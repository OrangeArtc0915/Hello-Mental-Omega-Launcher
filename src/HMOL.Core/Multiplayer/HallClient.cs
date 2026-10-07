using System.Net.Http;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>大厅在线玩家（对应旧版 poll_peers 返回的字典）。</summary>
public sealed record HallPeer(string Sid, string Ipv4, string Hostname, string Latency);

/// <summary>发起入房邀请时要带的信息。</summary>
public sealed record HallInviteRequest(
    string Community,
    string Node,
    string Key,
    NetworkEngineKind Kind,
    NetworkAddressMode AddressMode,
    string ManualIp,
    string Version);

/// <summary>收到的入房邀请。</summary>
public sealed record HallInvite(
    string Sid,
    string Name,
    string Community,
    string Node,
    string Key,
    NetworkEngineKind Kind,
    NetworkAddressMode AddressMode,
    string ManualIp,
    string Version,
    long Ts);

/// <summary>
/// 大厅聊天 / 公告通道（走 MQTT，不再依赖虚拟网卡）。对应旧版 hall.py:45 <c>_HallChat</c>。
/// 公开接口与旧版一致，便于界面直接搬过来。
/// </summary>
public sealed class HallChat
{
    private sealed class PeerState
    {
        public string Name { get; set; } = string.Empty;

        public string Ip { get; set; } = string.Empty;

