using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.App.Windows.Multiplayer;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Controls;

/// <summary>节点列表里的一行。</summary>
public sealed class SakuraNodeRow
{
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }
}

/// <summary>隧道列表里的一行。</summary>
public sealed class SakuraTunnelRow
{
    public required SakuraTunnel Source { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// 联机页「组网方案」里的樱花FRP 面板（第三种方案）。走端口映射直连，与 EasyTier / n2n 那种
/// 虚拟局域网是两回事：这里不分配虚拟 IP、没有房间与对端列表，只有一个映射地址。
///
/// <para>
/// frpc 进程由 <see cref="MultiplayerHub.SakuraFrpc"/> 持有：切走页面不会停隧道（主机往往正在打游戏），
/// 要停得点「停止隧道」，程序退出时由 <see cref="MultiplayerHub.Shutdown"/> 统一收尾。
/// 输出通过 <see cref="Log"/> 事件交给联机页，统一显示在「引擎日志」里。
/// </para>
/// </summary>
public partial class SakuraPlanPanel : UserControl
{
    /// <summary>接口客户端是线程安全的、也没有本地状态，做成共享实例，避免随页面反复创建 HttpClient。</summary>
    private static readonly SakuraFrpApi Api = new();

    private List<SakuraNode> _nodes = [];
    private List<SakuraTunnel> _tunnels = [];

    private bool _busy;
    private bool _hooked;

    private string _ipAddress = string.Empty;
    private string _domainAddress = string.Empty;

    public SakuraPlanPanel()
    {
        InitializeComponent();

        var settings = MultiplayerSettingsStore.Current;

        SetKey(settings.SakuraAccessKey);
        TxtTunnelName.Text = string.IsNullOrWhiteSpace(settings.SakuraTunnelName) ? Loc.T("心灵终结") : settings.SakuraTunnelName;
        TxtLocalPort.Text = NormalizePort(settings.LanPort).ToString();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>一行日志。由联机页接到「引擎日志」面板上。</summary>
    public event Action<string>? Log;

    /// <summary>
    /// 把「局域网端口」同步到这个面板。它和联机页上的那个输入框是同一个设置，
    /// 切换方案时调用一次，避免两处显示不一致。
    /// </summary>
    public void ReloadLanPort()
    {
        TxtLocalPort.Text = NormalizePort(MultiplayerSettingsStore.Current.LanPort).ToString();
    }

    /// <summary>密钥当前是否以明文显示。默认 false（掩码），避免旁边有人时直接看到。</summary>
    private bool _keyVisible;

    /// <summary>当前输入的访问密钥（去掉首尾空白），不区分明文 / 掩码两种输入控件。</summary>
    private string KeyText => (_keyVisible ? TxtKey.Text : PwdKey.Password).Trim();

    /// <summary>把密钥写进两个输入控件，并保持当前显示模式。</summary>
    private void SetKey(string? value)
    {
        var text = value ?? string.Empty;

        PwdKey.Password = text;
        TxtKey.Text = text;
    }

    /// <summary>在掩码 / 明文之间切换；两个控件的内容始终同步，切换不丢输入。</summary>
    private void OnToggleKeyVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_keyVisible)
        {
            PwdKey.Password = TxtKey.Text ?? string.Empty;
            TxtKey.Visibility = Visibility.Collapsed;
            PwdKey.Visibility = Visibility.Visible;

            BtnToggleKey.Content = Loc.T("显示");
            PwdKey.Focus();
        }
        else
        {
            TxtKey.Text = PwdKey.Password;
            PwdKey.Visibility = Visibility.Collapsed;
            TxtKey.Visibility = Visibility.Visible;

            BtnToggleKey.Content = Loc.T("隐藏");
            TxtKey.Focus();
        }

        _keyVisible = !_keyVisible;
    }

    private SakuraFrpcRunner Frpc => MultiplayerHub.SakuraFrpc;

    // ————— 进出页面 —————

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked) return;
        _hooked = true;

