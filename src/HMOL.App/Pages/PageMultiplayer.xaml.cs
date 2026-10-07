using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.App.Windows.Multiplayer;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Pages;

/// <summary>
/// 联机页。对齐旧版「组网 / 大厅 / 对端」三块内容（HMOL联机模块.py:_build_tab_network / _build_tab_hall / _build_tab_peers），
/// 组网编排全部交给 <see cref="MultiplayerSession"/>，本页只做表单、列表与弹窗。
/// </summary>
public partial class PageMultiplayer : LauncherPage
{
    /// <summary>房间名上限（旧版 _community_limit 为 6）。</summary>
    private const int MaxRoomNameLength = 6;

    /// <summary>引擎输出面板最多保留的行数。</summary>
    private const int MaxEngineLines = 300;

    /// <summary>大厅列表刷新间隔（秒）。旧版 _hall_poller 为 3 秒。</summary>
    private const double HallPollSeconds = 3.0;

    private static readonly Regex Ipv4Pattern = new(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$", RegexOptions.Compiled);

    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");

    /// <summary>大厅在线玩家一行。</summary>
    private sealed record HallRow(string Sid, string Name, string Ip, string Latency);

    /// <summary>公开房间一行。</summary>
    private sealed record PublicRoomRow(string Name, string Community, string Latency, string Node);

    /// <summary>收藏队友一行。</summary>
    private sealed record FriendRow(string Name, string Ip, string Community, string Times);

    /// <summary>本页动画：键统一带 <c>mp:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("mp:");

    private NetworkEngineKind _kind = NetworkEngineKind.EasyTier;

    /// <summary>组网方案里是不是选了樱花 Frp（端口映射直连，不走虚拟局域网引擎）。</summary>
    private bool _sakuraPlan;
    private NetworkAddressMode _addressMode = NetworkAddressMode.Auto;

    private bool _publishing;
    private bool _hooked;
    private bool _loadingForm;
    private bool _suppressNodeChange;
    private bool _tapping;

    /// <summary>上一次展示过的「必要文件」提示，避免同一句反复弹。</summary>
    private string? _lastRequiredHint;

    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _hallLoopCts;
    private Task? _hallLoopTask;

    private HallClient? _hall;
    private HallChat? _hallChat;

    private MultiplayerChatWindow? _roomChatWindow;
    private MultiplayerChatWindow? _hallChatWindow;

    private readonly List<string> _roomMessages = [];
    private readonly List<string> _hallMessages = [];

    public PageMultiplayer()
    {
        InitializeComponent();

        // 入场计划：页头 → 四个子视图（延迟 200ms 封顶，整体在 500ms 内）；分类切换交给窗口侧栏
        _anim.Group(0, HeaderMultiplayer);
        _anim.Group(80, BarNotice, PanNetwork, PanHall, PanPeers, PanLog);

        HookSession();
        LoadForm();
        SwitchCategory(0);
        RefreshConnectionUi();
        RefreshPeers();
        RefreshHall();
        RefreshOwnerButtons();
        RefreshHudButton();
        RefreshRequiredFilesUi();

        // 樱花 Frp 的输出与 EasyTier / n2n 共用「引擎日志」，方便一起排查
        PanSakuraPlan.Log += AppendEngineLine;
    }

    private MultiplayerSession Session => MultiplayerHub.Session;

    private Window? OwnerWindow => Window.GetWindow(this);

    private string RoomName => ChatCrypt.SanitizeText(TxtRoomName.Text, MaxRoomNameLength);

    private string RoomKey => ChatCrypt.SanitizeText(TxtRoomKey.Text, 64);

    private string ManualIp => TxtManualIp.Text.Trim();

    /// <summary>联机昵称：取设置里的联机昵称，未设置时用占位名（进大厅会要求先去填）。</summary>
    private string Nickname
    {
        get
        {
            var value = ChatCrypt.SanitizeText(SettingsStore.Current.Nickname, 32);
            return value.Length == 0 ? Loc.T("玩家") : value;
        }
    }

    // ————— 生命周期 —————

    public override void OnEnter()
    {
        HookSession();

        RefreshConnectionUi();
        RefreshPeers();
        RefreshNickname();

        if (_hall is { Connected: true })
        {
            StartHallLoop();
            RefreshHall();
        }

        RefreshHudButton();
        UpdateEngineHint();
        RefreshRequiredFilesUi();

        // 首次进联机页的必要文件引导放到页面渲染之后，避免打断入场动画与导航
        Dispatcher.InvokeAsync(MaybePromptRequiredFiles);

        _anim.Play();
    }

    public override void OnLeave()
    {
        // 页面只是被隐藏，订阅与大厅轮询照旧（后台仍要收消息），这里只收掉本页的入场动画
        _anim.Stop();
    }

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    /// <summary>四个子视图：组网 / 大厅 / 对端 / 引擎日志。</summary>
    public override int SubViewCount => 4;

    /// <summary>
    /// 自检也走窗口那条路：侧栏切换与视图显隐一次全覆盖。
    /// </summary>
    public override void SelectSubView(int index)
        => (Window.GetWindow(this) as MainWindow)?.SelectMultiplayerCategory(index);

    // ————— 会话事件 —————

    private void HookSession()
    {
        if (_hooked) return;
        _hooked = true;

        var session = Session;

        session.StateChanged += OnSessionStateChanged;
        session.PeersChanged += OnSessionPeersChanged;
        session.OutputReceived += OnEngineOutputLine;
        session.ChatMessageReceived += OnRoomMessage;
        session.Notice += text => Dispatch(() => ShowNotice(text));

        MultiplayerHub.StateChanged += OnHubStateChanged;
    }

    private void OnSessionStateChanged() => Dispatch(() =>
    {
        RefreshConnectionUi();
        RefreshPublishUi();
        RefreshOwnerButtons();
        RefreshHudButton();
        RefreshPeers();
    });

    private void OnSessionPeersChanged() => Dispatch(() =>
    {
        RefreshPeers();
        RefreshOwnerButtons();
    });

    private void OnHubStateChanged() => Dispatch(RefreshHudButton);

    private void OnEngineOutputLine(string line) => Dispatch(() => AppendEngineLine(line));

    private void OnRoomMessage(ChatMessage message)
    {
        var line = $"[{TimeText(message.Ts)}] {message.Name}: {message.Text}";

        _roomMessages.Add(line);
        TrimHistory(_roomMessages);

        Dispatch(() => _roomChatWindow?.Append(line));
    }

    /// <summary>把 UI 操作切回界面线程。</summary>
    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    private static string TimeText(long unix)
    {
        if (unix <= 0) return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        try { return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
        catch { return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture); }
    }

    private static void TrimHistory(List<string> lines)
    {
        if (lines.Count > 300) lines.RemoveRange(0, 100);
    }

    // ————— 子视图切换 —————

    /// <summary>
    /// 切换本页显示的子视图（组网 / 大厅 / 对端 / 引擎日志）。侧栏选中态由
    /// <see cref="MainWindow"/> 负责，这里只管视图本身的显隐。
    /// </summary>
    internal void SwitchCategory(int index)
    {
        var view = Math.Clamp(index, 0, 3);

        PanNetwork.Visibility = view == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanHall.Visibility = view == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanPeers.Visibility = view == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanLog.Visibility = view == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (view == 1) RefreshHall();
        if (view == 3) UpdateEngineHint();
    }

    /// <summary>本页内部要跳回某个子视图时走这里，让侧栏选中态跟着一起走。</summary>
    private void GoToCategory(int index)
    {
        if (Window.GetWindow(this) is MainWindow window) window.SelectMultiplayerCategory(index);
        else SwitchCategory(index);
    }

    // ————— 表单 —————

    private void LoadForm()
    {
        _loadingForm = true;

        var settings = MultiplayerSettingsStore.Current;

        _kind = settings.Engine;
        _sakuraPlan = settings.UseSakuraFrp;
        _addressMode = settings.AddressMode;
        _publishing = settings.PublishRoom;

        TxtRoomName.Text = string.IsNullOrWhiteSpace(settings.RoomName) ? NewRoomName() : settings.RoomName;
        TxtRoomKey.Text = settings.RoomKey;
        TxtManualIp.Text = string.IsNullOrWhiteSpace(settings.ManualIp) ? $"{NodeCatalog.N2nDefaultSubnet}.66" : settings.ManualIp;
        ChkHideForeignPeers.IsChecked = settings.HideForeignPeers;

        _loadingForm = false;

        RefreshPlanButtons();
        RefreshNodeOptions();
        RefreshIpModeUi();
        RefreshPublishUi();
        RefreshNickname();
        RefreshSharePreview();
    }

    private void SaveForm()
    {
        if (_loadingForm) return;

        var kind = _kind;
        var node = SelectedNode();
        var room = RoomName;
        var key = RoomKey;
        var addressMode = _addressMode;
        var manualIp = ManualIp;
        var publishing = _publishing;
        var hideForeignPeers = ChkHideForeignPeers.IsChecked == true;

        MultiplayerSettingsStore.Update(settings =>
        {
            settings.Engine = kind;
            settings.UseSakuraFrp = _sakuraPlan;
            settings.RoomName = room;
            settings.RoomKey = key;
            settings.AddressMode = addressMode;
            settings.ManualIp = manualIp;
            settings.PublishRoom = publishing;
            settings.HideForeignPeers = hideForeignPeers;

            if (kind == NetworkEngineKind.N2n) settings.N2nNode = node;
            else settings.EasyTierNode = node;
        });
    }

    private static string NewRoomName()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(MaxRoomNameLength, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[Random.Shared.Next(chars.Length)];
        });
    }

    private void OnPlanClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        if (string.Equals(tag, "Sakura", StringComparison.OrdinalIgnoreCase))
        {
            if (_sakuraPlan) return;

            _sakuraPlan = true;
        }
        else
        {
            var kind = string.Equals(tag, "N2n", StringComparison.OrdinalIgnoreCase)
                ? NetworkEngineKind.N2n
                : NetworkEngineKind.EasyTier;

            if (!_sakuraPlan && kind == _kind) return;

            _sakuraPlan = false;
            _kind = kind;
        }

        RefreshPlanButtons();
        RefreshNodeOptions();
        RefreshIpModeUi();
        SaveForm();
        RefreshSharePreview();
        RefreshRequiredFilesUi();
    }

    private void RefreshPlanButtons()
    {
        BtnPlanEasyTier.Tone = !_sakuraPlan && _kind == NetworkEngineKind.EasyTier ? ButtonTone.Solid : ButtonTone.Outline;
        BtnPlanN2n.Tone = !_sakuraPlan && _kind == NetworkEngineKind.N2n ? ButtonTone.Solid : ButtonTone.Outline;
        BtnPlanSakura.Tone = _sakuraPlan ? ButtonTone.Solid : ButtonTone.Outline;

        // 樱花 Frp 是端口映射、不分虚拟 IP：参数卡与「连接」卡都换成它自己的那一套
        CardVpnPlan.Visibility = _sakuraPlan ? Visibility.Collapsed : Visibility.Visible;
        CardSakuraPlan.Visibility = _sakuraPlan ? Visibility.Visible : Visibility.Collapsed;
        CardConnect.Visibility = _sakuraPlan ? Visibility.Collapsed : Visibility.Visible;

        // 它也没有房间与分享口令，分享文本那一卡整块收起来
        CardShare.Visibility = _sakuraPlan ? Visibility.Collapsed : Visibility.Visible;

        // 工具箱里跟虚拟网卡/局域网相关的那几件（连通性检测、网卡优先级、文件传输、更多设置、广播转发）在樱花方案下用不上；
        // 游戏 HUD 也一并收起（HUD 是给虚拟局域网那套用的，樱花方案下从设置页开关即可），只留防火墙与延迟代码
        var vpnTools = _sakuraPlan ? Visibility.Collapsed : Visibility.Visible;
        BtnNetCheck.Visibility = vpnTools;
        BtnMetric.Visibility = vpnTools;
        BtnTransfer.Visibility = vpnTools;
        BtnMoreSettings.Visibility = vpnTools;
        BtnWinIpBroadcast.Visibility = vpnTools;
        BtnHud.Visibility = vpnTools;

        PanN2nTools.Visibility = !_sakuraPlan && _kind == NetworkEngineKind.N2n ? Visibility.Visible : Visibility.Collapsed;

        LabPlanHint.Text = _sakuraPlan
            ? Loc.T("樱花 Frp 是端口映射直连：不用装虚拟网卡、不分配虚拟 IP，主机开一条隧道把地址发给队友，队友在客户端里直连（见下方「樱花 Frp 参数」）。人少、临时开一局很方便。")
            : _kind == NetworkEngineKind.N2n
                ? Loc.T("n2n 是二层组网，对依赖广播的老游戏兼容性最好，但首次使用必须先安装 TAP 虚拟网卡驱动。")
                : Loc.T("EasyTier 是三层组网，免装驱动，用内置公共节点即可开房（推荐）。");
    }

    private void RefreshNodeOptions()
    {
        var settings = MultiplayerSettingsStore.Current;
        var options = (_kind == NetworkEngineKind.N2n
            ? NodeCatalog.N2nNodeOptions()
            : NodeCatalog.EasyTierNodeOptions()).ToList();

        var saved = _kind == NetworkEngineKind.N2n ? settings.N2nNode : settings.EasyTierNode;
        var label = string.IsNullOrWhiteSpace(saved) ? string.Empty : NodeCatalog.NodeLabel(saved);

        if (label.Length > 0 && !options.Contains(label)) options.Add(label);

        _suppressNodeChange = true;
        CmbNode.ItemsSource = options;

        if (label.Length > 0 && options.Contains(label)) CmbNode.SelectedItem = label;
        else if (options.Count > 0) CmbNode.SelectedIndex = 0;

        _suppressNodeChange = false;
    }

    private void OnNodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNodeChange || _loadingForm) return;

        SaveForm();
        RefreshSharePreview();
    }

    /// <summary>把某个节点设为当前选中项（分享 / 邀请 / 测速回填时用）。</summary>
    private void SelectNode(string node)
    {
        if (string.IsNullOrWhiteSpace(node)) return;

        var label = NodeCatalog.NodeLabel(node);
        var options = (CmbNode.ItemsSource as IEnumerable<string>)?.ToList() ?? [];

        if (!options.Contains(label)) options.Add(label);

        _suppressNodeChange = true;
        CmbNode.ItemsSource = options;
        CmbNode.SelectedItem = label;
        _suppressNodeChange = false;
    }

    private string SelectedNode()
    {
        var label = CmbNode.SelectedItem as string ?? string.Empty;
        var node = NodeCatalog.LabelToNode(label);

        if (node.Length > 0) return node;

        var settings = MultiplayerSettingsStore.Current;
        return _kind == NetworkEngineKind.N2n ? settings.N2nNode : settings.EasyTierNode;
    }

    private void OnRoomNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingForm) return;

        if (TxtRoomName.Text.Length > MaxRoomNameLength) TxtRoomName.Text = TxtRoomName.Text[..MaxRoomNameLength];

        SaveForm();
        RefreshSharePreview();
    }

    private void OnRandomRoomClick(object sender, RoutedEventArgs e)
    {
        TxtRoomName.Text = NewRoomName();
        SaveForm();
        RefreshSharePreview();
    }

    private void OnIpModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        _addressMode = string.Equals(tag, "Manual", StringComparison.OrdinalIgnoreCase)
            ? NetworkAddressMode.Manual
            : NetworkAddressMode.Auto;

        RefreshIpModeUi();
        SaveForm();
        RefreshSharePreview();
    }

    private void RefreshIpModeUi()
    {
        BtnIpAuto.Tone = _addressMode == NetworkAddressMode.Auto ? ButtonTone.Solid : ButtonTone.Outline;
        BtnIpManual.Tone = _addressMode == NetworkAddressMode.Manual ? ButtonTone.Solid : ButtonTone.Outline;

        var manual = _addressMode == NetworkAddressMode.Manual;
        TxtManualIp.IsEnabled = manual;
        PanManualIp.Opacity = manual ? 1 : 0.55;

        var subnet = _kind == NetworkEngineKind.N2n ? NodeCatalog.N2nDefaultSubnet : NodeCatalog.EasyTierDefaultSubnet;
        LabIpHint.Text = manual
            ? Loc.F("手动模式：直接使用上面的 IP（建议 {0}.66 起）。同一房间里的两台机器不能填相同地址。", subnet)
            : Loc.F("自动模式：由节点分配虚拟 IP（{0}.x 网段）。", subnet);
    }

    private void RefreshSharePreview() => TxtSharePreview.Text = BuildShareText();

    private string BuildShareText() => ShareCode.Build(
        _kind,
        SelectedNode(),
        RoomName,
        RoomKey,
        _addressMode,
        ManualIp,
        AppInfo.VersionDisplay);

    // ————— 连接 —————

    private async void OnConnectClick(object sender, RoutedEventArgs e) => await ConnectInternalAsync(isOwner: true);

    private async Task ConnectInternalAsync(bool isOwner)
    {
        var session = Session;

        if (session.IsConnected || session.IsBusy)
        {
            await DisconnectInternalAsync();
            return;
        }

        // 虚拟局域网方案要先备齐组网组件（runtime 不再随包分发，缺就现场下载）
        if (!_sakuraPlan)
        {
            var missing = RuntimeComponents.MissingFor(_kind);

            if (missing.Count > 0)
            {
                var names = string.Join("、", missing.Select(RuntimeComponents.DisplayName));

                var answer = ChoiceWindow.Confirm(OwnerWindow, Loc.T("需要组网组件"),
                    Loc.F("当前方案需要 {0}，本机还没有，是否现在下载？", names),
                    confirmText: Loc.T("下载并安装"), cancelText: Loc.T("取消"));

                if (!answer) return;

                foreach (var component in missing)
                {
                    if (await RequiredFilesFlow.EnsureRuntimeAsync(OwnerWindow, component, confirm: false)) continue;

                    ShowNotice(Loc.F("缺少 {0}，无法连接。", RuntimeComponents.DisplayName(component)), isError: true);
                    return;
                }

                RefreshRequiredFilesUi();
            }
        }

        var room = RoomName;

        if (room.Length == 0)
        {
            ShowNotice(Loc.T("请先填写房间名称（小组名）。"), isError: true);
            return;
        }

        if (room.Length > MaxRoomNameLength)
        {
            ShowNotice(Loc.F("房间名称最多 {0} 个字符。", MaxRoomNameLength), isError: true);
            return;
        }

        if (_addressMode == NetworkAddressMode.Manual && !IsUsableManualIp(ManualIp))
        {
            ShowNotice(Loc.T("手动虚拟 IP 格式不正确，或填成了网段 / 广播地址（不能以 .0 或 .255 结尾）。"), isError: true);
            return;
        }

        if (_kind == NetworkEngineKind.N2n)
        {
            var tapCount = await NetworkToolkit.TapCountAsync();

            if (tapCount <= 0)
            {
                var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("需要 TAP 虚拟网卡"),
                    Loc.T("n2n 方案依赖 TAP 虚拟网卡，当前系统里没有检测到。"),
                    Loc.T("下一步会打开「安装 TAP 驱动」说明，安装需要管理员权限。"),
                    new ChoiceOption(Loc.T("去安装 TAP 驱动"), "install"),
                    new ChoiceOption(Loc.T("取消"), "cancel"));

                if (choice == "install") await RunTapInstallAsync();
                return;
            }
        }

        SaveForm();

        var options = new NetworkSessionOptions(
            room, RoomKey, SelectedNode(), Nickname, isOwner, _addressMode, ManualIp,
            HideForeignPeers: ChkHideForeignPeers.IsChecked == true);

        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        SetConnectingUi(true);

        var progress = new Progress<string>(text =>
        {
            LabStatus.Text = text;
            AppendEngineLine(text);
        });

        bool ok;

        try
        {
            ok = await session.ConnectAsync(_kind, options, progress, _connectCts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("连接联机失败"), ex);
            ShowNotice(Loc.F("连接失败：{0}", ex.Message), isError: true);
            return;
        }
        finally
        {
            SetConnectingUi(false);
        }

        if (ok)
        {
            ShowNotice(Loc.F("已连接，虚拟 IP：{0}", (string.IsNullOrEmpty(session.LocalIp) ? Loc.T("-") : session.LocalIp)));
            _hallChat = _hall?.Chat ?? _hallChat;
        }
        else if (session.State == SessionState.Failed)
        {
            ShowNotice(session.StatusText, isError: true);
        }

        RefreshConnectionUi();
        RefreshPeers();
        RefreshPublishUi();
        RefreshSharePreview();
    }

    private async Task DisconnectInternalAsync()
    {
        try { _connectCts?.Cancel(); }
        catch { /* 已释放 */ }

        try { await Session.DisconnectAsync(); }
        catch (Exception ex) { Log.Warn(Loc.F("断开联机失败：{0}", ex.Message)); }

        _publishing = false;
        RefreshConnectionUi();
        RefreshPublishUi();
        RefreshPeers();
    }

    private void SetConnectingUi(bool connecting)
    {
        BtnConnect.IsEnabled = true;

        if (connecting)
        {
            BtnConnect.Content = Loc.T("取消连接");
            BtnConnect.Tone = ButtonTone.Danger;
        }
        else
        {
            RefreshConnectionUi();
        }
    }

    private void RefreshConnectionUi()
    {
        var session = Session;

        LabStatus.Text = session.StatusText;
        LabLocalIp.Text = string.IsNullOrEmpty(session.LocalIp) ? "-" : session.LocalIp;

        var info = session.NodeInfo;
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(info.NatType)) parts.Add($"NAT: {info.NatType}");
        if (!string.IsNullOrWhiteSpace(info.PeerId)) parts.Add($"PeerID: {Truncate(info.PeerId, 12)}");
        if (!string.IsNullOrWhiteSpace(info.PublicIpv4)) parts.Add(Loc.F("公网 IP: {0}", info.PublicIpv4));

        LabNodeInfo.Text = string.Join("   ", parts);

        if (session.IsConnected)
        {
            BtnConnect.Content = Loc.T("断 开");
            BtnConnect.Tone = ButtonTone.Danger;
        }
        else if (session.IsBusy)
        {
            BtnConnect.Content = Loc.T("取消连接");
            BtnConnect.Tone = ButtonTone.Danger;
        }
        else
        {
            BtnConnect.Content = Loc.T("连 接");
            BtnConnect.Tone = ButtonTone.Solid;
        }

        LabPeerHint.Text = session.IsConnected
            ? Loc.T("连接后每 3 秒自动刷新；被移出房间的玩家不会出现在这里。")
            : Loc.T("尚未连接：连接成功后这里会列出同房间的对端。");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static bool IsUsableManualIp(string ip)
    {
        var match = Ipv4Pattern.Match(ip);
        if (!match.Success) return false;

        for (var i = 1; i <= 4; i++)
        {
            if (!int.TryParse(match.Groups[i].Value, out var value) || value > 255) return false;
        }

        if (ip.EndsWith(".0", StringComparison.Ordinal) || ip.EndsWith(".255", StringComparison.Ordinal)) return false;

        return true;
    }

    // ————— 公开房间 —————

    private void OnPublishClick(object sender, RoutedEventArgs e)
    {
        var session = Session;

        if (!_publishing)
        {
            if (!session.IsConnected)
            {
                ShowNotice(Loc.T("请先连接联机房间，再公开房间。"), isError: true);
                return;
            }

            if (_hall is not { Connected: true })
            {
                ShowNotice(Loc.T("请先在「大厅」页进入大厅，才能把房间公开给大厅玩家。"), isError: true);
                return;
            }

            _publishing = true;
            _hallChat = _hall.Chat;
            _hallChat?.SetRoomAnnounce(RoomName, session.LocalIp, SelectedNode());
            ShowNotice(Loc.F("已公开房间：{0}（大厅玩家可一键加入）", RoomName));
        }
        else
        {
            _publishing = false;
            _hallChat?.ClearRoomAnnounce();
            ShowNotice(Loc.T("已取消公开房间"));
        }

        SaveForm();
        RefreshPublishUi();
    }

    private void RefreshPublishUi()
    {
        var session = Session;
        var canPublish = session.IsConnected && _hall is { Connected: true };

        if (!canPublish && _publishing)
        {
            _publishing = false;
            _hallChat?.ClearRoomAnnounce();
        }

        BtnPublish.Content = _publishing ? Loc.T("公开中") : Loc.T("未公开");
        BtnPublish.Tone = _publishing ? ButtonTone.Solid : ButtonTone.Outline;

        LabPublishHint.Text = canPublish
            ? Loc.T("房间名会显示在大厅的公开房间列表里，其他玩家可以一键加入。")
            : session.IsConnected
                ? Loc.T("已连接房间，还需要在「大厅」页进入大厅才能公开。")
                : Loc.T("需要先连接房间并进入大厅。");
    }

    // ————— 分享 / 一键加入 —————

    private void OnCopyShareClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveForm();
            Clipboard.SetText(BuildShareText());
            ShowNotice(Loc.T("分享文本已复制到剪贴板，发给队友即可。"));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("复制分享文本失败：{0}", ex.Message));
            ShowNotice(Loc.F("复制失败：{0}", ex.Message), isError: true);
        }
    }

    private void OnShareWindowClick(object sender, RoutedEventArgs e)
    {
        SaveForm();
        new MultiplayerShareWindow(BuildShareText()) { Owner = OwnerWindow }.Show();
    }

    private async void OnJoinShareClick(object sender, RoutedEventArgs e)
    {
        string text;

        try { text = Clipboard.GetText().Trim(); }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("读取剪贴板失败：{0}", ex.Message), isError: true);
            return;
        }

        if (text.Length == 0)
        {
            ShowNotice(Loc.T("剪贴板里没有内容，请先复制队友发来的分享文本。"), isError: true);
            return;
        }

        var info = ShareCode.Parse(text);

        if (info is null)
        {
            ShowNotice(Loc.T("剪贴板内容不是有效的分享文本（应以「HMOL联机模块 组网分享」开头）。"), isError: true);
            return;
        }

        if (!ApplyShareInfo(info)) return;

        ShowNotice(Loc.F("已按分享填入：房间 {0}，节点 {1}", RoomName, SelectedNode()));

        if (Session.IsConnected || Session.IsBusy) return;

        await ConnectInternalAsync(isOwner: false);
    }

    /// <summary>把分享 / 邀请里的参数填进表单。返回 false 表示版本不一致，已提示。</summary>
    private bool ApplyShareInfo(ShareInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Version) &&
            !string.Equals(info.Version, AppInfo.VersionDisplay, StringComparison.OrdinalIgnoreCase))
        {
            ChoiceWindow.Warn(OwnerWindow, Loc.T("版本不匹配"),
                Loc.F("该分享来自不同版本的联机模块：\n\n分享版本：{0}\n当前版本：{1}\n\n请双方升级到相同版本后再联机。", info.Version, AppInfo.VersionDisplay));

            return false;
        }

        _kind = info.Kind;
        _addressMode = info.AddressMode;

        _loadingForm = true;
        TxtRoomName.Text = ChatCrypt.SanitizeText(info.RoomName, MaxRoomNameLength);
        TxtRoomKey.Text = info.Key;

        if (info.AddressMode == NetworkAddressMode.Manual && !string.IsNullOrWhiteSpace(info.ManualIp))
            TxtManualIp.Text = info.ManualIp;

        _loadingForm = false;

        RefreshPlanButtons();
        RefreshNodeOptions();
        SelectNode(info.Node);
        RefreshIpModeUi();

        SaveForm();
        RefreshSharePreview();

        return true;
    }

    // ————— 大厅 —————

    private HallClient EnsureHall()
    {
        if (_hall is not null) return _hall;

        var hall = new HallClient(line => ActivityLog.Write(LogSource.App, line));

        hall.MessageReceived += OnHallMessage;
        hall.InviteReceived += OnHallInvite;

        _hall = hall;
        return hall;
    }

    private async void OnHallClick(object sender, RoutedEventArgs e)
    {
        var hall = EnsureHall();

        if (hall.Connected)
        {
            BtnHall.IsEnabled = false;
            LabHallStatus.Text = Loc.T("正在离开大厅…");

            try
            {
                await hall.LeaveAsync();
            }
            catch (Exception ex)
            {
                Log.Error(Loc.T("离开大厅失败"), ex);
            }
            finally
            {
                // 与进入分支对称：不恢复的话退出一次后按钮会永久灰掉
                BtnHall.IsEnabled = true;
            }

            StopHallLoop();
            _hallChat = null;

            LabHallStatus.Text = Loc.T("未进入大厅");
            RefreshHallButton();
            RefreshPublishUi();

            _hallChatWindow?.Close();
            _hallChatWindow = null;

            return;
        }

        if (string.IsNullOrWhiteSpace(ChatCrypt.SanitizeText(SettingsStore.Current.Nickname, 32)))
        {
            ShowNotice(Loc.T("请先在「设置 → 联机设置」里填写昵称，再进入大厅。"), isError: true);
            RefreshNickname();
            return;
        }

        BtnHall.IsEnabled = false;
        LabHallStatus.Text = Loc.T("正在进入大厅…");

        var progress = new Progress<string>(text => LabHallStatus.Text = text);

        bool ok;

        try
        {
            ok = await hall.JoinAsync(Nickname, CancellationToken.None, progress);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("进入大厅失败"), ex);
            ok = false;
        }
        finally
        {
            BtnHall.IsEnabled = true;
        }

        if (!ok)
        {
            LabHallStatus.Text = Loc.T("进入大厅失败");
            ShowNotice(Loc.T("进入大厅失败：所有大厅信标都不可用（请检查网络或稍后重试）。"), isError: true);
            RefreshHallButton();
            return;
        }

        _hallChat = hall.Chat;
        Session.SetNickname(Nickname);

        LabHallStatus.Text = Loc.F("已进入大厅 · 公网 IP：{0}", (string.IsNullOrEmpty(hall.Ip) ? Loc.T("-") : hall.Ip));
        ShowNotice(Loc.T("已进入联机大厅。"));

        RefreshHallButton();
        RefreshPublishUi();
        StartHallLoop();
        RefreshHall();
    }

    private void RefreshHallButton()
    {
        var connected = _hall is { Connected: true };

        BtnHall.Content = connected ? Loc.T("离开大厅") : Loc.T("进入大厅");
        BtnHall.Tone = connected ? ButtonTone.Danger : ButtonTone.Solid;
    }

    private void StartHallLoop()
    {
        if (_hallLoopTask is { IsCompleted: false }) return;

        _hallLoopCts?.Dispose();
        _hallLoopCts = new CancellationTokenSource();

        var token = _hallLoopCts.Token;

        _hallLoopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(HallPollSeconds), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                // 旧版是主线程 3 秒刷一次；这里同样只在界面线程上取快照，不做网络等待
                try { _ = Dispatcher.InvokeAsync(RefreshHall); }
                catch (Exception ex) { Log.Warn(Loc.F("刷新大厅列表失败：{0}", ex.Message)); }
            }
        }, CancellationToken.None);
    }

    private void StopHallLoop()
    {
        try { _hallLoopCts?.Cancel(); }
        catch { /* 已释放 */ }

        _hallLoopCts?.Dispose();
        _hallLoopCts = null;
        _hallLoopTask = null;
    }

    private void OnHallRefreshClick(object sender, RoutedEventArgs e) => RefreshHall();

    private void RefreshHall()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshHall);
            return;
        }

        var peers = _hall?.PollPeers() ?? [];

        ListHallPeers.ItemsSource = peers
            .Select(peer => new HallRow(
                peer.Sid,
                string.IsNullOrWhiteSpace(peer.Hostname) ? peer.Ipv4 : peer.Hostname,
                string.IsNullOrWhiteSpace(peer.Ipv4) ? "-" : peer.Ipv4,
                peer.Latency))
            .ToList();

        // 公开房间的延迟：优先用房间自己带的，其次按房主 IP / 会话 ID 去在线玩家列表里找
        var latencyByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var latencyBySid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var peer in peers)
        {
            if (!string.IsNullOrWhiteSpace(peer.Latency))
            {
                if (!string.IsNullOrWhiteSpace(peer.Ipv4)) latencyByIp.TryAdd(peer.Ipv4, peer.Latency);
                if (!string.IsNullOrWhiteSpace(peer.Sid)) latencyBySid.TryAdd(peer.Sid, peer.Latency);
            }
        }

        var rooms = _hallChat?.Rooms() ?? [];

        ListPublicRooms.ItemsSource = rooms
            .Select(room =>
            {
                var latency = room.Latency;

                if (string.IsNullOrWhiteSpace(latency))
                    latencyBySid.TryGetValue(room.Id, out latency);

                if (string.IsNullOrWhiteSpace(latency))
                    latencyByIp.TryGetValue(room.RoomIp, out latency);

                return new PublicRoomRow(room.Name, room.Community, latency ?? string.Empty, room.Node);
            })
            .ToList();

        RefreshFriends();
    }

    private void OnHallChatClick(object sender, RoutedEventArgs e)
    {
        if (_hall is not { Connected: true })
        {
            ShowNotice(Loc.T("请先进入大厅。"), isError: true);
            return;
        }

        if (_hallChatWindow is { IsLoaded: true })
        {
            _hallChatWindow.Activate();
            return;
        }

        _hallChat = _hall.Chat;
        _hallChatWindow = new MultiplayerChatWindow(Loc.T("大厅聊天"), Nickname,
            text => _hallChat?.SendText(text) ?? ChatSendStatus.Empty)
        {
            Owner = OwnerWindow
        };

        foreach (var line in _hallMessages) _hallChatWindow.Append(line);

        _hallChatWindow.Closed += (_, _) => _hallChatWindow = null;
        _hallChatWindow.Show();
    }

    private void OnHallMessage(ChatMessage message)
    {
        var line = $"[{TimeText(message.Ts)}] {message.Name}: {message.Text}";

        _hallMessages.Add(line);
        TrimHistory(_hallMessages);

        Dispatch(() => _hallChatWindow?.Append(line));
    }

    private void OnHallInvite(HallInvite invite) => Dispatch(() => HandleInvite(invite));

    private async void HandleInvite(HallInvite invite)
    {
        var message = Loc.F("{0} 邀请你加入他的房间：\n\n房间名：{1}\n", invite.Name, invite.Community) +
                      Loc.F("节点：{0}\n密钥：{1}\n", invite.Node, (string.IsNullOrWhiteSpace(invite.Key) ? Loc.T("(无)") : invite.Key)) +
                      Loc.F("方案：{0}\n\n", NetworkEngineFactory.DisplayName(invite.Kind));

        if (Session.IsConnected || Session.IsBusy)
            message += Loc.T("你当前已在房间中，接受会断开当前连接并加入新房间。");
        else
            message += Loc.T("是否接受并加入？");

        var answer = ChoiceWindow.Confirm(OwnerWindow, Loc.T("收到入房邀请"), message,
            confirmText: Loc.T("接受并加入"), cancelText: Loc.T("拒绝"));

        if (!answer) return;

        var info = new ShareInfo(
            invite.Kind, NetworkEngineFactory.ToPlan(invite.Kind), invite.Node,
            invite.Community, invite.Key, false, invite.AddressMode, invite.ManualIp, invite.Version);

        if (!ApplyShareInfo(info)) return;

        if (Session.IsConnected || Session.IsBusy) await DisconnectInternalAsync();

        ShowNotice(Loc.F("已接受 {0} 的邀请，正在加入房间…", invite.Name));
        GoToCategory(0);

        await ConnectInternalAsync(isOwner: false);
    }

    // ————— 收藏队友 —————

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (ListHallPeers.SelectedItem is not HallRow row || row.Ip.Length == 0 || row.Ip == "-")
        {
            ShowNotice(Loc.T("请先在大厅列表里选中要收藏的玩家。"), isError: true);
            return;
        }

        FriendStore.Upsert(row.Name, row.Ip, RoomName);
        RefreshFriends();
        ShowNotice(Loc.F("已收藏 {0}（{1}）", row.Name, row.Ip));
    }

    private void OnFriendRefreshClick(object sender, RoutedEventArgs e) => RefreshFriends();

    private void RefreshFriends()
    {
        ListFriends.ItemsSource = FriendStore.List()
            .Select(friend => new FriendRow(
                string.IsNullOrWhiteSpace(friend.Name) ? friend.Ip : friend.Name,
                friend.Ip,
                friend.Community,
                $"×{friend.Times}"))
            .ToList();
    }

    private void OnFriendRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ListFriends.SelectedItem is not FriendRow row)
        {
            ShowNotice(Loc.T("请先选中一个收藏的队友。"), isError: true);
            return;
        }

        FriendStore.Remove(row.Ip);
        RefreshFriends();
        ShowNotice(Loc.F("已取消收藏 {0}", row.Name));
    }

    private void OnFriendJoinClick(object sender, RoutedEventArgs e)
    {
        if (ListFriends.SelectedItem is not FriendRow row)
        {
            ShowNotice(Loc.T("请先选中一个收藏的队友。"), isError: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Community))
        {
            try
            {
                Clipboard.SetText(row.Ip);
                ShowNotice(Loc.F("该队友没有房间记录，虚拟 IP 已复制：{0}", row.Ip));
            }
            catch (Exception ex)
            {
                ShowNotice(Loc.F("复制失败：{0}", ex.Message), isError: true);
            }

            return;
        }

        _loadingForm = true;
        TxtRoomName.Text = ChatCrypt.SanitizeText(row.Community, MaxRoomNameLength);
        _loadingForm = false;

        SaveForm();
        RefreshSharePreview();
        GoToCategory(0);

        ShowNotice(Loc.F("已填入房间 {0}，请确认节点后点「连接」。", row.Community));
    }

    // ————— 公开房间 —————

    private void OnPublicRefreshClick(object sender, RoutedEventArgs e) => RefreshHall();

    private void OnJoinPublicClick(object sender, RoutedEventArgs e)
    {
        if (ListPublicRooms.SelectedItem is not PublicRoomRow row)
        {
            ShowNotice(Loc.T("请先选中一个公开房间。"), isError: true);
            return;
        }

        _loadingForm = true;
        TxtRoomName.Text = ChatCrypt.SanitizeText(row.Community, MaxRoomNameLength);
        _loadingForm = false;

        SelectNode(row.Node);
        SaveForm();
        RefreshSharePreview();
        GoToCategory(0);

        ShowNotice(Loc.F("已填入公开房间 {0}（节点 {1}），确认后点「连接」。", row.Community, SelectedNode()));
    }

    // ————— 邀请入房 —————

    private void OnInviteClick(object sender, RoutedEventArgs e)
    {
        var session = Session;

        if (!session.IsConnected || !session.IsOwner)
        {
            ShowNotice(Loc.T("只有已连接房间的房主可以邀请大厅玩家入房。"), isError: true);
            return;
        }

        if (_hall is not { Connected: true })
        {
            ShowNotice(Loc.T("请先在「大厅」页进入大厅，才能邀请玩家。"), isError: true);
            return;
        }

        if (ListHallPeers.SelectedItem is not HallRow row)
        {
            ShowNotice(Loc.T("请先在大厅列表里选中要邀请的玩家。"), isError: true);
            return;
        }

        var request = new HallInviteRequest(
            RoomName, SelectedNode(), RoomKey, _kind, _addressMode, ManualIp, AppInfo.VersionDisplay);

        if (_hall.SendInvite(row.Sid, request)) ShowNotice(Loc.F("已向 {0} 发送入房邀请。", row.Name));
        else ShowNotice(Loc.T("邀请发送失败或过于频繁，请稍后再试。"), isError: true);
    }

    // ————— 对端 —————

    private void RefreshPeers()
    {
        var selected = (ListPeers.SelectedItem as SessionPeer)?.Ip;

        var rows = Session.Peers.ToList();
        ListPeers.ItemsSource = rows;

        if (selected is not null)
        {
            var match = rows.FirstOrDefault(peer => peer.Ip == selected);
            if (match is not null) ListPeers.SelectedItem = match;
        }
    }

    private async void OnPeerRefreshClick(object sender, RoutedEventArgs e)
    {
        try { await Session.RefreshPeersAsync(); }
        catch (Exception ex) { Log.Warn(Loc.F("刷新对端失败：{0}", ex.Message)); }

        RefreshPeers();
    }

    private void OnRoomChatClick(object sender, RoutedEventArgs e)
    {
        if (!Session.IsConnected)
        {
            ShowNotice(Loc.T("请先连接组网。"), isError: true);
            return;
        }

        if (_roomChatWindow is { IsLoaded: true })
        {
            _roomChatWindow.Activate();
            return;
        }

        _roomChatWindow = new MultiplayerChatWindow(Loc.T("房间聊天"), Nickname, text => Session.SendRoomChat(text))
        {
            Owner = OwnerWindow
        };

        foreach (var line in _roomMessages) _roomChatWindow.Append(line);

        _roomChatWindow.Closed += (_, _) => _roomChatWindow = null;
        _roomChatWindow.Show();
    }

    private void RefreshOwnerButtons()
    {
        var session = Session;
        var isOwner = session.IsConnected && session.IsOwner;

        BtnKick.IsEnabled = isOwner;
        BtnUnkick.IsEnabled = isOwner;

        LabOwnerHint.Text = isOwner
            ? Loc.T("你是房主：可以踢出对端或把被移出的玩家拉回。")
            : Loc.T("踢人与拉回仅房主可用（手动点「连接」建房的一方是房主）。");
    }

    private void OnKickClick(object sender, RoutedEventArgs e)
    {
        var session = Session;

        if (!session.IsConnected || !session.IsOwner)
        {
            ShowNotice(Loc.T("只有房主可以踢人。"), isError: true);
            return;
        }

        if (ListPeers.SelectedItem is not SessionPeer peer)
        {
            ShowNotice(Loc.T("请先选中要踢出的对端。"), isError: true);
            return;
        }

        if (!string.IsNullOrEmpty(session.LocalIp) &&
            string.Equals(peer.Ip, session.LocalIp, StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice(Loc.T("不能踢出自己。"), isError: true);
            return;
        }

        var name = string.IsNullOrWhiteSpace(peer.Name) ? peer.Ip : peer.Name;

        var answer = ChoiceWindow.Confirm(OwnerWindow, Loc.T("踢出玩家"),
            Loc.F("确定将 {0}（{1}）移出房间？\n全房间成员会拉黑该玩家，其聊天将被拒绝。", name, peer.Ip),
            confirmText: Loc.T("踢出"), danger: true);

        if (!answer) return;

        session.Kick(peer.Ip, name);
        ShowNotice(Loc.F("已将 {0}（{1}）移出房间。", name, peer.Ip));
        RefreshPeers();
    }

    private void OnUnkickClick(object sender, RoutedEventArgs e)
    {
        var session = Session;

        if (!session.IsConnected || !session.IsOwner)
        {
            ShowNotice(Loc.T("只有房主可以拉回被移出的玩家。"), isError: true);
            return;
        }

        var banned = session.BannedPeers();

        if (banned.Count == 0)
        {
            ShowNotice(Loc.T("当前没有被移出房间的玩家。"));
            return;
        }

        var options = banned
            .Take(6)
            .Select(item => new ChoiceOption(
                string.IsNullOrWhiteSpace(item.Name) ? item.Ip : $"{item.Name}（{item.Ip}）", item.Ip))
            .Append(new ChoiceOption(Loc.T("取消"), "cancel"))
            .ToArray();

        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("拉回被踢玩家"),
            Loc.T("选择要拉回房间的玩家："), Loc.T("拉回后该玩家可以重新收发房间聊天。"), options);

        if (choice is null or "cancel") return;

        session.Unkick(choice);
        ShowNotice(Loc.F("已把 {0} 拉回房间。", choice));
        RefreshPeers();
    }

    // ————— 工具箱 —————

    private void OnNetCheckClick(object sender, RoutedEventArgs e)
        => new MultiplayerNetCheckWindow { Owner = OwnerWindow }.Show();

    private void OnCustomNodesClick(object sender, RoutedEventArgs e) => OpenMoreWindow();

    private void OnMoreSettingsClick(object sender, RoutedEventArgs e) => OpenMoreWindow();

    private void OpenMoreWindow()
    {
        var window = new MultiplayerMoreWindow { Owner = OwnerWindow };

        window.Closed += (_, _) =>
        {
            RefreshNodeOptions();
            SaveForm();
            RefreshSharePreview();
        };

        window.Show();
    }

    private void OnNodeSpeedClick(object sender, RoutedEventArgs e)
    {
        var window = new MultiplayerNodeSpeedWindow(_kind, SelectedNode(), node =>
        {
            SelectNode(node);
            SaveForm();
            RefreshSharePreview();
        })
        {
            Owner = OwnerWindow
        };

        window.Show();
    }

    private void OnSupernodeClick(object sender, RoutedEventArgs e)
        => new MultiplayerSupernodeWindow { Owner = OwnerWindow }.Show();

    private void OnLagCodeClick(object sender, RoutedEventArgs e)
        => new MultiplayerLagCodeWindow { Owner = OwnerWindow }.Show();

    private void OnFileTransferClick(object sender, RoutedEventArgs e)
        => new MultiplayerFileWindow(Session, Nickname) { Owner = OwnerWindow }.Show();

    private void OnHudClick(object sender, RoutedEventArgs e)
    {
        var visible = MultiplayerHub.ToggleHud();
        RefreshHudButton();

        ShowNotice(visible
            ? Loc.T("游戏 HUD 已开启（浮窗可拖动，点浮窗右上角的 × 可隐藏）。")
            : Loc.T("游戏 HUD 已隐藏。"));
    }

    private void RefreshHudButton()
    {
        var visible = MultiplayerHub.HudVisible;
        BtnHud.Content = visible ? Loc.T("游戏 HUD（已开启）") : Loc.T("游戏 HUD");
        BtnHud.Tone = visible ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private async void OnFirewallClick(object sender, RoutedEventArgs e)
    {
        var state = await NetworkToolkit.FirewallStateAsync();

        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("防火墙控制"),
            Loc.F("当前防火墙状态：{0}", state),
            Loc.T("关闭防火墙可以让同一房间的机器互相发现（组网端口与游戏广播走 UDP，常被防火墙拦截）。\n") +
            Loc.T("修改防火墙需要管理员权限，会弹出系统提权确认；不点确认就不会执行任何改动。\n") +
            Loc.T("联机结束后建议恢复开启。"),
            new ChoiceOption(Loc.T("关闭防火墙"), "off", ButtonTone.Danger),
            new ChoiceOption(Loc.T("恢复开启"), "on"),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice is null or "cancel") return;

        var task = choice == "off" ? ElevatedTasks.FirewallOff : ElevatedTasks.FirewallOn;

        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("防火墙控制"), Loc.T("正在执行…"), indeterminate: true, canCancel: false);

        try
        {
            var result = await ElevationHelper.RunElevatedAsync(task, timeout: TimeSpan.FromMinutes(2));

            ShowNotice(result.Ok ? result.Message : Loc.F("操作失败：{0}", result.Message), !result.Ok);

            if (result.Ok) ShowNotice(Loc.F("{0}（当前状态：{1}）", result.Message, await NetworkToolkit.FirewallStateAsync()));
        }
        finally
        {
            progress.Finish();
        }
    }

    private async void OnMetricClick(object sender, RoutedEventArgs e)
    {
        var ip = Session.LocalIp;

        if (string.IsNullOrEmpty(ip))
        {
            ShowNotice(Loc.T("请先连接组网，再调整虚拟网卡优先级。"), isError: true);
            return;
        }

        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("调整虚拟网卡优先级"),
            Loc.F("把虚拟 IP {0} 所在网卡的接口跃点数设为 1。", ip),
            Loc.T("跃点数越小优先级越高；设为 1 是为了让游戏的局域网广播优先走虚拟网卡，避免被真实网卡抢走。\n") +
            Loc.T("这条命令通常需要管理员权限；如果当前不是管理员身份，可能不会生效。"),
            new ChoiceOption(Loc.T("执行"), "ok"),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "ok") return;

        var result = await NetworkToolkit.SetInterfaceMetricAsync(ip, 1);

        if (result.Ok && !ElevationHelper.IsElevated)
        {
            ShowNotice(Loc.F("{0}（当前不是管理员身份，若游戏内仍发现不了房间，请以管理员身份重试）", result.Message));
            return;
        }

        ShowNotice(result.Message, !result.Ok);
    }

    private async void OnTapInstallClick(object sender, RoutedEventArgs e) => await RunTapInstallAsync();

    /// <summary>
    /// 安装并启动 WinIPBroadcast（局域网广播转发服务）。
    /// 走的提权链路与装 TAP 驱动一致：<see cref="ElevationHelper.RunElevatedAsync"/> +
    /// <see cref="ElevatedTasks.WinIpBroadcastInstall"/>，由高权限实例执行 sc 查询 / 静默安装 / net start。
    /// </summary>
    private async void OnWinIpBroadcastClick(object sender, RoutedEventArgs e)
    {
        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("WinIPBroadcast 广播转发服务"),
            Loc.T("安装并启动 WinIPBroadcast 服务，把局域网游戏广播转发到虚拟网卡。"),
            Loc.T("做什么：优先启动已安装的 WinIPBroadcast 服务；没装则运行发行包自带的 WinIPBroadcast-1.6.exe 静默安装后再启动。\n") +
            Loc.T("为什么要管理员：安装 / 启动 Windows 服务需要管理员权限。\n") +
            Loc.T("会执行什么：sc query WinIPBroadcast →（未安装时）WinIPBroadcast-1.6.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART → net start WinIPBroadcast。\n") +
            Loc.T("执行前系统会弹出 UAC 提权窗口，你可以拒绝；除安装 / 启动该服务外不会改动其它系统设置。"),
            new ChoiceOption(Loc.T("确认执行"), "ok", ButtonTone.Danger),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "ok") return;

        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("广播转发服务"), Loc.T("正在安装并启动，请稍候…"), indeterminate: true, canCancel: false);

        try
        {
            var result = await ElevationHelper.RunElevatedAsync(ElevatedTasks.WinIpBroadcastInstall, timeout: TimeSpan.FromMinutes(3));

            ShowNotice(result.Ok ? result.Message : Loc.F("操作失败：{0}", result.Message), !result.Ok);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("安装 WinIPBroadcast 失败"), ex);
            ShowNotice(Loc.F("安装 WinIPBroadcast 失败：{0}", ex.Message), isError: true);
        }
        finally
        {
            progress.Finish();
        }
    }

    /// <summary>
    /// 安装 TAP 驱动：先讲清楚做什么、为什么需要管理员、会执行什么，确认后才提权。
    /// </summary>
    private async Task RunTapInstallAsync()
    {
        if (_tapping) return;

        var existing = await NetworkToolkit.TapCountAsync();

        if (existing > 0)
        {
            ShowNotice(Loc.F("系统里已经有 {0} 个 TAP 虚拟网卡，无需重复安装。", existing));
            return;
        }

        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("安装 TAP 驱动"),
            Loc.T("即将安装 TAP 虚拟网卡驱动，安装后 n2n 方案才能创建虚拟网卡。"),
            Loc.T("做什么：运行发行包自带的 tapinstall.exe，把 runtime\\tap 下的 OemVista.inf / tap0901.cat / tap0901.sys 注册为系统驱动，并创建一块 TAP 虚拟网卡。\n") +
            Loc.T("为什么要管理员：安装与注册驱动会写入系统驱动库，必须以管理员身份执行。\n") +
            Loc.T("会执行什么：tapinstall.exe install OemVista.inf tap0901（工作目录 runtime\\tap）。\n") +
            Loc.T("执行前系统会弹出 UAC 提权窗口，你可以拒绝；除装驱动外不会改动其它系统设置。"),
            new ChoiceOption(Loc.T("确认安装"), "ok", ButtonTone.Danger),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "ok") return;

        _tapping = true;
        BtnTapInstall.IsEnabled = false;

        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("安装 TAP 驱动"), Loc.T("正在安装，请稍候…"), indeterminate: true, canCancel: false);

        try
        {
            var result = await ElevationHelper.RunElevatedAsync(ElevatedTasks.TapInstall);
            ShowNotice(result.Ok ? Loc.T("TAP 驱动安装完成，可以选 n2n 方案连接了。") : Loc.F("TAP 驱动安装失败：{0}", result.Message), !result.Ok);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("安装 TAP 驱动失败"), ex);
            ShowNotice(Loc.F("TAP 驱动安装失败：{0}", ex.Message), isError: true);
        }
        finally
        {
            progress.Finish();
            BtnTapInstall.IsEnabled = true;
            _tapping = false;
        }
    }

    // ————— 组网组件状态（下载在顶层「下载」页） —————

    /// <summary>
    /// 缺当前方案所需的组网组件时提示到「下载」页补齐。页面本身不再用补丁当「门锁」：
    /// 联机功能默认可用，缺组件时连接前会提示并现场下载。
    /// </summary>
    private void RefreshRequiredFilesUi()
    {
        PanContent.IsEnabled = true;
        PanContent.Opacity = 1;

        var hint = BuildRequiredHint();

        if (hint is null)
        {
            _lastRequiredHint = null;
            return;
        }

        if (string.Equals(hint, _lastRequiredHint, StringComparison.Ordinal)) return;

        _lastRequiredHint = hint;
        ShowNotice(hint, isError: true);
    }

    /// <summary>缺什么提示什么：当前方案所需的组网组件（樱花方案不需要）。</summary>
    private string? BuildRequiredHint()
    {
        if (InstanceManager.Current is null) return null;
        if (_sakuraPlan) return null;

        var missing = RuntimeComponents.MissingFor(_kind);
        if (missing.Count == 0) return null;

        var names = string.Join("、", missing.Select(RuntimeComponents.DisplayName));
        return Loc.F("缺少组网组件（{0}），请先到左侧「下载」页下载。", names);
    }

    /// <summary>
    /// 第一次进联机页时弹一次引导，引导去「下载」页取组网组件与樱花 Frp 引擎。只提示一次。
    /// </summary>
    private void MaybePromptRequiredFiles()
    {
        if (MultiplayerSettingsStore.Current.RequiredFilesPrompted) return;

        MultiplayerSettingsStore.Update(settings => settings.RequiredFilesPrompted = true);

        var choice = ChoiceWindow.Ask(OwnerWindow, Loc.T("需要组网组件"),
            Loc.T("联机需要组网组件（EasyTier / n2n / TAP 等），按方案下载到启动器目录的 runtime\\ 下；用樱花 Frp 方案还需要 frpc 引擎（连接时自动下载）。"),
            Loc.T("这些都在左侧「下载」页按需下载；缺组件时连接前也会提示并现场下载。"),
            new ChoiceOption(Loc.T("前往下载"), "go", ButtonTone.Solid),
            new ChoiceOption(Loc.T("稍后"), "later"));

        if (choice != "go") return;

        (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Download);
    }

    // ————— 引擎输出与提示条 —————

    private void AppendEngineLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _engineTexts.Add(line);
        if (_engineTexts.Count > MaxEngineLines) _engineTexts.RemoveRange(0, _engineTexts.Count - MaxEngineLines);

        var block = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = line
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        PanEngineOutput.Children.Add(block);
        while (PanEngineOutput.Children.Count > MaxEngineLines) PanEngineOutput.Children.RemoveAt(0);

        LabEngineEmpty.Visibility = Visibility.Collapsed;
        ScrollEngineOutput.ScrollToEnd();
        UpdateEngineHint();
    }

    private readonly List<string> _engineTexts = [];

    /// <summary>刷新日志视图上的行数与空状态提示。</summary>
    private void UpdateEngineHint()
    {
        if (LabEngineHint is null) return;

        LabEngineHint.Text = _engineTexts.Count == 0 ? string.Empty : Loc.F("共 {0} 行", _engineTexts.Count);
        LabEngineEmpty.Visibility = _engineTexts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>导出引擎日志：把当前累积的输出写成带 BOM 的 UTF-8 文本，方便发给别人排查。</summary>
    private void OnExportEngineLogClick(object sender, RoutedEventArgs e)
    {
        if (_engineTexts.Count == 0)
        {
            ShowNotice(Loc.T("还没有可导出的引擎日志。"), isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("导出联机引擎日志"),
            FileName = $"HMOL_multiplayer_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Filter = Loc.T("文本文件 (*.txt)|*.txt")
        };

        var owner = OwnerWindow;
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        try
        {
            // 带 BOM 写出，记事本 / Excel 打开中文才不乱码
            File.WriteAllText(dialog.FileName, string.Join(Environment.NewLine, _engineTexts), new UTF8Encoding(true));

            Log.Info(Loc.F("已导出 {0} 行联机引擎日志到 {1}", _engineTexts.Count, dialog.FileName));
            ShowNotice(Loc.F("已导出 {0} 行到：\n{1}", _engineTexts.Count, dialog.FileName));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导出联机引擎日志失败：{0}", ex.Message), ex);
            ShowNotice(Loc.F("导出失败：{0}", ex.Message), isError: true);
        }
    }

    /// <summary>只看当前视图：清掉面板与已累积的文本，下次连接会重新记录。</summary>
    private void OnClearEngineLogClick(object sender, RoutedEventArgs e)
    {
        _engineTexts.Clear();
        PanEngineOutput.Children.Clear();
        UpdateEngineHint();
    }

    private void ShowNotice(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        ActivityLog.Write(LogSource.App, message, isError ? ActivityLevel.Warn : ActivityLevel.Info);

        BarNotice.Visibility = Visibility.Visible;
        LabNotice.Text = message;

        BarNotice.SetResourceReference(Border.BackgroundProperty, isError ? "Status.DangerSoft" : "Accent.Faint");
        IconNotice.Icon = isError ? "lucide/triangle-alert" : "lucide/info";
        IconNotice.SetResourceReference(SvgIcon.IconBrushProperty, isError ? "Status.Danger" : "Accent.Base");
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => BarNotice.Visibility = Visibility.Collapsed;

    private void RefreshNickname()
    {
        var raw = ChatCrypt.SanitizeText(SettingsStore.Current.Nickname, 32);

        if (raw.Length == 0)
        {
            LabNickname.Text = Loc.T("未设置");
            LabNicknameHint.Text = Loc.T("还没有联机昵称：请到「设置 → 联机设置」填写后再进入大厅。");
        }
        else
        {
            LabNickname.Text = raw;
            LabNicknameHint.Text = Loc.T("昵称取自「设置 → 联机设置」，改动后重新进入大厅即可生效。");
        }
    }
}