        public double LastSeen { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _sessionId;
    private readonly string _secret;
    private readonly Action<string, Dictionary<string, object?>> _publish;
    private readonly Action<ChatMessage>? _onMessage;
    private readonly Action<string, RoomAnnouncement>? _onRoom;
    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoomAnnouncement> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<double> _sendTimes = new();

    internal HallChat(
        string sessionId,
        string nickname,
        string secret,
        Action<string, Dictionary<string, object?>> publish,
        Action<ChatMessage>? onMessage,
        Action<string, RoomAnnouncement>? onRoom)
    {
        _sessionId = sessionId;
        _secret = secret;
        _publish = publish;
        _onMessage = onMessage;
        _onRoom = onRoom;
        Nickname = nickname;
    }

    /// <summary>本机昵称。</summary>
    public string Nickname { get; set; }

    /// <summary>本机要公开的房间公告；为空表示不公开。</summary>
    public RoomAnnouncement? RoomAnnounce { get; private set; }

    /// <summary>是否处于活动状态。</summary>
    public bool IsActive { get; private set; }

    public void Start() => IsActive = true;

    public void Stop()
    {
        IsActive = false;

        lock (_lock)
        {
            _peers.Clear();
            _rooms.Clear();
        }

        RoomAnnounce = null;
    }

    /// <summary>发送大厅聊天（清洗 + 策略过滤 + 混淆 + 限速）。</summary>
    public ChatSendStatus SendText(string? text)
    {
        var value = ChatCrypt.SanitizeText(text, ChatCrypt.MaxTextLength);
        if (value.Length == 0) return ChatSendStatus.Empty;

        if (!ChatCrypt.CheckPolicy(value).Ok) return ChatSendStatus.Blocked;

        var now = RoomChat.UnixNow();

        lock (_lock)
        {
            while (_sendTimes.Count > 0 && now - _sendTimes.Peek() > RoomChat.SendWindow) _sendTimes.Dequeue();

            if (_sendTimes.Count >= RoomChat.SendMax) return ChatSendStatus.Limited;

            _sendTimes.Enqueue(now);
        }

        _publish(HallClient.ChatTopic, new Dictionary<string, object?>
        {
            ["t"] = "m",
            ["id"] = _sessionId,
            ["n"] = Nickname,
            ["x"] = ChatCrypt.Encrypt(_secret, value),
            ["s"] = (long)now
        });

        return ChatSendStatus.Ok;
    }

    /// <summary>会话 ID → 昵称。</summary>
    public IReadOnlyDictionary<string, string> PeerNames()
    {
        lock (_lock) return _peers.ToDictionary(pair => pair.Key, pair => pair.Value.Name);
    }

    /// <summary>在线玩家数（含本机）。</summary>
    public int PeerCount()
    {
        lock (_lock) return _peers.Count;
    }

    /// <summary>设置本机要公开的房间公告；心跳循环会周期性发布。</summary>
    public void SetRoomAnnounce(string? community, string? roomIp, string? node = null, string? latency = null)
    {
        RoomAnnounce = new RoomAnnouncement(
            _sessionId,
            Nickname,
            ChatCrypt.SanitizeText(community, 6),
            roomIp ?? string.Empty,
            node ?? string.Empty,
            latency ?? string.Empty,
            RoomChat.UnixNow());
    }

    public void ClearRoomAnnounce() => RoomAnnounce = null;

    /// <summary>大厅内收到的公开房间列表（含超时清理）。</summary>
    public IReadOnlyList<RoomAnnouncement> Rooms()
    {
        lock (_lock)
        {
            var now = RoomChat.UnixNow();

            foreach (var id in _rooms
                         .Where(pair => now - pair.Value.LastSeen > RoomChat.RoomTimeout)
                         .Select(pair => pair.Key)
                         .ToArray())
                _rooms.Remove(id);

            return _rooms.Values.ToArray();
        }
    }

    /// <summary>超时剔除心跳过期的玩家，同时清掉他公开的房间。</summary>
    internal void Prune()
    {
        var now = RoomChat.UnixNow();

        lock (_lock)
        {
            foreach (var id in _peers
                         .Where(pair => now - pair.Value.LastSeen > RoomChat.PeerTimeout)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _peers.Remove(id);
                _rooms.Remove(id);
            }
        }
    }

    /// <summary>兼容旧接口：MQTT 模式下对端由 presence 维护，无需引擎列表。</summary>
    public void SetPeers(IEnumerable<string>? _ = null)
    {
    }

    internal void HandlePresence(string id, string name, string encryptedIp, string plainIp)
    {
        var ip = ChatCrypt.Decrypt(HallClient.HallChatSecret, encryptedIp);

        // 兼容旧版明文 presence
        if (ip.Length == 0) ip = plainIp.Length > 45 ? plainIp[..45] : plainIp;

        ip = ChatCrypt.MaskIp(ip);
        var now = RoomChat.UnixNow();

        lock (_lock)
        {
            if (!_peers.TryGetValue(id, out var state))
            {
                state = new PeerState();
                _peers[id] = state;
            }

            if (name.Length > 0) state.Name = name;
            if (ip.Length > 0) state.Ip = ip;
            state.LastSeen = now;
        }
    }

    internal void HandleGone(string id)
    {
        lock (_lock)
        {
            _peers.Remove(id);
            _rooms.Remove(id);
        }
    }

    internal void HandleChat(string id, string name, string encrypted, long timestamp)
    {
        if (_onMessage is null) return;

        var text = ChatCrypt.SanitizeText(ChatCrypt.Decrypt(_secret, encrypted), ChatCrypt.MaxTextLength);
        if (text.Length == 0 || !ChatCrypt.CheckPolicy(text).Ok) return;   // 无法解密或含违禁内容，丢弃

        _onMessage(new ChatMessage(id, name.Length > 0 ? name : Loc.T("未知"), text, timestamp));
    }

    internal void HandleRoom(string id, string name, string community, string roomIp, string node, string latency)
    {
        if (id.Length == 0 || community.Length == 0 || roomIp.Length == 0) return;

        var room = new RoomAnnouncement(
            id,
            name.Length > 0 ? name : id,
            community,
            roomIp,
            node,
            latency,
            RoomChat.UnixNow());

        lock (_lock) _rooms[id] = room;

        _onRoom?.Invoke(id, room);
    }

    /// <summary>供大厅心跳读取：会话 ID → 最近活跃时间与展示信息。</summary>
    internal IReadOnlyDictionary<string, (string Name, string Ip, double LastSeen)> SnapshotPeers()
    {
        lock (_lock)
        {
            return _peers.ToDictionary(
                pair => pair.Key,
                pair => (pair.Value.Name, pair.Value.Ip, pair.Value.LastSeen),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
/// 联机大厅：连接公共 MQTT broker，维护在线玩家 / 公告聊天 / 公开房间 / 入房邀请。
/// 对应旧版 hall.py:195 <c>Hall</c>。
///
/// <para>
/// <b>用的是第三方公共服务：</b><c>broker.emqx.io</c> 与 <c>test.mosquitto.org</c> 都是公开 MQTT broker，
/// 任何人都能订阅同样的主题，因此大厅聊天内容对第三方可见（旧版已用内置共享密钥做传输混淆，
/// 但这只能挡住「不持密钥的旁观者」，不能当作保密）。请勿在大厅发送敏感信息。
/// </para>
///
/// <para>
/// <b>协议互通：</b>主题与载荷字段严格照旧版（<c>hmol/hall/v1/presence|chat|room|invite/&lt;会话ID&gt;</c>，
/// 载荷 <c>t</c> 取值 p/g/m/r/i），因此新版与旧版客户端同处一个大厅。
/// </para>
/// </summary>
public sealed class HallClient : IAsyncDisposable
{
    /// <summary>大厅主题前缀（hall.py:26 <c>HALL_PREFIX</c>）。</summary>
    public const string HallPrefix = "hmol/hall/v1";

    /// <summary>在线状态主题（hall.py:27 <c>PUB_TOPIC</c>）。</summary>
    public const string PubTopic = HallPrefix + "/presence";

    /// <summary>大厅聊天主题（hall.py:28 <c>CHAT_TOPIC</c>）。</summary>
    public const string ChatTopic = HallPrefix + "/chat";

    /// <summary>公开房间主题（hall.py:29 <c>ROOM_TOPIC</c>）。</summary>
    public const string RoomTopic = HallPrefix + "/room";

    /// <summary>私信邀请主题前缀（hall.py:30 <c>INVITE_TOPIC</c>）；每个会话订阅自己的 <c>{前缀}/{会话ID}</c>。</summary>
    public const string InviteTopicPrefix = HallPrefix + "/invite";

    /// <summary>
    /// 大厅聊天共享密钥（hall.py:34 <c>HALL_CHAT_SECRET</c>）：所有客户端内置同一份，
    /// 用于对公共 MQTT 流量做传输混淆，防第三方直接窥探明文；密钥不随消息传输（与旧版同一把，故新旧互通）。
    /// </summary>
    public const string HallChatSecret = "hmol-hall-v1-9f8e7d6c";

    /// <summary>公共 broker 列表（hall.py:25 <c>HALL_BROKERS</c>，第三方公共服务，失败自动切换下一个）。</summary>
    public static IReadOnlyList<(string Host, int Port)> Brokers { get; } =
    [
        ("broker.emqx.io", 1883),
        ("test.mosquitto.org", 1883)
    ];

    /// <summary>邀请限速窗口（秒）。hall.py:391。</summary>
    private const int InviteWindowSeconds = 10;

    /// <summary>窗口内最多邀请次数。hall.py:393。</summary>
    private const int InviteMax = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly object _lock = new();
    private readonly Action<string>? _log;
    private readonly Queue<double> _inviteTimes = new();
    private readonly List<HallPeer> _peersCache = [];

    private IMqttClient? _client;
    private HallChat? _chat;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private bool _sessionActive;
    private bool _reconnectFailureLogged;
    private volatile bool _connected;
    private string _nick = string.Empty;
    private string _publicIp = string.Empty;

    public HallClient(Action<string>? log = null)
    {
        _log = log;

        // 会话 ID：旧版为 'p' + 11 位小写字母数字，用于在公共 broker 上互相区分
        SessionId = "p" + RandomToken(11).ToLowerInvariant();
    }

    /// <summary>本机会话 ID。</summary>
    public string SessionId { get; }

    /// <summary>是否已连上大厅信标。</summary>
    public bool Connected => _connected;

    /// <summary>公网 IP（对外只给脱敏值）。</summary>
    public string Ip => ChatCrypt.MaskIp(_publicIp);

    /// <summary>大厅聊天通道；未进入大厅时为 null。</summary>
    public HallChat? Chat => _chat;

    /// <summary>收到大厅聊天消息。</summary>
    public event Action<ChatMessage>? MessageReceived;

    /// <summary>收到公开房间公告（会话 ID, 房间）。</summary>
    public event Action<string, RoomAnnouncement>? RoomUpdated;

    /// <summary>收到入房邀请。</summary>
    public event Action<HallInvite>? InviteReceived;

    /// <summary>设置本机昵称（已进入大厅时会立刻重新广播在线状态）。</summary>
    public void SetNickname(string? name)
    {
        _nick = ChatCrypt.SanitizeText(name, 32);

        if (_chat is not null) _chat.Nickname = _nick;

        if (_connected) PublishPresence();
    }

    /// <summary>进入大厅：连接公共 MQTT broker（失败自动切换）。成功返回 true。</summary>
    public async Task<bool> JoinAsync(
        string nickname,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        SetNickname(nickname);

        if (_connected) return true;

        if (_sessionActive) return false;

        _sessionActive = true;
        _chat = new HallChat(
            SessionId,
            _nick,
            HallChatSecret,
            Publish,
            message =>
            {
                try { MessageReceived?.Invoke(message); }
                catch { /* 回调异常不影响收发 */ }
            },
            (id, room) =>
            {
                try { RoomUpdated?.Invoke(id, room); }
                catch { /* 同上 */ }
            });

        progress?.Report(Loc.T("正在获取公网 IP..."));
        _publicIp = await FetchPublicIpAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(Loc.T("正在连接大厅信标..."));
        var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            LogLine(Loc.T("所有大厅信标均不可用, 进入大厅失败"));
            _chat = null;
            _sessionActive = false;
            return false;
        }

        _cts = new CancellationTokenSource();
        _chat.Start();
        PublishPresence();
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

        LogLine(Loc.F("已进入大厅, 公网IP={0}", (Ip.Length > 0 ? Ip : Loc.T("-"))));
        return true;
    }

    /// <summary>离开大厅：广播离线并断开信标（broker 遗嘱兜底异常退出）。</summary>
    public async Task LeaveAsync()
    {
        if (_connected)
        {
            Publish(PubTopic, new Dictionary<string, object?> { ["t"] = "g", ["id"] = SessionId });
        }

        _sessionActive = false;

        try { _chat?.Stop(); }
        catch { /* 忽略 */ }

        var cts = _cts;
        var client = _client;
        var heartbeat = _heartbeatTask;

        _cts = null;
        _heartbeatTask = null;
        _client = null;
        _connected = false;

        try { cts?.Cancel(); }
        catch { /* 已释放 */ }

        if (heartbeat is not null)
        {
            try { await heartbeat.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 心跳线程退出即可 */ }
        }

        cts?.Dispose();

        if (client is not null)
        {
            try
            {
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogLine(Loc.F("断开大厅信标失败：{0}", ex.Message));
            }

            try { client.Dispose(); }
            catch { /* 忽略 */ }
        }

        lock (_lock)
        {
            _peersCache.Clear();
        }

        _chat = null;
        LogLine(Loc.T("已离开大厅"));
    }

    /// <summary>查询大厅在线玩家（已排除本机），同时刷新缓存。</summary>
    public IReadOnlyList<HallPeer> PollPeers()
    {
        var chat = _chat;

        if (!_connected || chat is null)
        {
            lock (_lock) return _peersCache.ToArray();
        }

        var now = RoomChat.UnixNow();
        var peers = new List<HallPeer>();

        foreach (var (id, info) in chat.SnapshotPeers())
        {
            if (id == SessionId) continue;

            var name = info.Name.Length > 0 ? info.Name : id[..Math.Min(8, id.Length)];
            var seconds = Math.Max(0, (int)(now - info.LastSeen));
            var latency = seconds < 60 ? Loc.F("{0}秒", seconds) : Loc.F("{0}分{1}秒", seconds / 60, seconds % 60);

            peers.Add(new HallPeer(id, info.Ip.Length > 0 ? info.Ip : "-", name, latency));
        }

        lock (_lock)
        {
            _peersCache.Clear();
            _peersCache.AddRange(peers);
            return _peersCache.ToArray();
        }
    }

    /// <summary>上次查询结果的缓存。</summary>
    public IReadOnlyList<HallPeer> PeersCached()
    {
        lock (_lock) return _peersCache.ToArray();
    }

    /// <summary>
    /// 向指定大厅玩家发送入房邀请；限速 5 次 / 10 秒，失败返回 false。
    /// <c>Version</c> 为发送方版本号，供接收方校验版本一致。
    /// </summary>
    public bool SendInvite(string targetSid, HallInviteRequest request)
    {
        var client = _client;
        if (!_connected || client is null) return false;

        var target = (targetSid ?? string.Empty).Trim();
        if (target.Length == 0 || target == SessionId) return false;

        var now = RoomChat.UnixNow();

        lock (_lock)
        {
            while (_inviteTimes.Count > 0 && now - _inviteTimes.Peek() > InviteWindowSeconds) _inviteTimes.Dequeue();

            if (_inviteTimes.Count >= InviteMax) return false;

            _inviteTimes.Enqueue(now);
        }

        Publish($"{InviteTopicPrefix}/{target}", new Dictionary<string, object?>
        {
            ["t"] = "i",
            ["id"] = SessionId,
            ["n"] = _chat?.Nickname ?? _nick,
            ["com"] = Truncate(request.Community, 6),
            ["node"] = request.Node,
            ["key"] = request.Key,
            ["plan"] = NetworkEngineFactory.ToPlan(request.Kind),
            ["ipm"] = request.AddressMode == NetworkAddressMode.Manual ? "manual" : "auto",
            ["mip"] = request.ManualIp,
            ["ver"] = request.Version,
            ["s"] = (long)now
        });

        return true;
    }

    // ————— 连接 —————

    private async Task<IMqttClient?> ConnectAsync(CancellationToken cancellationToken)
    {
        foreach (var (host, port) in Brokers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IMqttClient? client = null;

            try
            {
                client = new MqttFactory().CreateMqttClient();

                var will = JsonSerializer.Serialize(
                    new Dictionary<string, object?> { ["t"] = "g", ["id"] = SessionId },
                    RoomChat.JsonOptions);

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(host, port)
                    .WithClientId($"hmol_hall_{SessionId}")
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                    .WithCleanSession()
                    .WithWillTopic(PubTopic)
                    .WithWillPayload(will)
                    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .WithWillRetain(false)
                    .Build();

                client.ConnectedAsync += OnConnectedAsync;
                client.DisconnectedAsync += OnDisconnectedAsync;
                client.ApplicationMessageReceivedAsync += OnApplicationMessageAsync;

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(25));

                await client.ConnectAsync(options, timeoutSource.Token).ConfigureAwait(false);

                await client.SubscribeAsync(PubTopic, MqttQualityOfServiceLevel.AtMostOnce, cancellationToken).ConfigureAwait(false);
                await client.SubscribeAsync(ChatTopic, MqttQualityOfServiceLevel.AtMostOnce, cancellationToken).ConfigureAwait(false);
                await client.SubscribeAsync(RoomTopic, MqttQualityOfServiceLevel.AtMostOnce, cancellationToken).ConfigureAwait(false);
                await client.SubscribeAsync($"{InviteTopicPrefix}/{SessionId}", MqttQualityOfServiceLevel.AtMostOnce, cancellationToken)
                    .ConfigureAwait(false);

                _client = client;
                _connected = true;
                _reconnectFailureLogged = false;
                LogLine(Loc.F("已连接大厅信标 {0}:{1}", host, port));
                return client;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { client?.Dispose(); } catch { /* 忽略 */ }
                throw;
            }
            catch (Exception ex)
            {
                _connected = false;
                LogLine(Loc.F("信标 {0}:{1} 连接失败：{2}", host, port, ex.Message));

                try { client?.Dispose(); }
                catch { /* 忽略 */ }
            }
        }

        return null;
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _connected = false;
        LogLine(Loc.T("大厅信标连接断开, 等待自动重连"));
        return Task.CompletedTask;
    }

    private Task OnApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            HandlePayload(args.ApplicationMessage.ConvertPayloadToString());
        }
        catch (Exception ex)
        {
            LogLine(Loc.F("处理大厅消息失败：{0}", ex.Message));
        }

        return Task.CompletedTask;
    }

    private void HandlePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) return;

