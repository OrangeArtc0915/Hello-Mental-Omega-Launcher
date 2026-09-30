using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>联机会话状态。</summary>
public enum SessionState
{
    /// <summary>未连接。</summary>
    Idle = 0,

    /// <summary>正在连接（启动引擎 / 等虚拟 IP）。</summary>
    Connecting = 1,

    /// <summary>已连接。</summary>
    Connected = 2,

    /// <summary>正在断开。</summary>
    Disconnecting = 3,

    /// <summary>连接失败。</summary>
    Failed = 4
}

/// <summary>会话里的一台对端：引擎给的 IP / 延迟 / 状态，名称优先取房间聊天里的昵称。</summary>
public sealed record SessionPeer(string Name, string Ip, string Latency, string Status);

/// <summary>
/// 一次联机会话的编排门面：选引擎 → 启动 → 建房间聊天 → 同步对端 → 挂进程守护 → 断开清理。
/// 对应旧版 HMOL联机模块.py:1769 <c>_connect_worker</c> + :1468 <c>_peer_poller</c> + :1940 <c>_ensure_guard</c>。
///
/// <para>
/// 界面只订阅事件、读属性，<b>不需要自己轮询</b>：对端列表与节点信息由本类内部循环刷新后推送
/// （<see cref="PeersChanged"/>），引擎输出逐行推送（<see cref="OutputReceived"/>）。
/// 所有事件都可能来自后台线程，界面自行切回 UI 线程。
/// </para>
/// </summary>
public sealed class MultiplayerSession : IAsyncDisposable
{
    /// <summary>引擎输出缓存上限。</summary>
    private const int OutputKeep = 300;

    /// <summary>对端轮询间隔（秒）。旧版 _peer_poller 为 3 秒。</summary>
    private const double PeerPollSeconds = 3.0;

    private readonly object _lock = new();
    private readonly List<string> _output = [];
    private readonly List<SessionPeer> _peers = [];

    private readonly Action<string>? _log;

    /// <summary>串行化连接 / 断开 / 守护重启，避免状态互相踩。</summary>
    private readonly SemaphoreSlim _transition = new(1, 1);

    private INetworkEngine? _engine;
    private RoomChat? _chat;
    private ProcessGuard? _guard;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private NetworkSessionOptions? _options;

    private SessionState _state = SessionState.Idle;
    private string _status = "未连接";
    private string _lastError = string.Empty;
    private EngineNodeInfo _nodeInfo = new(null, null, null, null);
    private bool _guardEnabled = true;
    private bool _disposed;

    public MultiplayerSession(Action<string>? log = null) => _log = log;

    // ————— 事件 —————

    /// <summary>状态 / 虚拟 IP / 节点信息变化。</summary>
    public event Action? StateChanged;

    /// <summary>对端列表变化。</summary>
    public event Action? PeersChanged;

    /// <summary>引擎每输出一行。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>收到房间聊天消息。</summary>
    public event Action<ChatMessage>? ChatMessageReceived;

    /// <summary>新对端宣布昵称（IP, 昵称）。</summary>
    public event Action<string, string>? PeerAnnounced;

    /// <summary>面向用户的中文提示（连接失败原因、被踢、房主降级、守护停止等）。</summary>
    public event Action<string>? Notice;

    // ————— 状态 —————

    /// <summary>当前状态。</summary>
    public SessionState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>可直接展示的中文状态文本。</summary>
    public string StatusText
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>最近一次失败原因；无失败时为空串。</summary>
    public string LastError
    {
        get { lock (_lock) return _lastError; }
    }

    public bool IsConnected => State == SessionState.Connected;

    /// <summary>连接或断开过程中。</summary>
    public bool IsBusy => State is SessionState.Connecting or SessionState.Disconnecting;

    /// <summary>本地虚拟 IP；未就绪时为 null。</summary>
    public string? LocalIp => _engine?.LocalIp;

    /// <summary>当前引擎类型；未连接时为 null。</summary>
    public NetworkEngineKind? EngineKind => _engine?.Kind;

