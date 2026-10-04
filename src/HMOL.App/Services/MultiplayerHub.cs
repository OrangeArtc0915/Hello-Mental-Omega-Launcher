using System.Windows;
using HMOL.App.Windows;
using HMOL.App.Windows.Multiplayer;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Services;

/// <summary>
/// 联机模块的共享入口。组网会话与游戏内 HUD 在同一时刻只允许存在一份，
/// 联机页、托盘菜单、HUD 都从这里取，避免各自持有会话导致状态不一致（与 <see cref="GameSessionHub"/> 同一思路）。
/// </summary>
internal static class MultiplayerHub
{
    private static readonly object Lock = new();

    private static MultiplayerSession? _session;
    private static MultiplayerHudWindow? _hud;
    private static FileTransfer? _transfer;
    private static SupernodeServer? _server;
    private static SakuraFrpcRunner? _sakura;

    /// <summary>共享的联机会话（首次访问时创建，日志转投运行日志页）。</summary>
    public static MultiplayerSession Session
    {
        get
        {
            lock (Lock)
            {
                return _session ??= CreateSession();
            }
        }
    }

    /// <summary>会话状态变化（连接 / 断开 / 守护重启 / 虚拟 IP 变化）。可能来自后台线程。</summary>
    public static event Action? StateChanged;

    public static bool IsConnected => Session.IsConnected;

    public static bool IsBusy => Session.IsBusy;

    /// <summary>供托盘提示用的一句话状态。</summary>
    public static string Summary
    {
        get
        {
            var session = Session;

            if (!session.IsConnected) return session.StatusText;

            return string.IsNullOrWhiteSpace(session.LocalIp)
                ? "已连接"
                : $"已连接 {session.LocalIp}";
        }
    }

    private static MultiplayerSession CreateSession()
    {
        var session = new MultiplayerSession(line => ActivityLog.Write(LogSource.App, line));

        session.StateChanged += OnSessionStateChanged;
        session.PeersChanged += UpdateHud;

        return session;
    }