        var id = RoomChat.GetString(root, "id");

        // MQTT 会把消息回发给订阅者，忽略自己发布的消息
        if (id.Length == 0 || id == SessionId) return;

        var chat = _chat;

        switch (RoomChat.GetString(root, "t"))
        {
            case "p":
                chat?.HandlePresence(id, RoomChat.GetString(root, "n"), RoomChat.GetString(root, "x"), RoomChat.GetString(root, "ip"));
                break;

            case "g":
                chat?.HandleGone(id);
                break;

            case "m":
                chat?.HandleChat(
                    id,
                    RoomChat.GetString(root, "n"),
                    RoomChat.GetString(root, "x"),
                    RoomChat.GetLong(root, "s", (long)RoomChat.UnixNow()));
                break;

            case "r":
                chat?.HandleRoom(
                    id,
                    RoomChat.GetString(root, "n"),
                    RoomChat.GetString(root, "com"),
                    RoomChat.GetString(root, "rip"),
                    RoomChat.GetString(root, "nid"),
                    RoomChat.GetString(root, "l"));
                break;

            case "i":
                HandleInvite(id, root);
                break;
        }
    }

    private void HandleInvite(string id, JsonElement root)
    {
        if (InviteReceived is null) return;

        var community = RoomChat.GetString(root, "com");
        var node = RoomChat.GetString(root, "node");

        // 缺房间名/节点无法加入，直接丢弃
        if (community.Length == 0 || node.Length == 0) return;

        var name = RoomChat.GetString(root, "n");
        var addressMode = RoomChat.GetString(root, "ipm");

        var invite = new HallInvite(
            id,
            name.Length > 0 ? name : Loc.T("玩家"),
            community,
            node,
            RoomChat.GetString(root, "key"),
            NetworkEngineFactory.ParseKind(RoomChat.GetString(root, "plan")),
            addressMode == "manual" ? NetworkAddressMode.Manual : NetworkAddressMode.Auto,
            RoomChat.GetString(root, "mip"),
            RoomChat.GetString(root, "ver"),
            RoomChat.GetLong(root, "s", (long)RoomChat.UnixNow()));

        try { InviteReceived(invite); }
        catch (Exception ex)
        {
            LogLine(Loc.F("邀请回调异常：{0}", ex.Message));
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_connected && _sessionActive) await ReconnectAsync(cancellationToken).ConfigureAwait(false);

                if (_connected)
                {
                    PublishPresence();

                    var chat = _chat;
                    var announce = chat?.RoomAnnounce;

                    if (chat is not null && announce is not null)
                    {
                        Publish(RoomTopic, new Dictionary<string, object?>
                        {
                            ["t"] = "r",
                            ["id"] = SessionId,
                            ["n"] = chat.Nickname,
                            ["com"] = announce.Community,
                            ["rip"] = announce.RoomIp,
                            ["nid"] = announce.Node,
                            ["l"] = announce.Latency,
                            ["s"] = (long)RoomChat.UnixNow()
                        });
                    }

                    chat?.Prune();
                }
            }
            catch (Exception ex)
            {
                LogLine(Loc.F("大厅心跳失败：{0}", ex.Message));
            }

