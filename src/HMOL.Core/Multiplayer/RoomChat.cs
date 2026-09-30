using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>聊天发送结果。对应旧版 ChatChannel.send_text 的字符串返回：ok / empty / blocked / limited。</summary>
public enum ChatSendStatus
{
    Ok = 0,
    Empty = 1,
    Blocked = 2,
    Limited = 3
}

/// <summary>一条聊天消息。大厅聊天与房间聊天共用（旧版两处字典字段相同：ip / name / text / ts）。</summary>
public sealed record ChatMessage(string Ip, string Name, string Text, long Ts);

/// <summary>
/// 公开房间公告。房间聊天里 Id 是房主的虚拟 IP，大厅里 Id 是房主的会话 ID（旧版都放在字典的 'ip' 键上）。
/// LastSeen 是 Unix 秒，用于超时剔除。
/// </summary>
public sealed record RoomAnnouncement(
    string Id,
    string Name,
    string Community,
    string RoomIp,
    string Node,
    string Latency,
    double LastSeen);

/// <summary>
/// 房间聊天通道：绑定本地 UDP 端口，向在线对端的虚拟 IP 单播收发消息，并实现房主身份、
/// 踢人 / 拉回 / 公告协议。对应旧版 chat.py:33 <c>ChatChannel</c>。
///
/// <para>
/// <b>协议互通：</b>报文是 UTF-8 的 JSON 文本，键名与含义严格照旧版
/// （<c>t</c> 类型 / <c>c</c> 渠道 / <c>n</c> 昵称 / <c>s</c> 时间戳 / <c>x</c> 密文 / <c>ip</c> 目标 /
/// <c>tok</c> 房主令牌 / <c>com</c>+<c>rip</c>+<c>nid</c>+<c>l</c> 房间公告），
/// 因此新版与旧版客户端可以互通。改键名会直接破坏互通。
/// </para>
/// </summary>
public sealed class RoomChat : IAsyncDisposable
{
    /// <summary>
    /// 房间聊天 UDP 端口。chat.py 本身由调用方传端口，旧版主程序传入的是
    /// hall.py:38 的 <c>ROOM_UDP_PORT = 5567</c>（主程序 HMOL联机模块.py:1847 使用）。
    /// 必须与旧版一致才能互通。
    /// </summary>
    public const int RoomUdpPort = 5567;

    /// <summary>旧版大厅 UDP 端口（hall.py:37）；大厅已改为 MQTT，仅保留常量避免引用处报错。</summary>
    public const int HallUdpPort = 5566;

    /// <summary>心跳间隔（秒）。chat.py:20 <c>HB_INTERVAL</c>。</summary>
    public const double HeartbeatInterval = 8.0;

    /// <summary>对端心跳超时（秒）。chat.py:21 <c>PEER_TIMEOUT</c>。</summary>
    public const double PeerTimeout = 30.0;

    /// <summary>公开房间公告超时（秒）。chat.py:22 <c>ROOM_TIMEOUT</c>。</summary>
    public const double RoomTimeout = 15.0;

    /// <summary>发送限速窗口（秒）。chat.py:23 <c>SEND_WINDOW</c>。</summary>
    public const double SendWindow = 3.0;

    /// <summary>窗口内最多发送条数。chat.py:24 <c>SEND_MAX</c>。</summary>
    public const int SendMax = 5;

    /// <summary>接收限速窗口（秒）。chat.py:25 <c>RX_WINDOW</c>。</summary>
    public const double RxWindow = 2.0;

    /// <summary>窗口内单来源最多接收条数。chat.py:26 <c>RX_MAX</c>。</summary>
    public const int RxMax = 20;

    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 中文不转义，报文更短也便于抓包核对
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class PeerState
    {
        public string Name { get; set; } = string.Empty;

        public double LastSeen { get; set; }
    }

    private sealed record OwnerState(string Ip, string Tok);