    private static void OnSessionStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn($"联机状态回调异常：{ex.Message}"); }

        UpdateHud();
    }

    // ————— 游戏内 HUD —————

    /// <summary>HUD 是否正在显示。</summary>
    public static bool HudVisible
    {
        get
        {
            lock (Lock) return _hud is { IsVisible: true };
        }
    }

    /// <summary>开关 HUD。返回切换后的可见状态。</summary>
    public static bool ToggleHud()
    {
        if (HudVisible)
        {
            HideHud();
            return false;
        }

        ShowHud();
        return true;
    }

    public static void ShowHud()
    {
        MultiplayerHudWindow hud;

        lock (Lock)
        {
            hud = _hud ??= CreateHud();
        }

        UpdateHud();
        hud.ShowAtDefault();
    }

    public static void HideHud()
    {
        lock (Lock) _hud?.Hide();
    }

    /// <summary>
    /// 设置页改了「HUD 透明度」后调用：HUD 已经建出来就立刻刷新，
    /// 还没建过就什么都不做——下次 <see cref="ShowHud"/> 建窗口时会自然带上新值。
    /// </summary>
    public static void ApplyHudOpacity()
    {
        MultiplayerHudWindow? hud;

        lock (Lock) hud = _hud;

        if (hud is null) return;

        try
        {
            hud.ApplyOpacity();
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新 HUD 透明度失败：{ex.Message}");
        }
    }

    private static MultiplayerHudWindow CreateHud()
    {
        var hud = new MultiplayerHudWindow();

        // HUD 自己关掉时只是隐藏，下次点「游戏 HUD」还能接着用
        hud.HideRequested += OnHudHidden;

        // 被系统关掉（Alt+F4 等）时丢掉引用，下次重新建一个，避免对着已关闭的窗口调 Show
        hud.Closed += (_, _) =>
        {
            lock (Lock)
            {
                if (ReferenceEquals(_hud, hud)) _hud = null;
            }

            OnHudHidden();
        };

        return hud;
    }

    private static void OnHudHidden()
    {
        try { StateChanged?.Invoke(); }
        catch { /* 回调异常不影响业务 */ }
    }

    /// <summary>把会话状态投影到 HUD。可能在后台线程被调用，内部自行切回 UI 线程。</summary>
    private static void UpdateHud()
    {
        MultiplayerHudWindow? hud;

        lock (Lock)
        {
            hud = _hud;
        }

        if (hud is null) return;

        if (!hud.Dispatcher.CheckAccess())
        {
            hud.Dispatcher.InvokeAsync(UpdateHud);
            return;
        }

        if (!hud.IsVisible) return;

        var session = Session;

        IReadOnlyList<HudRow> rows = session.IsConnected
            ? session.Peers.Select(peer => new HudRow(peer.Name, peer.Ip, peer.Latency)).ToArray()
            : [];

        hud.SetContent(session.StatusText, rows);
    }

    // ————— 文件传输 —————

    /// <summary>收到文件并接收完成后触发（参数是最终保存路径）。可能来自后台线程。</summary>
    public static event Action<string>? FileReceived;

    /// <summary>后台常驻的文件接收服务（首次访问时启动监听）。</summary>
    public static FileTransfer Transfer
    {
        get
        {
            lock (Lock)
            {
                if (_transfer is not null) return _transfer;

                var transfer = new FileTransfer(log: line => ActivityLog.Write(LogSource.App, line));
                transfer.IncomingFileHandler = AskSavePath;
                transfer.FileReceived += path => FileReceived?.Invoke(path);
                transfer.Start();

                _transfer = transfer;
                return transfer;
            }
        }
    }

    /// <summary>
    /// 收到文件时在 UI 线程上询问保存位置。返回 null 表示拒收
    /// （对应旧版 _on_file_incoming：60 秒不响应也按拒收处理）。
    /// </summary>
    private static string? AskSavePath(IncomingFile incoming)
    {
        var app = Application.Current;
        if (app is null) return null;

        try
        {
            return app.Dispatcher.Invoke(() =>
            {
                var owner = app.MainWindow is { IsLoaded: true } window ? window : null;

                var message = $"队友 {(string.IsNullOrWhiteSpace(incoming.From) ? incoming.FromIp : incoming.From)} 想发送文件：\n\n" +
                              $"文件：{incoming.Name}\n大小：{FileTransfer.FormatSize(incoming.Size)}\n\n是否接收？";

                var answer = ChoiceWindow.Confirm(owner, "收到文件传输请求", message,
                    confirmText: "接收", cancelText: "拒收");

                if (!answer) return null;

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存文件到",
                    FileName = incoming.Name,
                    Filter = "所有文件 (*.*)|*.*"
                };

                var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                return confirmed == true ? dialog.FileName : null;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"询问文件保存位置失败：{ex.Message}");
            return null;
        }
    }

    // ————— 自建 supernode（服务端模式） —————

    /// <summary>当前自建的 supernode；未启动时为 null。窗口关掉后仍在运行，直到手动停止或程序退出。</summary>
    public static SupernodeServer? Server
    {
        get
        {
            lock (Lock) return _server;
        }
    }

    /// <summary>启动自建节点。</summary>
    public static async Task<EngineStartResult> StartServerAsync(int port, string ipPool, CancellationToken token)
    {
        await StopServerAsync().ConfigureAwait(false);

        var server = new SupernodeServer(port, ipPool, line => ActivityLog.Write(LogSource.App, line));

        var result = await server.StartAsync(15, token).ConfigureAwait(false);

        if (result.Ok)
        {
            lock (Lock) _server = server;
            return result;
        }

        try { await server.DisposeAsync().ConfigureAwait(false); }
        catch { /* 忽略 */ }

        return result;
    }

    /// <summary>停止自建节点。</summary>
    public static async Task StopServerAsync()
    {
        SupernodeServer? server;

        lock (Lock)
        {
            server = _server;
            _server = null;
        }

        if (server is null) return;

        try { await server.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"停止自建节点失败：{ex.Message}"); }
    }

    // ————— 樱花FRP 隧道（端口映射直连） —————

    /// <summary>
    /// 共享的 frpc 隧道进程。放在 Hub 里而不是联机页里，是为了「离开联机页隧道照旧跑」——
    /// 房主往往正在打游戏，一关掉界面就把隧道掐了会直接掉线。程序退出时由 <see cref="Shutdown"/> 收尾。
    /// </summary>
    public static SakuraFrpcRunner SakuraFrpc
    {
        get
        {
            lock (Lock)
            {
                return _sakura ??= new SakuraFrpcRunner();
            }
        }
    }

    // ————— 退出清理 —————

    /// <summary>真正退出前清理：关掉 HUD、断开并释放会话。由 App 调用。</summary>
    public static void Shutdown()
    {
        MultiplayerHudWindow? hud;
        MultiplayerSession? session;
        FileTransfer? transfer;
        SakuraFrpcRunner? sakura;

        lock (Lock)
        {
            hud = _hud;
            session = _session;
            transfer = _transfer;
            sakura = _sakura;
            _hud = null;
            _session = null;
            _transfer = null;
            _sakura = null;
        }

        try { hud?.Close(); }
        catch (Exception ex) { Log.Warn($"关闭联机 HUD 失败：{ex.Message}"); }

        try { sakura?.Dispose(); }
        catch (Exception ex) { Log.Warn($"停止樱花FRP 隧道失败：{ex.Message}"); }

        try { transfer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { Log.Warn($"停止文件传输失败：{ex.Message}"); }

        try { StopServerAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Log.Warn($"停止自建节点失败：{ex.Message}"); }

        if (session is null) return;

        // 放到线程池上等：会话内部的等待都走 ConfigureAwait(false)，在 UI 线程上阻塞等待容易被误判为死锁
        try { Task.Run(() => session.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Warn($"释放联机会话失败：{ex.Message}"); }
    }
}