        var frpc = Frpc;
        frpc.Log += OnFrpcLog;
        frpc.AddressesUpdated += OnFrpcAddressesUpdated;
        frpc.Started += OnFrpcStarted;

        RefreshPanelButton();
        UpdateFrpcState();
        RevealRunningIfAny();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        _hooked = false;

        // frpc 进程归 MultiplayerHub 所有：这里只摘掉事件订阅，不停隧道
        var frpc = Frpc;
        frpc.Log -= OnFrpcLog;
        frpc.AddressesUpdated -= OnFrpcAddressesUpdated;
        frpc.Started -= OnFrpcStarted;
    }

    /// <summary>重新进入页面时，如果隧道还在跑，就把运行状态与地址直接铺回界面。</summary>
    private void RevealRunningIfAny()
    {
        var frpc = Frpc;

        if (!frpc.IsRunning)
        {
            UpdateFrpcState();
            return;
        }

        LabFrpc.Text = Loc.T("隧道正在运行。");
        PanRunning.Visibility = Visibility.Visible;
        ApplyAddresses(frpc);
    }

    // ————— 访问密钥 / 账号 / 节点 —————

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            await LoadAccountAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> LoadAccountAsync(bool silent = false)
    {
        var key = KeyText;

        if (key.Length == 0)
        {
            SetAccount(Loc.T("请先填写访问密钥。"), warn: true);
            return false;
        }

        SetAccount(Loc.T("正在验证访问密钥…"), warn: false);

        var user = await Api.GetUserAsync(key);

        if (!user.Ok || user.Value is null)
        {
            SetAccount(user.Message, warn: true);
            return false;
        }

        ShowAccount(user.Value);

        var nodes = await Api.GetNodesAsync(key);

        if (!nodes.Ok || nodes.Value is null)
        {
            AppendLog(Loc.F("读取节点列表失败：{0}", nodes.Message), warn: true);
            return true;
        }

        // 离线节点列出来只会让人误点，直接不显示
        _nodes = [.. nodes.Value.Where(node => !node.IsOffline).OrderByDescending(node => node.Usable)];
        ApplyNodes();

        // 验证通过才落盘，避免把写错的密钥存起来
        MultiplayerSettingsStore.Update(settings => settings.SakuraAccessKey = key);

        if (!silent) AppendLog(Loc.F("已加载 {0} 个可用节点。", _nodes.Count));

        await ReloadTunnelsAsync(key);
        return true;
    }

    private void ShowAccount(SakuraUser user)
    {
        if (user.BanReason.Length > 0)
        {
            SetAccount(user.BanReason, warn: true);
            return;
        }

        var traffic = Loc.F("已用 {0} · 剩余 {1}", SakuraFrpApi.FormatTraffic(user.TrafficUsed), SakuraFrpApi.FormatTraffic(user.TrafficRemaining));

        SetAccount(Loc.F("账号：{0}　隧道上限 {1}　速度 {2}　{3}", user.Name, user.TunnelLimit, user.Speed, traffic), warn: false);
    }

    private void SetAccount(string message, bool warn)
    {
        LabAccount.Text = message;
        LabAccount.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    private void ApplyNodes()
    {
        ListNodes.ItemsSource = _nodes
            .Select(node => new SakuraNodeRow
            {
                Id = node.Id,
                Title = $"{node.Name}（ID {node.Id}）",
                Detail = node.Summary
            })
            .ToList();

        if (ListNodes.SelectedIndex < 0 && _nodes.Count > 0) ListNodes.SelectedIndex = 0;
    }

    private int SelectedNodeId() => ListNodes.SelectedItem is SakuraNodeRow row ? row.Id : 0;

    /// <summary>把端口收进 1–65535；越界或为 0 时回落到默认值。</summary>
    private static int NormalizePort(int port)
        => port is < 1 or > 65535 ? SakuraFrpApi.DefaultLocalPort : port;

    /// <summary>
    /// 读界面上的本地端口。留空按默认值算；填了非数字或越界则提示并返回 0，由调用方中止。
    /// </summary>
    private int LocalPortInput()
    {
        var text = TxtLocalPort.Text.Trim();

        if (text.Length == 0) return SakuraFrpApi.DefaultLocalPort;

        if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
        {
            AppendLog(Loc.T("本地端口要填 1–65535 之间的整数。"), warn: true);
            return 0;
        }

        return port;
    }

    // ————— 隧道 —————

    private async void OnRefreshTunnelsClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            if (KeyText.Length == 0)
            {
                SetAccount(Loc.T("请先填写访问密钥。"), warn: true);
                return;
            }

            await ReloadTunnelsAsync(KeyText);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadTunnelsAsync(string key)
    {
        var result = await Api.GetTunnelsAsync(key);

        if (!result.Ok || result.Value is null)
        {
            AppendLog(Loc.F("读取隧道列表失败：{0}", result.Message), warn: true);
            return;
        }

        // 本方案走 TCP：只有 TCP 隧道能用，其它类型列出来只会让人误点
        _tunnels = [.. result.Value.Where(tunnel => string.Equals(tunnel.Type, "tcp", StringComparison.OrdinalIgnoreCase))];

        ApplyTunnels();
    }

    private void ApplyTunnels()
    {
        var lastUsed = MultiplayerSettingsStore.Current.SakuraTunnelId;

        ListTunnels.ItemsSource = _tunnels
            .OrderByDescending(tunnel => tunnel.Id == lastUsed)
            .ThenBy(tunnel => tunnel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(tunnel => new SakuraTunnelRow
            {
                Source = tunnel,
                Title = tunnel.Name + (tunnel.Id == lastUsed ? Loc.T("　（上次用的）") : string.Empty),
                Detail = Loc.F("ID {0}　本地 {1}:{2}　远程 {3}", tunnel.Id, tunnel.LocalIp, tunnel.LocalPort, tunnel.Remote) +
                         (tunnel.Status != 0 ? Loc.F("　状态异常：{0}", tunnel.StatusReason) : string.Empty)
            })
            .ToList();
    }

    private async void OnCreateTunnelClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var name = TxtTunnelName.Text.Trim();
        if (name.Length == 0)
        {
            AppendLog(Loc.T("请先填隧道名。"), warn: true);
            return;
        }

        var localPort = LocalPortInput();
        if (localPort == 0) return;

        SetBusy(true);
        try
        {
            if (!await LoadAccountAsync(silent: true)) return;

            var nodeId = SelectedNodeId();

            if (nodeId <= 0)
            {
                AppendLog(Loc.T("请先在上面选一个节点。"), warn: true);
                return;
            }

            AppendLog(Loc.F("正在创建 TCP 隧道「{0}」（节点 {1}，本地端口 {2}）…", name, nodeId, localPort));

            var created = await Api.CreateTcpTunnelAsync(KeyText, name, nodeId, localPort);

            if (!created.Ok || created.Value is null)
            {
                AppendLog(Loc.F("创建隧道失败：{0}", created.Message), warn: true);
                return;
            }

            MultiplayerSettingsStore.Update(settings =>
            {
                settings.SakuraTunnelName = name;
                // 与 MO 局域网监听端口是同一个值，必须一起改
                settings.LanPort = localPort;
            });

            AppendLog(Loc.F("隧道已创建：{0}（ID {1}）", created.Value.Name, created.Value.Id));
            await ReloadTunnelsAsync(KeyText);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartTunnelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SakuraTunnelRow row }) return;
        if (_busy) return;

        if (Frpc.IsRunning)
        {
            AppendLog(Loc.T("已经有一条隧道在运行，先停掉再启动另一条。"), warn: true);
            return;
        }

        SetBusy(true);
        try
        {
            if (!await LoadAccountAsync(silent: true)) return;

            var prepared = await Frpc.EnsureFrpcAsync();
            if (!prepared.Ok)
            {
                AppendLog(prepared.Message, warn: true);
                return;
            }

            UpdateFrpcState();

            _ipAddress = string.Empty;
            _domainAddress = string.Empty;
            LabIpAddress.Text = Loc.T("等待 frpc 输出…");
            LabDomainAddress.Text = Loc.T("等待 frpc 输出…");
            PanRunning.Visibility = Visibility.Visible;

            var started = Frpc.Start(KeyText, row.Source.Id);

            if (!started.Ok)
            {
                AppendLog(started.Message, warn: true);
                PanRunning.Visibility = Visibility.Collapsed;
                return;
            }

            // 先用接口信息拼一个兜底地址；frpc 日志里出现真实地址后会覆盖它
            var fallback = SakuraFrpApi.BuildAddress(row.Source, _nodes);
            if (fallback.Length > 0)
            {
                _domainAddress = fallback;
                LabDomainAddress.Text = fallback;
            }

            MultiplayerSettingsStore.Update(settings => settings.SakuraTunnelId = row.Source.Id);

            AppendLog(Loc.F("已启动隧道「{0}」，等 frpc 报出连接地址。", row.Source.Name));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnStopTunnelClick(object sender, RoutedEventArgs e)
    {
        Frpc.Stop();

        _ipAddress = string.Empty;
        _domainAddress = string.Empty;

        PanRunning.Visibility = Visibility.Collapsed;
        UpdateFrpcState();

        AppendLog(Loc.T("已停止隧道。"));
    }

    // ————— frpc —————

    private async void OnPrepareFrpcClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        LabFrpc.Text = Loc.T("正在准备 frpc…");

        try
        {
            var result = await Frpc.EnsureFrpcAsync();

            if (!result.Ok) AppendLog(result.Message, warn: true);
            else AppendLog(Loc.F("frpc 已就绪：{0}", result.Value));
        }
        finally
        {
            SetBusy(false);
            UpdateFrpcState();
        }
    }

    private void UpdateFrpcState()
    {
        if (Frpc.IsRunning)
        {
            LabFrpc.Text = Loc.T("隧道正在运行。");
            return;
        }

        var present = System.IO.File.Exists(SakuraFrpcRunner.FrpcPath);

        LabFrpc.Text = present
            ? Loc.T("frpc 已就绪，可以启动隧道。")
            : Loc.T("还没有准备 frpc 客户端，启动隧道前需要先下载一次（约 5 MB）。");

        BtnPrepare.Content = present ? Loc.T("重新准备 frpc") : Loc.T("准备 frpc");
    }

    private void OnFrpcStarted() => Dispatch(() => AppendLog(Loc.T("隧道启动成功，正在等待连接地址…")));

    private void OnFrpcLog(string line) => Dispatch(() => Log?.Invoke(line));

    private void OnFrpcAddressesUpdated() => Dispatch(() => ApplyAddresses(Frpc));

    private void ApplyAddresses(SakuraFrpcRunner frpc)
    {
        if (frpc.DomainAddress.Length > 0) _domainAddress = frpc.DomainAddress;
        if (frpc.IpAddress.Length > 0) _ipAddress = frpc.IpAddress;

        LabDomainAddress.Text = _domainAddress.Length > 0 ? _domainAddress : Loc.T("等待 frpc 输出…");
        LabIpAddress.Text = _ipAddress.Length > 0 ? _ipAddress : Loc.T("等待 frpc 输出…");
    }

    // ————— 复制 —————

    private void OnCopyIpCommandClick(object sender, RoutedEventArgs e)
    {
        if (_ipAddress.Length == 0)
        {
            AppendLog(Loc.T("还没拿到 IP 形式的地址，等 frpc 输出后再复制（客户端只认纯 IP，域名用不了）。"), warn: true);
            return;
        }

        Copy(_ipAddress, Loc.T("口令"));
        AppendLog(Loc.T("队友拿到后：粘到 MO 客户端「局域网大厅」底部的地址框（Ctrl+V）回车即可加入。"));
    }

    private void OnCopyDomainClick(object sender, RoutedEventArgs e)
    {
        if (_domainAddress.Length == 0)
        {
            AppendLog(Loc.T("还没拿到域名形式的地址。"), warn: true);
            return;
        }

        Copy(_domainAddress, Loc.T("域名地址"));
    }

    private void Copy(string text, string label)
    {
        try
        {
            Clipboard.SetText(text);
            AppendLog(Loc.F("已复制{0}。", label));
        }
        catch (Exception ex)
        {
            AppendLog(Loc.F("复制到剪贴板失败：{0}", ex.Message), warn: true);
        }
    }

    // ————— 内嵌面板 —————

    private void OnPanelClick(object sender, RoutedEventArgs e)
    {
        if (!MultiplayerSakuraPanelWindow.IsRuntimeAvailable())
        {
            AppendLog(Loc.T("本机没有 WebView2 运行时，内嵌面板打不开；已为你打开 WebView2 下载页，装完再点一次即可（也可以直接用浏览器打开 natfrp.com）。"), warn: true);
            MultiplayerSakuraPanelWindow.OpenRuntimeDownloadPage();
            return;
        }

        // 面板默认关闭，但按钮始终可点：点了就说明要用，确认一下当场启用，不必再跑一趟设置页
        if (!MultiplayerSettingsStore.Current.SakuraPanelEnabled)
        {
            var owner = Window.GetWindow(this);

            if (!ChoiceWindow.Confirm(
                    owner,
                    Loc.T("启用内嵌面板"),
                    Loc.T("内嵌面板默认是关闭的。要现在启用，并在启动器里打开樱花FRP 管理面板吗？"),
                    Loc.T("启用后可在「设置 → 联机设置」里再次关闭。")))
            {
                return;
            }

            MultiplayerSettingsStore.Update(settings => settings.SakuraPanelEnabled = true);
            RefreshPanelButton();
        }

        new MultiplayerSakuraPanelWindow { Owner = Window.GetWindow(this) }.Show();
    }

    private void RefreshPanelButton()
    {
        var available = MultiplayerSakuraPanelWindow.IsRuntimeAvailable();
        var enabled = MultiplayerSettingsStore.Current.SakuraPanelEnabled;

        // 按钮永远可点：没启用就点一下当场启用，缺运行时就给出下载地址，不要留一个灰按钮没有任何反馈
        BtnPanel.IsEnabled = true;
        BtnPanel.ToolTip = !available
            ? Loc.T("在内嵌浏览器里打开樱花FRP 管理面板（本机缺少 WebView2 运行时，点击会打开下载页）")
            : enabled
                ? Loc.T("在内嵌浏览器里打开樱花FRP 管理面板（登录后可建隧道 / 看流量）")
                : Loc.T("在内嵌浏览器里打开樱花FRP 管理面板（点击会先确认启用，可在「设置 → 联机设置」里关闭）");
    }

    // ————— 通用 —————

    private void SetBusy(bool busy)
    {
        _busy = busy;

        BtnVerify.IsEnabled = !busy;
        BtnCreate.IsEnabled = !busy;
        BtnRefresh.IsEnabled = !busy;
        BtnPrepare.IsEnabled = !busy;
        TxtKey.IsEnabled = !busy;
        PwdKey.IsEnabled = !busy;
        TxtLocalPort.IsEnabled = !busy;
        BtnToggleKey.IsEnabled = !busy;
    }

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    /// <summary>写一行到「引擎日志」。警告行加个前缀，方便在同一个面板里一眼看出来。</summary>
    private void AppendLog(string message, bool warn = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => AppendLog(message, warn));
            return;
        }

        Log?.Invoke(warn ? Loc.F("[樱花FRP] ⚠ {0}", message) : Loc.F("[樱花FRP] {0}", message));
        HMOL.Core.Logging.Log.Info(Loc.F("[樱花FRP] {0}", message));
    }
}