            try { await Task.Delay(TimeSpan.FromSeconds(RoomChat.HeartbeatInterval), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>断线自动重连（旧版由 paho 的 loop 自动完成）。</summary>
    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        var old = _client;
        _client = null;

        try { old?.Dispose(); }
        catch { /* 忽略 */ }

        var client = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (client is null)
        {
            if (!_reconnectFailureLogged)
            {
                _reconnectFailureLogged = true;
                LogLine(Loc.T("大厅信标重连失败, 将继续重试"));
            }

            return;
        }

        _reconnectFailureLogged = false;
        LogLine(Loc.T("已重新连接大厅信标"));
    }

    private async Task<string> FetchPublicIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ip = (await Http.GetStringAsync("https://api.ipify.org", cancellationToken).ConfigureAwait(false)).Trim();
            return ip.Length > 0 && !ip.Contains(' ') ? ip : string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLine(Loc.F("取公网 IP 失败（不影响大厅使用）：{0}", ex.Message));
            return string.Empty;
        }
    }

    private void PublishPresence()
    {
        Publish(PubTopic, new Dictionary<string, object?>
        {
            ["t"] = "p",
            ["id"] = SessionId,
            ["n"] = _chat?.Nickname ?? _nick,
            ["x"] = ChatCrypt.Encrypt(HallChatSecret, _publicIp),
            ["s"] = (long)RoomChat.UnixNow()
        });
    }

    private void Publish(string topic, Dictionary<string, object?> payload)
    {
        var client = _client;

        if (!_connected || client is null) return;

        try
        {
            var json = JsonSerializer.Serialize(payload, RoomChat.JsonOptions);
            PublishCoreAsync(client, topic, json);
        }
        catch (Exception ex)
        {
            LogLine(Loc.F("发布大厅消息失败：{0}", ex.Message));
        }
    }

    private async void PublishCoreAsync(IMqttClient client, string topic, string json)
    {
        try
        {
            await client.PublishStringAsync(topic, json, MqttQualityOfServiceLevel.AtMostOnce).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogLine(Loc.F("发布大厅消息失败：{0}", ex.Message));
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string RandomToken(int length)
        => RoomChat.RandomToken(length);

    private void LogLine(string message)
    {
        try { _log?.Invoke(Loc.F("[大厅] {0}", message)); }
        catch { /* 日志回调异常不影响业务 */ }
    }

    public async ValueTask DisposeAsync() => await LeaveAsync().ConfigureAwait(false);
}