    private readonly object _lock = new();
    private readonly int _port;
    private readonly string _channel;
    private readonly string _secret;
    private readonly string _bindIp;
    private readonly Action<string>? _log;
    private readonly string _ownerToken;
    private readonly HashSet<string> _banned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _bannedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoomAnnouncement> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Count, double WindowStart)> _rxCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<double> _sendTimes = new();

    private HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);
    private OwnerState? _owner;
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;

    /// <param name="port">本地 UDP 端口（房间聊天固定 5567）。</param>
    /// <param name="nickname">本机昵称。</param>
    /// <param name="channel">渠道名（房间聊天为 "room"），用于区分同一端口上的不同渠道。</param>
    /// <param name="secret">房间密钥；作为聊天密文的密钥，密钥不同则互相读不懂。</param>
    /// <param name="isOwner">本机是否房主（手动建房为真，通过分享文本/邀请加入为假）。</param>
    /// <param name="bindIp">绑定地址，默认 0.0.0.0。</param>
    /// <param name="log">日志回调。</param>
    public RoomChat(
        int port,
        string nickname,
        string channel,
        string secret,
        bool isOwner,
        string bindIp = "0.0.0.0",
        Action<string>? log = null)
    {
        _port = port;
        _channel = channel;
        _secret = secret ?? string.Empty;
        _bindIp = bindIp;
        _log = log;
        Nickname = nickname ?? string.Empty;
        IsOwner = isOwner;

        // 房主令牌：多房主冲突时按令牌大小协商，大者胜（旧版行为）
        _ownerToken = isOwner ? RandomToken(16) : string.Empty;
    }

    /// <summary>本机昵称（大厅进入后会同步）。</summary>
    public string Nickname { get; set; }

    /// <summary>本机是否房主。协商失败（对方令牌更大）时会自动降级为 false。</summary>
    public bool IsOwner { get; private set; }

    /// <summary>是否正在收发。</summary>
    public bool IsActive
    {
        get { lock (_lock) return _socket is not null; }
    }

    /// <summary>本机当前要公开的房间公告；为空表示不公开。</summary>
    public RoomAnnouncement? RoomAnnounce { get; private set; }

    /// <summary>当前认定的房主 IP；无人声明时为 null。</summary>
    public string? OwnerIp
    {
        get { lock (_lock) return _owner?.Ip; }
    }

    /// <summary>收到一条聊天消息。</summary>
    public event Action<ChatMessage>? MessageReceived;

    /// <summary>发现新对端（IP, 昵称）。</summary>
    public event Action<string, string>? PeerAnnounced;

    /// <summary>收到公开房间公告。</summary>
    public event Action<string, RoomAnnouncement>? RoomReceived;

    /// <summary>发送被限速。</summary>
    public event Action? SendRateLimited;

    /// <summary>本机房主身份被更高令牌的对端压制（参数为对方 IP）。</summary>
    public event Action<string>? OwnerDemoted;

    /// <summary>收到踢出广播（目标 IP, 昵称）。</summary>
    public event Action<string, string>? Kicked;

    /// <summary>收到解除踢出广播（目标 IP）。</summary>
    public event Action<string>? Unkicked;

    /// <summary>绑定端口并启动收发循环。</summary>
    public void Start()
    {
        CancellationToken token;

        lock (_lock)
        {
            if (_socket is not null) return;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Parse(_bindIp), _port));

            _socket = socket;
            _cts = new CancellationTokenSource();
            token = _cts.Token;
        }

        _receiveTask = Task.Run(() => ReceiveLoopAsync(token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(token));

        LogLine($"房间聊天已启动 (UDP {_port}, 渠道 {_channel}, {(IsOwner ? "房主" : "成员")})");
    }

    /// <summary>广播离线并停止收发。</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Socket? socket;

        lock (_lock)
        {
            if (_socket is null) return;
        }

        // 先广播离线，再关套接字
        try { Broadcast(Payload("l"), skipUnknown: false); }
        catch (Exception ex) { Log.Warn($"广播离线失败：{ex.Message}"); }

        lock (_lock)
        {
            cts = _cts;
            socket = _socket;
            _cts = null;
            _socket = null;
        }

        try { cts?.Cancel(); }
        catch { /* 已释放 */ }

        try { socket?.Close(); }
        catch { /* 已关闭 */ }

        var running = new[] { _receiveTask, _heartbeatTask }.Where(task => task is not null).Cast<Task>().ToArray();
        _receiveTask = null;
        _heartbeatTask = null;

        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* 收线程退出即可 */ }

        cts?.Dispose();

        lock (_lock)
        {
            _peers.Clear();
            _targets.Clear();
            _rooms.Clear();
            _banned.Clear();
            _bannedNames.Clear();
            _rxCount.Clear();
            _owner = null;
            RoomAnnounce = null;
        }
    }

    // ————— 对外操作 —————

    /// <summary>引擎侧最新的对端 IP 列表；新对端进入时立刻广播 announce。</summary>
    public void SetPeers(IEnumerable<string>? ips)
    {
        var targets = new HashSet<string>(
            (ips ?? []).Where(ip => !string.IsNullOrWhiteSpace(ip)).Select(ip => ip.Trim()),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> old;
        HashSet<string> banned;
        HashSet<string> alive;
        var now = UnixNow();

        lock (_lock)
        {
            old = new HashSet<string>(_targets, StringComparer.OrdinalIgnoreCase);
            _targets = targets;
            banned = new HashSet<string>(_banned, StringComparer.OrdinalIgnoreCase);
            alive = _peers.Where(pair => now - pair.Value.LastSeen <= PeerTimeout)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var ip in targets.Except(old))
        {
            if (!banned.Contains(ip)) Send(ip, Payload("a"));
        }

        // 对仍在线的已宣布对端补发 announce，保证昵称同步
        foreach (var ip in targets.Intersect(alive).Except(banned)) Send(ip, Payload("a"));
    }

    /// <summary>发送聊天文本（清洗 + 策略过滤 + 加密 + 限速）。</summary>
    public ChatSendStatus SendText(string? text)
    {
        var value = ChatCrypt.SanitizeText(text, ChatCrypt.MaxTextLength);
        if (value.Length == 0) return ChatSendStatus.Empty;

        if (!ChatCrypt.CheckPolicy(value).Ok) return ChatSendStatus.Blocked;

        var now = UnixNow();
        var limited = false;

        lock (_lock)
        {
            while (_sendTimes.Count > 0 && now - _sendTimes.Peek() > SendWindow) _sendTimes.Dequeue();

            if (_sendTimes.Count >= SendMax) limited = true;
            else _sendTimes.Enqueue(now);
        }

        if (limited)
        {
            try { SendRateLimited?.Invoke(); }
            catch (Exception ex) { Log.Warn($"限速回调异常：{ex.Message}"); }

            return ChatSendStatus.Limited;
        }

        Broadcast(Payload("m", ("x", ChatCrypt.Encrypt(_secret, value))));
        return ChatSendStatus.Ok;
    }

    /// <summary>房主身份声明：通过带令牌的心跳广播，多房主时按令牌大者协商。</summary>
    public void AnnounceOwner()
    {
        if (!IsOwner || _ownerToken.Length == 0 || !IsActive) return;

        Broadcast(Payload("h", ("tok", _ownerToken)));
    }

    /// <summary>房主踢人：广播踢出（含被踢者）并本机拉黑。</summary>
    public void Kick(string? ip, string? name = null)
    {
        var target = (ip ?? string.Empty).Trim();
        if (target.Length == 0 || !IsOwner) return;

        var safeName = ChatCrypt.SanitizeText(name, 32);

        AnnounceOwner();   // 先声明房主，保证成员能通过身份校验
        Broadcast(
            Payload("k", ("ip", target), ("n", safeName), ("tok", _ownerToken)),
            skipBanned: false,
            extra: [target]);

        lock (_lock)
        {
            _banned.Add(target);
            if (safeName.Length > 0) _bannedNames[target] = safeName;
            _peers.Remove(target);
            _targets.Remove(target);
        }
    }

    /// <summary>房主拉回：广播解除（含被移出者）并解除黑名单。</summary>
    public void Unkick(string? ip)
    {
        var target = (ip ?? string.Empty).Trim();
        if (target.Length == 0 || !IsOwner) return;

        AnnounceOwner();
        Broadcast(Payload("u", ("ip", target), ("tok", _ownerToken)), skipBanned: false, extra: [target]);

        lock (_lock)
        {
            _banned.Remove(target);
            _bannedNames.Remove(target);
        }
    }

    /// <summary>被移出房间的 [(IP, 昵称)] 列表，供房主「拉回」选择。</summary>
    public IReadOnlyList<(string Ip, string Name)> BannedPeers()
    {
        lock (_lock)
        {
            return _banned.OrderBy(ip => ip, StringComparer.Ordinal)
                .Select(ip => (ip, _bannedNames.TryGetValue(ip, out var name) ? name : string.Empty))
                .ToArray();
        }
    }

    /// <summary>该 IP 是否已被移出房间。</summary>
    public bool IsBanned(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;

        lock (_lock) return _banned.Contains(ip);
    }

    /// <summary>IP → 昵称 映射（含尚未宣布昵称的对端，用空串占位）。</summary>
    public IReadOnlyDictionary<string, string> PeerNames()
    {
        lock (_lock)
        {
            var names = _peers.ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var ip in _targets) names.TryAdd(ip, string.Empty);

            return names;
        }
    }

    /// <summary>在线对端数量（按引擎给的 IP 列表计）。</summary>
    public int PeerCount()
    {
        lock (_lock) return _targets.Count;
    }

    /// <summary>设置本机要公开的房间公告；心跳循环会周期性广播。</summary>
    public void SetRoomAnnounce(string? community, string? roomIp, string? node = null, string? latency = null)
    {
        RoomAnnounce = new RoomAnnouncement(
            string.Empty,
            Nickname,
            ChatCrypt.SanitizeText(community, 6),
            roomIp ?? string.Empty,
            node ?? string.Empty,
            latency ?? string.Empty,
            UnixNow());
    }

    /// <summary>取消公开房间。</summary>
    public void ClearRoomAnnounce() => RoomAnnounce = null;

    /// <summary>收到的公开房间列表（含超时清理）。</summary>
    public IReadOnlyList<RoomAnnouncement> Rooms()
    {
        lock (_lock)
        {
            var now = UnixNow();

            foreach (var ip in _rooms.Where(pair => now - pair.Value.LastSeen > RoomTimeout).Select(pair => pair.Key).ToArray())
                _rooms.Remove(ip);

            return _rooms.Values.ToArray();
        }
    }

    // ————— 收发 —————

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];

        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = _socket;
            if (socket is null) break;

            SocketReceiveFromResult result;

            try
            {
                result = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            catch (Exception ex)
            {
                Log.Warn($"房间聊天接收失败：{ex.Message}");
                continue;
            }

            try
            {
                HandleDatagram(result, buffer);
            }
            catch (Exception ex)
            {
                Log.Warn($"处理房间聊天报文失败：{ex.Message}");
            }
        }
    }

    private void HandleDatagram(SocketReceiveFromResult result, byte[] buffer)
    {
        using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.ReceivedBytes));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("c", out var channel) || channel.GetString() != _channel) return;

        var ip = (result.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        if (string.IsNullOrEmpty(ip)) return;

        lock (_lock)
        {
            // 被移出房间的成员消息一律丢弃
            if (_banned.Contains(ip)) return;
        }

        var name = ChatCrypt.SanitizeText(GetString(root, "n"), 32);
        var type = GetString(root, "t");
        var now = UnixNow();

        if (type == "m")
        {
            lock (_lock)
            {
                // 接收限速：单来源窗口内超过上限就丢弃，防刷屏
                _rxCount.TryGetValue(ip, out var window);

                if (window.Count == 0 || now - window.WindowStart > RxWindow) _rxCount[ip] = (1, now);
                else if (window.Count >= RxMax) return;
                else _rxCount[ip] = (window.Count + 1, window.WindowStart);
            }
        }

        var isNew = false;

        lock (_lock)
        {
            isNew = !_peers.TryGetValue(ip, out var state);
            state ??= new PeerState();
            if (name.Length > 0) state.Name = name;
            state.LastSeen = now;
            _peers[ip] = state;
        }

        switch (type)
        {
            case "a" when name.Length > 0:
                Send(ip, Payload("a"));
                if (isNew)
                {
                    try { PeerAnnounced?.Invoke(ip, name); }
                    catch (Exception ex) { Log.Warn($"对端回调异常：{ex.Message}"); }
                }

                break;

            case "h":
                HandleOwnerToken(ip, GetString(root, "tok"));
                break;

            case "k":
                HandleKick(ip, root);
                break;

            case "u":
                HandleUnkick(ip, root);
                break;

            case "m":
                var text = ChatCrypt.SanitizeText(ChatCrypt.Decrypt(_secret, GetString(root, "x")), ChatCrypt.MaxTextLength);
                if (text.Length == 0 || !ChatCrypt.CheckPolicy(text).Ok) return;

                var message = new ChatMessage(ip, name.Length > 0 ? name : ip, text, GetLong(root, "s", (long)now));
                try { MessageReceived?.Invoke(message); }
                catch (Exception ex) { Log.Warn($"消息回调异常：{ex.Message}"); }

                break;

            case "l":
                lock (_lock)
                {
                    _peers.Remove(ip);
                    _rooms.Remove(ip);
                    _rxCount.Remove(ip);
                }

                break;

            case "r":
                var community = GetString(root, "com");
                var roomIp = GetString(root, "rip");
                if (community.Length == 0 || roomIp.Length == 0) return;

                var room = new RoomAnnouncement(
                    ip,
                    name.Length > 0 ? name : ip,
                    community,
                    roomIp,
                    GetString(root, "nid"),
                    GetString(root, "l"),
                    now);

                lock (_lock) { _rooms[ip] = room; }

                try { RoomReceived?.Invoke(ip, room); }
                catch (Exception ex) { Log.Warn($"房间回调异常：{ex.Message}"); }

                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (IsOwner && _ownerToken.Length > 0) Broadcast(Payload("h", ("tok", _ownerToken)));
                else Broadcast(Payload("h"));

                var announce = RoomAnnounce;
                if (announce is not null)
                {
                    Broadcast(Payload(
                        "r",
                        ("com", announce.Community),
                        ("rip", announce.RoomIp),
                        ("nid", announce.Node),
                        ("l", announce.Latency)));
                }

                var now = UnixNow();

                lock (_lock)
                {
                    foreach (var ip in _peers.Where(pair => now - pair.Value.LastSeen > PeerTimeout).Select(pair => pair.Key).ToArray())
                    {
                        _peers.Remove(ip);
                        _rxCount.Remove(ip);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"房间聊天心跳失败：{ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(HeartbeatInterval), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>校验房主身份：消息来自记录的房主 IP 且令牌一致（防普通成员冒充）。</summary>
    private bool OwnerValid(string ip, JsonElement message)
    {
        OwnerState? owner;
        lock (_lock) owner = _owner;

        if (owner is null || owner.Ip != ip) return false;

        return owner.Tok == GetString(message, "tok");
    }

    /// <summary>收到他人心跳里的房主令牌：成员记录房主，房主之间令牌大者胜。</summary>
    private void HandleOwnerToken(string ip, string token)
    {
        if (token.Length == 0) return;

        OwnerState? current;
        lock (_lock) current = _owner;

        if (current is not null && current.Tok == token && current.Ip == ip) return;
        if (current is not null && string.CompareOrdinal(current.Tok, token) > 0) return;   // 已是更高令牌的房主

        lock (_lock) _owner = new OwnerState(ip, token);

        if (IsOwner && string.CompareOrdinal(token, _ownerToken) > 0)
        {
            IsOwner = false;   // 被压制，降级为成员

            try { OwnerDemoted?.Invoke(ip); }
            catch (Exception ex) { Log.Warn($"房主变更回调异常：{ex.Message}"); }
        }
    }

    private void HandleKick(string ip, JsonElement message)
    {
        if (!OwnerValid(ip, message)) return;

        var target = GetString(message, "ip");
        var name = ChatCrypt.SanitizeText(GetString(message, "n"), 32);
        if (target.Length == 0) return;

        lock (_lock)
        {
            _banned.Add(target);
            if (name.Length > 0) _bannedNames[target] = name;
            _peers.Remove(target);
            _targets.Remove(target);
        }

        try { Kicked?.Invoke(target, name); }
        catch (Exception ex) { Log.Warn($"踢出回调异常：{ex.Message}"); }
    }

    private void HandleUnkick(string ip, JsonElement message)
    {
        if (!OwnerValid(ip, message)) return;

        var target = GetString(message, "ip");
        if (target.Length == 0) return;

        lock (_lock)
        {
            _banned.Remove(target);
            _bannedNames.Remove(target);
        }

        try { Unkicked?.Invoke(target); }
        catch (Exception ex) { Log.Warn($"拉回回调异常：{ex.Message}"); }
    }

    // ————— 基础收发 —————

    private void Send(string ip, Dictionary<string, object?> payload)
    {
        var socket = _socket;
        if (socket is null || string.IsNullOrWhiteSpace(ip)) return;

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            socket.SendTo(bytes, new IPEndPoint(IPAddress.Parse(ip), _port));
        }
        catch (Exception ex)
        {
            // 送到日志而不是静默丢弃：对端地址不合法/网卡刚被移除时用户需要能看到原因
            LogLine($"发送到 {ip}:{_port} 失败：{ex.Message}");
        }
    }

    private void Broadcast(
        Dictionary<string, object?> payload,
        bool skipUnknown = true,
        bool skipBanned = true,
        IEnumerable<string>? extra = null)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            targets = skipUnknown
                ? new HashSet<string>(_peers.Keys, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(_targets, StringComparer.OrdinalIgnoreCase);

            if (skipBanned) targets.ExceptWith(_banned);
        }

        if (extra is not null) targets.UnionWith(extra);

        foreach (var ip in targets) Send(ip, payload);
    }

    private Dictionary<string, object?> Payload(string type, params (string Key, object? Value)[] extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["t"] = type,
            ["c"] = _channel,
            ["n"] = Nickname,
            ["s"] = (long)UnixNow()
        };

        foreach (var (key, value) in extra) payload[key] = value;

        return payload;
    }

    internal static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    internal static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal static long GetLong(JsonElement element, string name, long fallback)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => fallback
        };
    }

    internal static string RandomToken(int length)
        => string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
        });

    private void LogLine(string message)
    {
        try { _log?.Invoke($"[房间] {message}"); }
        catch { /* 日志回调异常不影响业务 */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