    /// <summary>本次会话的房间名。</summary>
    public string RoomName
    {
        get { lock (_lock) return _options?.RoomName ?? string.Empty; }
    }

    /// <summary>本机是否房主（多房主冲突时 chat 会自动降级）。</summary>
    public bool IsOwner => _chat?.IsOwner ?? false;

    /// <summary>当前认定的房主虚拟 IP。</summary>
    public string? OwnerIp => _chat?.OwnerIp;

    /// <summary>房间聊天是否在收发。</summary>
    public bool IsRoomChatActive => _chat?.IsActive ?? false;

    /// <summary>本节点信息（虚拟 IP / PeerID / 公网 IP / NAT 类型）。</summary>
    public EngineNodeInfo NodeInfo
    {
        get { lock (_lock) return _nodeInfo; }
    }

    /// <summary>引擎最近输出（最后 40 行），连接失败时用于诊断。</summary>
    public string RecentOutput => _engine?.RecentOutput ?? string.Empty;

    /// <summary>本会话缓存的引擎输出（最多 <see cref="OutputKeep"/> 行）。</summary>
    public IReadOnlyList<string> OutputLines
    {
        get { lock (_lock) return [.. _output]; }
    }

    /// <summary>当前对端列表（已排除被移出房间的成员）。</summary>
    public IReadOnlyList<SessionPeer> Peers
    {
        get { lock (_lock) return [.. _peers]; }
    }

    /// <summary>被移出房间的玩家 [(IP, 昵称)]，供房主「拉回」选择。</summary>
    public IReadOnlyList<(string Ip, string Name)> BannedPeers()
        => _chat?.BannedPeers() ?? [];

    /// <summary>进程守护是否处于开启状态。</summary>
    public bool GuardEnabled
    {
        get => _guard?.Enabled ?? _guardEnabled;
        set
        {
            _guardEnabled = value;
            try { _guard?.SetEnabled(value); }
            catch (Exception ex) { Log.Warn($"切换进程守护失败：{ex.Message}"); }
        }
    }

    // ————— 连接 / 断开 —————

    /// <summary>
    /// 启动引擎并建立会话。返回 false 时 <see cref="StatusText"/> / <see cref="LastError"/> 是中文原因。
    /// </summary>
    public async Task<bool> ConnectAsync(
        NetworkEngineKind kind,
        NetworkSessionOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 已有会话（含失败残留）先彻底清干净，避免端口/网卡残留
            await TeardownAsync().ConfigureAwait(false);

            lock (_lock)
            {
                _options = options;
                _lastError = string.Empty;
            }

            SetState(SessionState.Connecting, $"正在启动 {NetworkEngineFactory.DisplayName(kind)} 引擎…");

            var engine = NetworkEngineFactory.Create(kind, _log);
            engine.OutputReceived += OnEngineOutput;
            _engine = engine;

            EngineStartResult result;

            try
            {
                result = await engine.StartAsync(options, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TeardownAsync().ConfigureAwait(false);
                SetState(SessionState.Idle, "连接已取消");
                return false;
            }

            if (!result.Ok)
            {
                var reason = result.Message;
                var tail = engine.RecentOutput;
                await TeardownAsync().ConfigureAwait(false);

                lock (_lock) _lastError = reason;
                SetState(SessionState.Failed, $"连接失败：{reason}");

                // 失败时把引擎最近输出打给日志，便于定位（旧版 _dump_engine_log）
                if (!string.IsNullOrWhiteSpace(tail)) _log?.Invoke(tail);

                RaiseNotice(reason);
                return false;
            }

            // 房间聊天：密钥沿用旧版行为（用房间名），改掉会破坏与旧客户端的互通
            var chat = new RoomChat(
                RoomChat.RoomUdpPort,
                options.Nickname,
                "room",
                options.RoomName,
                options.IsOwner,
                log: _log);

            chat.MessageReceived += message => Raise(ChatMessageReceived, message);
            chat.PeerAnnounced += (ip, name) => Raise(PeerAnnounced, ip, name);
            chat.OwnerDemoted += OnOwnerDemoted;
            chat.Kicked += OnKicked;
            chat.Unkicked += OnUnkicked;

            _chat = chat;

            try
            {
                chat.Start();
            }
            catch (Exception ex)
            {
                await TeardownAsync().ConfigureAwait(false);
                lock (_lock) _lastError = $"房间聊天端口 {RoomChat.RoomUdpPort} 占用：{ex.Message}";
                SetState(SessionState.Failed, $"连接失败：房间聊天端口被占用（{ex.Message}）");
                return false;
            }

            StartGuard(kind);

            var pollCts = new CancellationTokenSource();
            _pollCts = pollCts;
            _pollTask = Task.Run(() => PollLoopAsync(pollCts.Token), CancellationToken.None);

            await RefreshAsync(cancellationToken).ConfigureAwait(false);

            SetState(SessionState.Connected, result.Message);
            if (!string.IsNullOrWhiteSpace(result.Message)) _log?.Invoke(result.Message);

            return true;
        }
        catch (OperationCanceledException)
        {
            await TeardownAsync().ConfigureAwait(false);
            SetState(SessionState.Idle, "连接已取消");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("联机会话建立失败", ex);
            await TeardownAsync().ConfigureAwait(false);
            lock (_lock) _lastError = ex.Message;
            SetState(SessionState.Failed, $"连接失败：{ex.Message}");
            RaiseNotice(ex.Message);
            return false;
        }
        finally
        {
            try { _transition.Release(); }
            catch (ObjectDisposedException) { /* 已释放 */ }
        }
    }

    /// <summary>断开并清理全部资源。可重复调用。</summary>
    public async Task DisconnectAsync()
    {
        if (_disposed) return;

        try { await _transition.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (_engine is null && _chat is null)
            {
                SetState(SessionState.Idle, "未连接");
                return;
            }

            SetState(SessionState.Disconnecting, "正在断开…");
            await TeardownAsync().ConfigureAwait(false);
            SetState(SessionState.Idle, "未连接");
        }
        finally
        {
            try { _transition.Release(); }
            catch (ObjectDisposedException) { /* 已释放 */ }
        }
    }

    // ————— 房间操作（转交 RoomChat） —————

    /// <summary>发送房间聊天气泡文本。</summary>
    public ChatSendStatus SendRoomChat(string? text) => _chat?.SendText(text) ?? ChatSendStatus.Empty;

    /// <summary>同步本机昵称（大厅里改了昵称时调用）。</summary>
    public void SetNickname(string? nickname)
    {
        var chat = _chat;
        if (chat is not null) chat.Nickname = ChatCrypt.SanitizeText(nickname, 32);
    }

    /// <summary>房主踢人；非房主调用无效。</summary>
    public void Kick(string? ip, string? name = null)
    {
        _chat?.Kick(ip, name);
        RaisePeersChanged();
    }

    /// <summary>房主拉回被移出的玩家。</summary>
    public void Unkick(string? ip)
    {
        _chat?.Unkick(ip);
        RaisePeersChanged();
    }

    /// <summary>设置要公开到大厅的房间公告。</summary>
    public void SetRoomAnnounce(string? community, string? roomIp, string? node = null, string? latency = null)
        => _chat?.SetRoomAnnounce(community, roomIp, node, latency);

    /// <summary>取消公开房间。</summary>
    public void ClearRoomAnnounce() => _chat?.ClearRoomAnnounce();

    /// <summary>立刻刷新一次对端（用户点了刷新按钮）。</summary>
    public async Task RefreshPeersAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        RaisePeersChanged();
    }

    // ————— 内部：对端轮询 —————

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PeerPollSeconds), cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"刷新对端失败：{ex.Message}");
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var engine = _engine;
        var chat = _chat;

        if (engine is null || !engine.IsRunning) return;

        var raw = await engine.QueryPeersAsync(cancellationToken).ConfigureAwait(false);

        var names = chat?.PeerNames();
        var banned = chat is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : chat.BannedPeers().Select(item => item.Ip).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var peers = new List<SessionPeer>(raw.Count);

        foreach (var peer in raw)
        {
            if (string.IsNullOrWhiteSpace(peer.Ip) || banned.Contains(peer.Ip)) continue;

            var name = string.Empty;
            if (names is not null && names.TryGetValue(peer.Ip, out var nick)) name = nick;
            if (string.IsNullOrWhiteSpace(name)) name = peer.Name;

            peers.Add(new SessionPeer(name, peer.Ip, peer.Latency, peer.Status));
        }

        // 房间聊天只认同引擎给出的对端 IP
        chat?.SetPeers(raw.Select(peer => peer.Ip));

        var changed = false;

        lock (_lock)
        {
            if (!_peers.SequenceEqual(peers))
            {
                _peers.Clear();
                _peers.AddRange(peers);
                changed = true;
            }
        }

        if (changed) RaisePeersChanged();

        var nodeInfo = await engine.NodeInfoAsync(cancellationToken).ConfigureAwait(false);
        var infoChanged = false;

        lock (_lock)
        {
            if (nodeInfo != _nodeInfo)
            {
                _nodeInfo = nodeInfo;
                infoChanged = true;
            }
        }

        if (infoChanged) RaiseStateChanged();
    }

    // ————— 内部：守护 —————

    private void StartGuard(NetworkEngineKind kind)
    {
        _guardEnabled = MultiplayerSettingsStore.Current.GuardEnabled;

        var guard = new ProcessGuard(OnEngineLostAsync, _log);
        guard.SetWatch([NetworkEngineFactory.WatchProcessName(kind)]);
        guard.SetEnabled(_guardEnabled);
        guard.Start();

        _guard = guard;
    }

    /// <summary>守护检测到引擎退出：就地重启并同步状态（对应旧版 _restart_worker）。</summary>
    private async Task OnEngineLostAsync(string processName)
    {
        if (State != SessionState.Connected)
        {
            _log?.Invoke($"[守护] 会话已断开，忽略 {processName} 的退出");
            return;
        }

        RaiseNotice($"{processName} 异常退出，正在自动重启…");
        _log?.Invoke($"[守护] 检测到 {processName} 异常退出，正在自动重启…");

        try
        {
            await _transition.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var engine = _engine;
            var options = _options;

            if (engine is null || options is null || State != SessionState.Connected) return;

            await engine.StopAsync().ConfigureAwait(false);

            var result = await engine.StartAsync(options, null, CancellationToken.None).ConfigureAwait(false);

            if (!result.Ok)
            {
                _log?.Invoke($"[守护] 自动重启失败：{result.Message}");
                RaiseNotice($"引擎自动重启失败：{result.Message}");

                await TeardownAsync().ConfigureAwait(false);
                SetState(SessionState.Failed, $"引擎重启失败：{result.Message}");
                return;
            }

            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            SetState(SessionState.Connected, $"已自动重连：{result.Message}");
            _log?.Invoke($"[守护] 自动重启完成，虚拟 IP={result.LocalIp}");
        }
        catch (Exception ex)
        {
            Log.Error("守护重启引擎失败", ex);
            RaiseNotice($"引擎自动重启失败：{ex.Message}");
            await TeardownAsync().ConfigureAwait(false);
            SetState(SessionState.Failed, $"引擎重启失败：{ex.Message}");
        }
        finally
        {
            try { _transition.Release(); }
            catch (ObjectDisposedException) { /* 已释放 */ }

            // 守护自身因频繁崩溃停掉时要让用户知道
            if (_guard is { Enabled: false }) RaiseNotice("引擎频繁崩溃，进程守护已停止自动重启");
        }
    }

    private void OnOwnerDemoted(string ownerIp)
    {
        var text = $"检测到房主冲突，本机已降级为成员（房主：{ownerIp}）";
        _log?.Invoke($"[房间] {text}");
        RaiseNotice(text);
        RaiseStateChanged();
    }

    private void OnKicked(string target, string name)
    {
        var self = LocalIp;

        if (!string.IsNullOrEmpty(self) && string.Equals(target, self, StringComparison.OrdinalIgnoreCase))
        {
            RaiseNotice("你已被房主移出房间，将自动断开连接");
            _ = Task.Run(DisconnectAsync);
            return;
        }

        RaiseNotice($"房主已将 {(string.IsNullOrEmpty(name) ? target : name)} 移出房间");
        RaisePeersChanged();
    }

    private void OnUnkicked(string target)
    {
        var text = string.Equals(target, LocalIp, StringComparison.OrdinalIgnoreCase)
            ? "你已被房主拉回房间"
            : $"房主已将 {target} 拉回房间";

        _log?.Invoke($"[房间] {text}");
        RaisePeersChanged();
    }

    // ————— 内部：清理与通知 —————

    /// <summary>停掉轮询 / 聊天 / 守护 / 引擎并清空状态。调用方需持有 _transition。</summary>
    private async Task TeardownAsync()
    {
        var pollCts = _pollCts;
        var pollTask = _pollTask;
        _pollCts = null;
        _pollTask = null;

        try { pollCts?.Cancel(); }
        catch { /* 已释放 */ }

        if (pollTask is not null)
        {
            try { await pollTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 线程退出即可 */ }
        }

        pollCts?.Dispose();

        var guard = _guard;
        _guard = null;

        if (guard is not null)
        {
            try { guard.SetEnabled(false); }
            catch { /* 忽略 */ }

            try { await guard.StopAsync().ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }

        var chat = _chat;
        _chat = null;

        if (chat is not null)
        {
            try { await chat.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"停止房间聊天失败：{ex.Message}"); }
        }

        var engine = _engine;
        _engine = null;

        if (engine is not null)
        {
            engine.OutputReceived -= OnEngineOutput;

            try { await engine.StopAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"停止组网引擎失败：{ex.Message}"); }

            try { await engine.DisposeAsync().ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }

        var hadPeers = false;

        lock (_lock)
        {
            hadPeers = _peers.Count > 0;
            _peers.Clear();
            _nodeInfo = new EngineNodeInfo(null, null, null, null);
        }

        if (hadPeers) RaisePeersChanged();
    }

    private void OnEngineOutput(string line)
    {
        lock (_lock)
        {
            _output.Add(line);
            if (_output.Count > OutputKeep) _output.RemoveRange(0, _output.Count - OutputKeep);
        }

        var handler = OutputReceived;
        if (handler is null) return;

        try { handler(line); }
        catch (Exception ex) { Log.Warn($"引擎输出回调异常：{ex.Message}"); }
    }

    private void SetState(SessionState state, string status)
    {
        lock (_lock)
        {
            _state = state;
            _status = status;

            if (state == SessionState.Connected) _lastError = string.Empty;
        }

        Log.Info($"[联机] {status}");
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        var handler = StateChanged;
        if (handler is null) return;

        try { handler(); }
        catch (Exception ex) { Log.Warn($"会话状态回调异常：{ex.Message}"); }
    }

    private void RaisePeersChanged()
    {
        var handler = PeersChanged;
        if (handler is null) return;

        try { handler(); }
        catch (Exception ex) { Log.Warn($"对端列表回调异常：{ex.Message}"); }
    }

    private void RaiseNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var handler = Notice;
        if (handler is null) return;

        try { handler(message); }
        catch (Exception ex) { Log.Warn($"提示回调异常：{ex.Message}"); }
    }

    private static void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null) return;

        try { handler(value); }
        catch (Exception ex) { Log.Warn($"会话回调异常：{ex.Message}"); }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second)
    {
        if (handler is null) return;

        try { handler(first, second); }
        catch (Exception ex) { Log.Warn($"会话回调异常：{ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try { await DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"释放联机会话失败：{ex.Message}"); }

        _disposed = true;

        try { _transition.Dispose(); }
        catch { /* 忽略 */ }
    }
}
