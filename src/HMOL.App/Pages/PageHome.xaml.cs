using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Layout;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.Extensions;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Layout;
using HMOL.Core.Logging;
using HMOL.Core.Weather;

namespace HMOL.App.Pages;

/// <summary>主页「切换实例」列表里的一行。列表每次刷新时整份重建，因此不需要变更通知。</summary>
public sealed class SwitchRowItem
{
    public SwitchRowItem(GameInstance instance)
    {
        Instance = instance;
        IconImage = GameIconProvider.Resolve(instance);
    }

    public GameInstance Instance { get; }

    public string Name => Instance.Name;

    public string GameDir => Instance.GameDir;

    public bool IsCurrent => ReferenceEquals(Instance, InstanceStore.Current);

    /// <summary>行首的游戏图标（心灵终结 / 原版 / 尤复 / 其它 Mod 的主程序图标）。</summary>
    public ImageSource? IconImage { get; }

    public bool HasIconImage => IconImage is not null;

    public bool HasFallbackIcon => IconImage is null;

    /// <summary>没有游戏图标时的备用矢量图标：当前实例用播放键，其余用层叠图标。</summary>
    public string Icon => IsCurrent ? "lucide/play" : "lucide/layers";
}

/// <summary>主页「常用网站」小组件里的一行。列表每次刷新时整份重建，因此不需要变更通知。</summary>
public sealed class SiteLinkRow
{
    public SiteLinkRow(SiteLinkSetting link)
    {
        Name = link.DisplayName;
        Url = link.Url;
        Host = link.Host;
    }

    public string Name { get; }

    public string Url { get; }

    public string Host { get; }
}

/// <summary>
/// 主页扩展小组件的一张卡。内容全部来自扩展清单里的声明（文字 / 图标名 / 网址），
/// 配了数据源时主数值换成取回来的那个值；取不到就退回静态内容并给一句弱化提示，
/// 不弹窗、不影响其它小组件。列表每次刷新时整份重建，因此不需要变更通知。
/// </summary>
public sealed class ExtensionWidgetRow
{
    /// <summary>图标名找不到时退回的内置图标。</summary>
    private const string FallbackIcon = "lucide/puzzle";

    public ExtensionWidgetRow(ExtensionInfo info, ExtensionCard card, bool animate)
    {
        Id = info.Id;
        Title = info.DisplayName;
        Icon = ResolveIcon(info.Icon);
        SourceLabel = BuildSourceLabel(info);
        Animate = animate;

        Link = (info.Manifest?.Link ?? string.Empty).Trim();
        LinkLabel = Link.Length == 0 ? string.Empty : $"点击打开 {HostOf(Link)}";

        if (card.Error is { Length: > 0 } error)
        {
            HasError = true;
            ErrorText = $"此扩展暂时不可用：{error}";
            A11yName = $"{Title}：{ErrorText}";
            return;
        }

        HasData = true;
        Value = card.Value;
        HasValue = card.Value.Length > 0;
        Lines = card.Lines;
        Note = card.Note;
        HasNote = card.Note.Length > 0;
        HasLink = Link.Length > 0;

        A11yName = HasValue ? $"{Title}：{Value}" : Title;
    }

    /// <summary>扩展 ID（清单文件名），宿主与无障碍树都用它标识这一张卡。</summary>
    public string Id { get; }

    public string Title { get; }

    public string Icon { get; }

    /// <summary>右上角的来源信息（作者 · 版本）。</summary>
    public string SourceLabel { get; }

    /// <summary>这次刷新后要不要播一次轻量淡入（内容真的变了才播）。</summary>
    public bool Animate { get; }

    public string Value { get; } = string.Empty;

    public bool HasValue { get; }

    /// <summary>文字行（已在清单那一层拼成「标签：内容」）。</summary>
    public IReadOnlyList<string> Lines { get; } = [];

    public bool HasData { get; }

    /// <summary>数据源取不到时的弱化提示。</summary>
    public string Note { get; } = string.Empty;

    public bool HasNote { get; }

    public string ErrorText { get; } = string.Empty;

    public bool HasError { get; }

    /// <summary>清单里配的点击网址；空串表示这张卡不可点。</summary>
    public string Link { get; } = string.Empty;

    public bool HasLink { get; }

    public string LinkLabel { get; } = string.Empty;

    /// <summary>UIA（无障碍）读到的名字。</summary>
    public string A11yName { get; }

    /// <summary>网址的主机名，用于「点击打开 xxx」的提示；解析不出来就原样显示。</summary>
    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string BuildSourceLabel(ExtensionInfo info)
    {
        var parts = new List<string>();

        if (info.Author.Length > 0) parts.Add(info.Author);
        if (info.Version.Length > 0) parts.Add("v" + info.Version.TrimStart('v', 'V'));

        return string.Join(" · ", parts);
    }

    /// <summary>图标名写错时退回默认图标，免得卡片头上空一块。</summary>
    private static string ResolveIcon(string? icon)
    {
        var name = (icon ?? string.Empty).Trim();
        if (name.Length == 0) return FallbackIcon;

        return SvgIconLoader.Get(name) is null ? FallbackIcon : name;
    }
}

/// <summary>
/// 主页：欢迎横幅、三个小组件（现实月历 / 现实天气 / 常用网站）、按扩展清单渲染的扩展小组件，
/// 以及舞台上的当前实例 / 切换实例 / 无实例引导 / 底部提示。
/// 当前实例的读写都走 <see cref="InstanceStore"/>，切换由它写回 SettingsStore。
///
/// 主页控件支持「自由定位」：方案里给了百分比坐标的元素会被摘出流式布局绝对定位，没给坐标的照旧走流式布局。
/// 横幅与小组件都放在舞台之外，不参与自由定位。
///
/// 所有动效都用 <see cref="AnimationEngine"/> 实现，动画键统一带 <see cref="AnimPrefix"/> 前缀，
/// 离开页面时一次性收干净（含运行中状态点的呼吸循环），不留永久运行的动画。
/// 天气查询全程异步，不在 UI 线程等待网络；失败只改文案，不弹窗、不抛异常。
/// </summary>
public partial class PageHome : LauncherPage
{
    /// <summary>主页动画的统一键前缀：离开页面时按它一次性收尾。</summary>
    private const string AnimPrefix = "home:";

    /// <summary>入场：单块时长、同组错峰间隔、起始下移量（总时长控制在 500ms 内）。</summary>
    private const double EnterDurationMs = 220;
    private const double EnterStepMs = 40;
    private const double EnterOffsetY = 12;

    /// <summary>悬停上浮的位移与时长。</summary>
    private const double HoverLiftY = -2;
    private const double HoverLiftMs = 130;

    /// <summary>运行中状态点的呼吸节奏（单向时长，往返一个周期约 1.8 秒）。</summary>
    private const double PulseMs = 900;

    /// <summary>月历表头，周一为一周起点。</summary>
    private static readonly string[] WeekdayShort = ["一", "二", "三", "四", "五", "六", "日"];

    private bool _subscribed;
    private bool _launching;
    private bool _weatherRunning;
    private bool _extensionsRunning;

    /// <summary>扩展小组件的自动刷新定时器（扩展 ID → 定时器）。离开主页时全部停掉。</summary>
    private readonly Dictionary<string, DispatcherTimer> _extensionTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次渲染出的扩展主数值：只有真的变了才播轻量动效。</summary>
    private readonly Dictionary<string, string> _extensionLastValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前生成出来的扩展卡片。离开页面时要把它们自己那层透明度也收干净。</summary>
    private readonly List<FrameworkElement> _extensionCards = [];

    /// <summary>月历当前显示的月份（当月 1 日）。默认是现实月份，可用左右箭头翻看别的月份。</summary>
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>元素 → 它自己的平移变换。入场与悬停共用一份，避免两个动画抢同一个属性。</summary>
    private readonly Dictionary<FrameworkElement, TranslateTransform> _offsets = [];

    /// <summary>运行中状态点的呼吸是否在进行，以及当前是「亮 → 暗」还是「暗 → 亮」。</summary>
    private bool _pulsing;
    private bool _pulseUp = true;

    /// <summary>各主页控件在 XAML 里的流式位置，恢复流式布局时按它还原。</summary>
    private readonly Dictionary<string, FlowSlot> _flowSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前被摘出流式布局、绝对定位中的元素。</summary>
    private readonly HashSet<string> _freeIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在生效的方案。舞台尺寸变化时按它重算像素坐标。</summary>
    private LayoutScheme? _scheme;

    /// <summary>时钟小组件的走秒定时器。</summary>
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public PageHome()
    {
        InitializeComponent();

        CaptureFlowSlots();
        BuildWeekdayHeader();
        RefreshCalendar();
        RefreshSites();

        _clockTimer.Tick += (_, _) => RefreshClock();

        // 窗口缩放时百分比坐标要重算一次，不然会停在旧尺寸上
        PanHomeStage.SizeChanged += (_, _) => RefreshFreeRects();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        Refresh();
    }

    public override void OnEnter()
    {
        Refresh();
        RefreshCalendar();
        RefreshSites();

        _clockTimer.Start();

        // 日期与站点都用不着动画，只有天气要联网；命中 30 分钟缓存时这一步不发请求。
        // 天气块被隐藏时整段跳过，省一次没必要的请求（下次进主页时若已重新显示再补上）。
        if (SettingsStore.Current.HomeWidgets.Weather) _ = RefreshWeatherAsync();

        // 扩展：先取数据再出卡。整个过程不阻塞界面，失败只落在各自的卡上
        _ = RefreshExtensionWidgetsAsync();

        PlayEnterAnimation();
    }

    /// <summary>离开页面：把带 home: 前缀的动画（含呼吸循环）全部停掉，并把状态落到终态。</summary>
    public override void OnLeave()
    {
        _clockTimer.Stop();
        StopHomeAnimations();
    }

    private void StopHomeAnimations()
    {
        _pulsing = false;
        AnimationEngine.StopWhere(key => key.StartsWith(AnimPrefix, StringComparison.Ordinal));

        // 扩展卡有自己的自动刷新节奏，离开主页就停掉，别在别的页面上继续跑
        StopExtensionTimers();

        // 被停掉的动画不会自己跑到终点，这里手动落位：下次进入时入场动画会重新从起点播
        foreach (var (element, _) in EnterPlan())
        {
            element.Opacity = 1;
            OffsetOf(element).Y = 0;
        }

        // 扩展卡自己那层透明度不归入场动画管（它是每张卡各自的淡入），单独收
        foreach (var card in _extensionCards) card.Opacity = 1;

        // 月历切月动画走的是横向位移
        if (CalDays is not null)
        {
            CalDays.Opacity = 1;
            OffsetOf(CalDays).X = 0;
        }

        if (BorStateDot is not null) BorStateDot.Opacity = 1;

        if (IconLaunch?.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }
    }

    // ————— 自由定位 —————

    /// <summary>
    /// 按方案落位主页控件。传 null 或元素没带坐标时，该元素回到 XAML 里的流式位置。
    /// 主窗口在应用布局时调用；编辑器的改动只有点「保存并应用」后才会走到这里。
    /// </summary>
    public void ApplyHomeLayout(LayoutScheme? scheme)
    {
        _scheme = scheme;

        // 旧方案的坐标以「舞台区」为基准，先按实测高度换算到整页基准（只做一次）
        var migrated = scheme is not null && MigrateScheme(scheme);

        foreach (var info in HomeLayoutElements.All)
        {
            if (HomeElement(info.Id) is not { } element) continue;

            var item = scheme?.Find(info.Id);

            if (item is { HasBounds: true }) PlaceFree(info.Id, element, item);
            else RestoreFlow(info.Id, element);
        }

        // 位置与显隐都可能变了，重算一次可见性（自由定位的小组件还要顺手收掉流式里的空列）
        Refresh();

        if (migrated) LayoutStore.Save(scheme!);
    }

    /// <summary>
    /// 把 v1 方案（坐标基准是「舞台区」）换算到 v2（整页内容区）：只改 Y 与 Height，X / Width 的基准没变。
    /// 量不到高度（页面还没排版）就本次不转、等下次，绝不把方案改坏。
    /// </summary>
    private bool MigrateScheme(LayoutScheme scheme)
    {
        if (scheme.Version >= LayoutScheme.CurrentVersion) return false;

        var contentHeight = PanHomeStage.ActualHeight;
        if (contentHeight <= 0) return false;

        var bannerHeight = CardBanner.ActualHeight;
        var widgetsHeight = GrdWidgets.ActualHeight;

        // 旧舞台区：横幅之下（原先还带 10px 上边距）、小组件之上（同样留 10px）
        var stageTop = bannerHeight + 10;
        var stageHeight = Math.Max(1, contentHeight - stageTop - widgetsHeight - 10);

        var converted = 0;

        foreach (var item in scheme.Items)
        {
            if (!item.HasBounds) continue;

            var top = stageTop + item.YPercent!.Value / 100 * stageHeight;
            var height = item.HeightPercent!.Value / 100 * stageHeight;

            item.YPercent = Math.Round(Math.Clamp(top / contentHeight * 100, 0, 100), 1);
            item.HeightPercent = Math.Round(Math.Clamp(height / contentHeight * 100, LayoutItem.MinSizePercent, 100), 1);
            item.ClampBounds();

            converted++;
        }

        scheme.Version = LayoutScheme.CurrentVersion;

        // 没有带坐标的项就只是换个版本号，不必落盘
        if (converted == 0) return false;

        Log.Info($"布局方案「{scheme.Name}」的坐标基准已升级为整页（换算 {converted} 项）");
        return true;
    }

    /// <summary>记住 XAML 里的流式位置（行 / 列 / 跨行跨列 / 外边距）。</summary>
    private void CaptureFlowSlots()
    {
        foreach (var info in HomeLayoutElements.All)
        {
            if (HomeElement(info.Id) is not { } element) continue;
            if (element.Parent is not Panel parent) continue;

            _flowSlots[info.Id] = new FlowSlot(
                parent,
                Grid.GetRow(element),
                Grid.GetColumn(element),
                Grid.GetRowSpan(element),
                Grid.GetColumnSpan(element),
                element.Margin);
        }
    }

    /// <summary>
    /// 摘出流式布局：把元素搬进舞台、占满整格、左上对齐，再用 Margin 顶到目标位置，等价于绝对定位。
    /// 横幅与小组件原本在别的行 / 栅格里，必须搬到舞台才能整页自由定位；搬的是同一个控件实例，
    /// 状态（启动按钮、实例列表、日历）都跟着走，不会重建。
    /// </summary>
    private void PlaceFree(string id, FrameworkElement element, LayoutItem item)
    {
        if (!ReferenceEquals(element.Parent, PanHomeStage))
        {
            (element.Parent as Panel)?.Children.Remove(element);
            PanHomeStage.Children.Add(element);
        }

        Grid.SetRow(element, 0);
        Grid.SetColumn(element, 0);
        Grid.SetRowSpan(element, Math.Max(1, PanHomeStage.RowDefinitions.Count));
        Grid.SetColumnSpan(element, Math.Max(1, PanHomeStage.ColumnDefinitions.Count));

        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;

        // 自由定位的元素压在流式元素之上
        Panel.SetZIndex(element, 1);

        _freeIds.Add(id);
        ApplyBounds(element, item);
    }

    private void RestoreFlow(string id, FrameworkElement element)
    {
        _freeIds.Remove(id);

        if (!_flowSlots.TryGetValue(id, out var slot)) return;

        if (!ReferenceEquals(element.Parent, slot.Parent))
        {
            (element.Parent as Panel)?.Children.Remove(element);
            slot.Parent.Children.Add(element);
        }

        Grid.SetRow(element, slot.Row);
        Grid.SetColumn(element, slot.Column);
        Grid.SetRowSpan(element, slot.RowSpan);
        Grid.SetColumnSpan(element, slot.ColumnSpan);

        element.ClearValue(FrameworkElement.WidthProperty);
        element.ClearValue(FrameworkElement.HeightProperty);
        element.HorizontalAlignment = HorizontalAlignment.Stretch;
        element.VerticalAlignment = VerticalAlignment.Stretch;
        element.Margin = slot.Margin;
        Panel.SetZIndex(element, 0);
    }

    /// <summary>
    /// 百分比换算成像素：宽高按舞台尺寸取比例，位置再夹到舞台内。
    /// 舞台比方案要求的小时（窗口被缩窄）元素跟着收缩并贴边，因此不会溢出、不会盖到侧栏与标题栏。
    /// </summary>
    private void ApplyBounds(FrameworkElement element, LayoutItem item)
    {
        var stageWidth = PanHomeStage.ActualWidth;
        var stageHeight = PanHomeStage.ActualHeight;

        if (stageWidth <= 0 || stageHeight <= 0 || !item.HasBounds) return;

        var width = Math.Min(stageWidth, Math.Max(1, item.WidthPercent!.Value / 100 * stageWidth));
        var height = Math.Min(stageHeight, Math.Max(1, item.HeightPercent!.Value / 100 * stageHeight));

        element.Width = width;
        element.Height = height;
        element.Margin = new Thickness(
            Math.Clamp(item.XPercent!.Value / 100 * stageWidth, 0, stageWidth - width),
            Math.Clamp(item.YPercent!.Value / 100 * stageHeight, 0, stageHeight - height),
            0,
            0);
    }

    private void RefreshFreeRects()
    {
        if (_scheme is null || _freeIds.Count == 0) return;

        foreach (var id in _freeIds.ToArray())
        {
            if (HomeElement(id) is not { } element) continue;

            var item = _scheme.Find(id);
            if (item is { HasBounds: true }) ApplyBounds(element, item);
        }
    }

    /// <summary>按清单里的控件名取主页元素；找不到就安全跳过。</summary>
    private FrameworkElement? HomeElement(string id)
        => HomeLayoutElements.Find(id) is { } info ? FindName(info.ControlName) as FrameworkElement : null;

    /// <summary>一个主页元素在流式布局里的位置（含原来的父容器，供自由定位后还原）。</summary>
    private readonly record struct FlowSlot(Panel Parent, int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin);

    // ————— 动效 —————

    /// <summary>取元素自己的平移变换；没有就挂一个（入场与悬停复用它）。</summary>
    private TranslateTransform OffsetOf(FrameworkElement element)
    {
        if (_offsets.TryGetValue(element, out var found)) return found;

        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        _offsets[element] = transform;

        return transform;
    }

    /// <summary>
    /// 入场计划：元素 + 延迟。分组错峰（横幅 → 舞台 → 小组件），组内 40ms 一档，
    /// 最晚一块 160ms + 220ms ≈ 380ms，整体控制在一屏 500ms 内。
    /// 扩展小组件与日历 / 天气 / 常用网站同组，跟着现有错峰一起入场。
    /// </summary>
    private List<(FrameworkElement Element, double Delay)> EnterPlan()
    {
        var plan = new List<(FrameworkElement Element, double Delay)>();

        AddGroup(0, PanBannerText, BorBannerArt);
        AddGroup(40, CardCurrent, CardSwitch, CardEmpty, CardNote);
        AddGroup(80, CardCalendar, CardWeather, CardSites, PanExtensionWidgets);

        // 简洁模式浮层跟着舞台一起入场（默认模式下它是折叠的，动画无副作用）
        AddGroup(40, PanSimple);

        return plan;

        void AddGroup(double baseDelay, params FrameworkElement[] items)
        {
            for (var i = 0; i < items.Length; i++) plan.Add((items[i], baseDelay + i * EnterStepMs));
        }
    }

    /// <summary>入场：各区块依次淡入并轻微上移。同一 key 会替换旧动画，重复进入不会叠加。</summary>
    private void PlayEnterAnimation()
    {
        var plan = EnterPlan();

        if (!AnimationEngine.IsEnabled)
        {
            // 挂起时不能有副作用：直接把状态落到「已入场」
            foreach (var (element, _) in plan)
            {
                element.Opacity = 1;
                OffsetOf(element).Y = 0;
            }

            return;
        }

        foreach (var (element, delay) in plan)
        {
            var offset = OffsetOf(element);
            offset.Y = EnterOffsetY;
            element.Opacity = 0;

            AnimationEngine.Start($"{AnimPrefix}enter:{element.GetHashCode()}", 0, 1, EnterDurationMs, Ease.OutFluent, v =>
            {
                element.Opacity = v;
                offset.Y = EnterOffsetY * (1 - v);
            }, delayMs: delay);
        }
    }

    /// <summary>卡片悬停：轻微上浮；离开时落回。</summary>
    private void OnCardEnter(object sender, MouseEventArgs e) => Lift(sender as FrameworkElement, true);

    private void OnCardLeave(object sender, MouseEventArgs e) => Lift(sender as FrameworkElement, false);

    private void Lift(FrameworkElement? card, bool hover)
    {
        if (card is null) return;

        // 入场动画还没跑完时别抢同一个 Y，否则两者会互相覆盖
        if (AnimationEngine.RunningKeys.Contains($"{AnimPrefix}enter:{card.GetHashCode()}")) return;

        var offset = OffsetOf(card);

        AnimationEngine.Start($"{AnimPrefix}lift:{card.GetHashCode()}", offset.Y, hover ? HoverLiftY : 0,
            HoverLiftMs, Ease.OutFluent, v => offset.Y = v);
    }

    /// <summary>启动按钮悬停：图标轻微放大；按下时按钮本身会轻微下沉（OutlineButton 已实现）。</summary>
    private void OnLaunchHover(object sender, MouseEventArgs e) => AnimateLaunchIcon(1.14);

    private void OnLaunchLeave(object sender, MouseEventArgs e) => AnimateLaunchIcon(1);

    private void AnimateLaunchIcon(double to)
    {
        if (IconLaunch is null) return;

        IconLaunch.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = IconLaunch.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        IconLaunch.RenderTransform = scale;

        AnimationEngine.Start($"{AnimPrefix}launchIcon", scale.ScaleX, to, 140, Ease.OutBack, v =>
        {
            scale.ScaleX = v;
            scale.ScaleY = v;
        });
    }

    /// <summary>切换实例的行：悬停时轻微右移。</summary>
    private void OnSwitchRowEnter(object sender, MouseEventArgs e) => ShiftRow(sender as FrameworkElement, 3);

    private void OnSwitchRowLeave(object sender, MouseEventArgs e) => ShiftRow(sender as FrameworkElement, 0);

    private void ShiftRow(FrameworkElement? row, double to)
    {
        if (row?.RenderTransform is not TranslateTransform transform) return;

        AnimationEngine.Start($"{AnimPrefix}row:{transform.GetHashCode()}", transform.X, to, 120,
            Ease.OutFluent, v => transform.X = v);
    }

    /// <summary>
    /// 运行中状态点的呼吸（透明度 1 ⇄ 0.35 往复）。
    /// 单向播完在 completed 回调里接着播反向的那一段，因此不需要外部计时器；
    /// 游戏停止或离开页面时 <see cref="StopPulse"/> / <see cref="StopHomeAnimations"/> 会把循环停掉。
    /// </summary>
    private void StartPulse()
    {
        _pulsing = true;

        if (!AnimationEngine.IsEnabled)
        {
            if (BorStateDot is not null) BorStateDot.Opacity = 1;
            return;
        }

        if (AnimationEngine.RunningKeys.Contains($"{AnimPrefix}pulse")) return;

        _pulseUp = true;
        PulseStep();
    }

    private void PulseStep()
    {
        if (!_pulsing) return;

        // 挂起时不能再进入（Start 在挂起状态下会立刻回调 completed，会变成无限递归）
        if (!AnimationEngine.IsEnabled) return;

        var from = _pulseUp ? 1d : 0.35d;
        var to = _pulseUp ? 0.35d : 1d;
        _pulseUp = !_pulseUp;

        AnimationEngine.Start($"{AnimPrefix}pulse", from, to, PulseMs, Ease.InOutFluent, v =>
        {
            if (BorStateDot is not null) BorStateDot.Opacity = v;
        }, PulseStep);
    }

    private void StopPulse()
    {
        _pulsing = false;
        AnimationEngine.Stop($"{AnimPrefix}pulse");

        if (BorStateDot is not null) BorStateDot.Opacity = 1;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;

        _subscribed = true;
        InstanceStore.Changed += OnStoreChanged;
        GameSessionHub.StateChanged += OnSessionChanged;
        BgmPlayer.StateChanged += OnBgmStateChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;

        _subscribed = false;
        InstanceStore.Changed -= OnStoreChanged;
        GameSessionHub.StateChanged -= OnSessionChanged;
        BgmPlayer.StateChanged -= OnBgmStateChanged;

        _clockTimer.Stop();
        StopHomeAnimations();
    }

    private void OnBgmStateChanged() => RunOnUi(RefreshMusicWidget);

    /// <summary>事件可能来自后台线程（如导入实例、游戏退出），统一切回 UI 线程。</summary>
    private void OnStoreChanged() => RunOnUi(Refresh);

    private void OnSessionChanged() => RunOnUi(UpdateLaunchState);

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.InvokeAsync(action);
    }

    // ————— 刷新 —————

    /// <summary>主页是否处于「简洁模式」（只留右下角一个启动入口）。</summary>
    private static bool IsSimpleMode() => SettingsStore.Current.HomeMode == HomeMode.Simple;

    /// <summary>方案里该元素是否显示；没有方案或方案里没记录时默认显示。</summary>
    private bool LayoutVisible(string id)
        => _scheme?.Find(id) is not { } item || item.Visible;

    /// <summary>
    /// 重算所有主页区块的显隐。三个来源叠加：简洁模式、有没有实例 /「主页小组件」开关、布局方案里的显隐。
    /// 自由定位的小组件已从流式行里搬走，所以还要顺手收掉它留下的空列。
    /// </summary>
    private void ApplyVisibility()
    {
        var simple = IsSimpleMode();

        CardBanner.Visibility = Vis(!simple && LayoutVisible("home_banner"));
        GrdWidgets.Visibility = Vis(!simple);
        PanSimple.Visibility = Vis(simple);

        var hasInstance = InstanceManager.All.Count > 0;

        CardEmpty.Visibility = Vis(!simple && !hasInstance && LayoutVisible("home_empty"));
        CardCurrent.Visibility = Vis(!simple && hasInstance && LayoutVisible("home_current"));
        CardSwitch.Visibility = Vis(!simple && hasInstance && LayoutVisible("home_switch"));
        CardNote.Visibility = Vis(!simple && hasInstance && LayoutVisible("home_note"));

        ApplyWidgetVisibility();
        ApplyExtraWidgetVisibility(simple);

        static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>附加小组件（时钟 / 便签 / 快捷启动 / 音乐控制）：设置里打开、方案没隐藏、非简洁模式才显示。</summary>
    private void ApplyExtraWidgetVisibility(bool simple)
    {
        var extras = SettingsStore.Current.ExtraWidgets;

        CardClock.Visibility = Vis(!simple && extras.Clock && LayoutVisible("home_clock"));
        CardMemo.Visibility = Vis(!simple && extras.Memo && LayoutVisible("home_memo"));
        CardMusicWidget.Visibility = Vis(!simple && extras.Music && LayoutVisible("home_music"));

        static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ————— 附加小组件 —————

    /// <summary>刷新时钟 / 便签 / 快捷启动 / 音乐控制的内容。</summary>
    private void RefreshExtraWidgets()
    {
        if (LabClockTime is null) return;

        var extras = SettingsStore.Current.ExtraWidgets;

        RefreshClock();

        LabMemoTitle.Text = string.IsNullOrWhiteSpace(extras.MemoTitle) ? "便签" : extras.MemoTitle;
        LabMemoText.Text = string.IsNullOrWhiteSpace(extras.MemoText)
            ? "便签是空的，去「设置 → 主页设置」里写点什么吧。"
            : extras.MemoText;

        RefreshMusicWidget();
    }

    private void RefreshClock()
    {
        if (LabClockTime is null) return;

        var extras = SettingsStore.Current.ExtraWidgets;
        var now = DateTime.Now;

        LabClockTime.Text = extras.Clock24Hour ? now.ToString("HH:mm:ss") : now.ToString("hh:mm:ss");
        LabClockDate.Text = extras.ClockShowDate ? now.ToString("yyyy 年 M 月 d 日 dddd") : string.Empty;
    }

    private void RefreshMusicWidget()
    {
        if (LabMusicTrack is null) return;

        var hasPlaylist = SettingsStore.Current.Bgm.Playlist.Count > 0;

        BtnMusicPrev.IsEnabled = hasPlaylist;
        BtnMusicNext.IsEnabled = hasPlaylist;
        BtnMusicToggle.IsEnabled = hasPlaylist;

        if (!hasPlaylist)
        {
            LabMusicTrack.Text = "还没有歌";
            LabMusicHint.Text = "在「设置 → 背景音乐」里添加曲目。";
        }
        else
        {
            LabMusicTrack.Text = BgmPlayer.CurrentTrackName;
            LabMusicHint.Text = BgmPlayer.IsPlaying ? "正在播放" : "已暂停";
        }

        BtnMusicToggle.Icon = BgmPlayer.IsPlaying ? "lucide/square" : "lucide/play";
    }

    private void OnMusicPrevClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.Previous();
        RefreshMusicWidget();
    }

    private void OnMusicToggleClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.TogglePlayPause();
        RefreshMusicWidget();
    }

    private void OnMusicNextClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.Next();
        RefreshMusicWidget();
    }

    private void Refresh()
    {
        if (CardCurrent is null) return;

        ApplyVisibility();
        RefreshExtraWidgets();

        var instances = InstanceManager.All;
        var hasInstance = instances.Count > 0;

        RefreshBanner();

        // 简洁模式：实例切换菜单与当前实例名
        PanSimpleSwitch.ItemsSource = instances.Select(instance => new SwitchRowItem(instance)).ToList();
        LabSimpleInstance.Text = hasInstance
            ? InstanceManager.Current?.Name ?? string.Empty
            : "还没有游戏实例";

        if (!hasInstance)
        {
            UpdateLaunchState();
            return;
        }

        PanSwitch.ItemsSource = instances.Select(instance => new SwitchRowItem(instance)).ToList();
        LabSwitchEmpty.Visibility = instances.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;

        RefreshCurrentInstance();
    }

    /// <summary>
    /// 三块小组件：自由定位的按方案显示（它已经不占流式列了），仍在流式里的跟随「主页小组件」开关。
    /// 不能只把卡片 Collapsed——它占的等宽列也得一起收，否则那一条会留下等宽的空白。
    /// 列间的 12px 间距同理，只在相邻两块都还在流式里时留。
    /// </summary>
    private void ApplyWidgetVisibility()
    {
        var simple = IsSimpleMode();
        var widgets = SettingsStore.Current.HomeWidgets;

        var freeCalendar = _freeIds.Contains("home_calendar");
        var freeWeather = _freeIds.Contains("home_weather");
        var freeSites = _freeIds.Contains("home_sites");

        // 还在流式里的才占列；自由定位的已经搬去舞台，列宽一律收成 0
        var flowCalendar = !freeCalendar && widgets.Calendar;
        var flowWeather = !freeWeather && widgets.Weather;
        var flowSites = !freeSites && widgets.Sites;

        // 自由定位的按方案显隐，仍在流式里的跟随「主页小组件」开关；简洁模式下整页只剩浮层
        CardCalendar.Visibility = Vis(!simple && (freeCalendar ? LayoutVisible("home_calendar") : flowCalendar));
        CardWeather.Visibility = Vis(!simple && (freeWeather ? LayoutVisible("home_weather") : flowWeather));
        CardSites.Visibility = Vis(!simple && (freeSites ? LayoutVisible("home_sites") : flowSites));

        ColWidgetCalendar.Width = Star(flowCalendar, 1.1);
        ColWidgetWeather.Width = Star(flowWeather, 1.05);
        ColWidgetSites.Width = Star(flowSites, 0.95);

        ColWidgetGap1.Width = new GridLength(flowCalendar && flowWeather ? 12 : 0);
        ColWidgetGap2.Width = new GridLength(flowWeather && flowSites ? 12 : 0);

        static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

        static GridLength Star(bool show, double ratio)
            => show ? new GridLength(ratio, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>横幅：按时段的问候语 + 一句话状态摘要（有实例说实例名与路径可用性，没实例引导去创建）。</summary>
    private void RefreshBanner()
    {
        LabGreeting.Text = DateTime.Now.Hour switch
        {
            >= 5 and < 12 => "早上好",
            >= 12 and < 18 => "下午好",
            >= 18 and < 23 => "晚上好",
            _ => "夜深了"
        };

        var instance = InstanceManager.Current;

        LabBannerStatus.Text = instance is null
            ? "还没有游戏实例，先创建一个指向心灵终结游戏目录的实例吧。"
            : GameSessionHub.IsRunning
                ? $"当前实例「{instance.Name}」正在运行中。"
                : instance.IsValid
                    ? $"当前实例「{instance.Name}」已就绪，游戏目录可用。"
                    : $"当前实例「{instance.Name}」的游戏目录不可用，请到「游戏实例」页检查路径。";
    }

    private void RefreshCurrentInstance()
    {
        // 有实例时 InstanceStore 一定会给出当前实例；真取不到就只留空态，不做递归刷新
        var instance = InstanceManager.Current;
        if (instance is null) return;

        LabInstanceName.Text = instance.Name;

        // 简洁模式浮层上的实例名（目录失效时补一句，避免用户在简洁模式下看不出问题）
        if (LabSimpleInstance is not null)
            LabSimpleInstance.Text = instance.IsValid ? instance.Name : $"{instance.Name}（目录不可用）";

        LabPath.Text = instance.GameDir;
        LabPath.ToolTip = instance.GameDir;
        LabSummary.Text = instance.Summary;

        LabNote.Text = instance.Note;
        LabNote.Visibility = string.IsNullOrWhiteSpace(instance.Note) ? Visibility.Collapsed : Visibility.Visible;

        LabLastLaunch.Text = FormatLastLaunch(instance);
        LabLastLaunch.ToolTip = LabLastLaunch.Text;

        UpdateLaunchState();
    }

    /// <summary>上次启动时间。写在实例配置里（启动成功时落盘），重启程序后仍然留着。</summary>
    private static string FormatLastLaunch(GameInstance instance)
    {
        if (instance.LastLaunchedAt is not { } at) return "上次启动：还没启动过";

        var text = at.Date == DateTime.Today
            ? $"今天 {at:HH:mm}"
            : at.Year == DateTime.Today.Year
                ? at.ToString("MM-dd HH:mm")
                : at.ToString("yyyy-MM-dd HH:mm");

        return $"上次启动：{text}";
    }

    /// <summary>按「是否运行中 / 目录是否有效」刷新状态胶囊、状态点、启动按钮与提示。</summary>
    private void UpdateLaunchState()
    {
        if (BtnLaunch is null) return;

        var instance = InstanceManager.Current;
        var running = GameSessionHub.IsRunning;

        if (running)
        {
            SetState("运行中", "Status.Success", "Accent.Faint", "Accent.Base");
            StartPulse();
        }
        else
        {
            StopPulse();

            if (instance is { IsValid: true }) SetState("就绪", "Accent.Base", "Accent.Faint", "Status.Success");
            else SetState("目录不可用", "Status.Danger", "Status.DangerSoft", "Status.Danger");
        }

        LabLaunch.Text = running ? "停止游戏" : "启动游戏";
        IconLaunch.Icon = running ? "lucide/square" : "lucide/play";
        BtnLaunch.Tone = running ? ButtonTone.Danger : ButtonTone.Solid;
        BtnLaunch.IsEnabled = running || (instance is { IsValid: true } && !_launching);

        var hint = running
            ? "游戏正在运行，修改插件包前建议先关闭游戏。"
            : instance is { IsValid: true, ExecutablePath: null }
                ? "目录有效但未找到游戏主程序，启动会失败，请检查游戏文件是否完整。"
                : instance is null ? "没有可启动的实例。" : string.Empty;

        LabLaunchHint.Text = hint;
        LabLaunchHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

        // 简洁模式浮层：与主卡片同一状态；没有实例时按钮改为「创建实例」
        if (LabSimpleLaunch is not null)
        {
            if (instance is null)
            {
                LabSimpleLaunch.Text = "创建实例";
                IconSimpleLaunch.Icon = "lucide/circle-plus";
                BtnSimpleLaunch.Tone = ButtonTone.Solid;
                BtnSimpleLaunch.IsEnabled = true;
            }
            else
            {
                LabSimpleLaunch.Text = running ? "停止游戏" : "启动游戏";
                IconSimpleLaunch.Icon = running ? "lucide/square" : "lucide/play";
                BtnSimpleLaunch.Tone = running ? ButtonTone.Danger : ButtonTone.Solid;
                BtnSimpleLaunch.IsEnabled = running || (instance is { IsValid: true } && !_launching);
            }
        }

        void SetState(string text, string foregroundKey, string backgroundKey, string dotKey)
        {
            LabState.Text = text;
            LabState.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
            BorState.SetResourceReference(Border.BackgroundProperty, backgroundKey);
            BorStateDot.SetResourceReference(Border.BackgroundProperty, dotKey);
        }
    }

    // ————— 小组件：现实月历 —————

    /// <summary>星期一为一周起点，与 <see cref="WeekdayShort"/> 的顺序对应。</summary>
    private void BuildWeekdayHeader()
    {
        if (CalWeekdays is null) return;

        CalWeekdays.Children.Clear();
        var style = TryFindResource("CalendarDay") as Style;

        foreach (var name in WeekdayShort)
        {
            var text = new TextBlock { Text = name, FontSize = 11 };
            if (style is not null) text.Style = style;

            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            CalWeekdays.Children.Add(text);
        }
    }

    /// <summary>按 <see cref="_calendarMonth"/> 重画月份标题与日期网格。今天用强调色实心块高亮，非本月日期弱化。</summary>
    private void RefreshCalendar()
    {
        if (CalDays is null) return;

        LabCalendarTitle.Text = $"{_calendarMonth:yyyy 年 M 月}";
        BuildMonthGrid();
    }

    private void BuildMonthGrid()
    {
        CalDays.Children.Clear();

        var today = DateTime.Today;
        var first = _calendarMonth;

        // 从周一起算的前导格数
        var leading = ((int)first.DayOfWeek + 6) % 7;
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        var totalCells = (int)Math.Ceiling((leading + daysInMonth) / 7d) * 7;

        for (var i = 0; i < totalCells; i++) CalDays.Children.Add(BuildDayCell(first.AddDays(i - leading), today));

#if DEBUG
        LogCalendarCheck(today, leading);
#endif
    }

    /// <summary>一格日期：今天=强调色实心块 + 反白文字，本月其余日期=次要文字色，邻月日期=禁用色。</summary>
    private FrameworkElement BuildDayCell(DateTime date, DateTime today)
    {
        var inMonth = date.Month == _calendarMonth.Month && date.Year == _calendarMonth.Year;
        var isToday = date.Date == today.Date;

        var text = new TextBlock { Text = date.Day.ToString(), FontSize = 11.5 };
        if (TryFindResource("CalendarDay") as Style is { } style) text.Style = style;

        var cell = new Border
        {
            Margin = new Thickness(1.5),
            MinHeight = 21,
            CornerRadius = TryFindResource("Radius.Small") is CornerRadius radius ? radius : new CornerRadius(6),
            Child = text
        };

        // 无障碍名称：UIA 读得到「今天 / 具体哪一天」，无脚本也能确认高亮的是不是今天。
        // Border 本身没有 UIA 节点，名称要挂在里面的文字上才读得到。
        var dayName = isToday ? $"今天 {date.Month} 月 {date.Day} 日" : date.Day.ToString();
        AutomationProperties.SetName(text, dayName);
        AutomationProperties.SetName(cell, dayName);

        if (isToday)
        {
            text.FontWeight = FontWeights.Bold;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.OnAccent");
            cell.SetResourceReference(Border.BackgroundProperty, "Accent.Bright");
            return cell;
        }

        text.SetResourceReference(TextBlock.ForegroundProperty, inMonth ? "Text.Secondary" : "Text.Disabled");
        return cell;
    }

#if DEBUG
    /// <summary>自检：记下今天的格子落在第几格、背景色是什么，便于无人值守确认高亮生效。</summary>
    private void LogCalendarCheck(DateTime today, int leading)
    {
        try
        {
            var index = leading + today.Day - 1;
            var inCurrentMonth = today.Month == _calendarMonth.Month && today.Year == _calendarMonth.Year;
            var cell = inCurrentMonth ? CalDays.Children[index] as Border : null;
            var actual = (cell?.Background as SolidColorBrush)?.Color;
            var expected = (TryFindResource("Accent.Bright") as SolidColorBrush)?.Color;

            Log.Info($"月历自检：显示 {_calendarMonth:yyyy-MM} 共 {CalDays.Children.Count} 格（前导补白 {leading}，" +
                     $"当月 {DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month)} 天），" +
                     $"今天 {(inCurrentMonth ? $"在第 {index + 1} 格，背景={Describe(actual)}" : "不在当前显示的月份里")}，" +
                     $"Accent.Bright={Describe(expected)}");
        }
        catch (Exception ex)
        {
            Log.Info($"月历自检失败：{ex.Message}");
        }

        static string Describe(Color? color) => color is { } value ? $"#{value.R:X2}{value.G:X2}{value.B:X2}" : "无";
    }
#endif

    private void OnCalendarPrevClick(object sender, RoutedEventArgs e) => ShiftMonth(-1);

    private void OnCalendarNextClick(object sender, RoutedEventArgs e) => ShiftMonth(1);

    /// <summary>点标题回到现实所在月份。</summary>
    private void OnCalendarTitleClick(object sender, MouseButtonEventArgs e)
    {
        var current = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (_calendarMonth == current) return;

        var direction = _calendarMonth > current ? -1 : 1;
        _calendarMonth = current;

        RefreshCalendar();
        PlayMonthAnimation(direction);
    }

    private void ShiftMonth(int delta)
    {
        _calendarMonth = _calendarMonth.AddMonths(delta);

        RefreshCalendar();
        PlayMonthAnimation(delta);
    }

    /// <summary>切月：整块日期网格按翻页方向轻微横移 + 淡入。</summary>
    private void PlayMonthAnimation(int direction)
    {
        if (CalDays is null) return;

        var offset = OffsetOf(CalDays);
        var from = direction >= 0 ? 10 : -10;

        if (!AnimationEngine.IsEnabled)
        {
            CalDays.Opacity = 1;
            offset.X = 0;
            return;
        }

        CalDays.Opacity = 0;
        offset.X = from;

        AnimationEngine.Start($"{AnimPrefix}month", 0, 1, 180, Ease.OutFluent, v =>
        {
            CalDays.Opacity = v;
            offset.X = from * (1 - v);
        });
    }

    // ————— 小组件：现实天气 —————

    private void OnRefreshWeatherClick(object sender, RoutedEventArgs e) => _ = RefreshWeatherAsync(force: true);

    /// <summary>
    /// 异步查询现实天气（Open-Meteo）。失败只改文案，不弹窗、不抛异常；
    /// 命中 30 分钟缓存时不发请求（<see cref="WeatherService.CacheTtl"/>）。
    /// </summary>
    private async Task RefreshWeatherAsync(bool force = false)
    {
        if (_weatherRunning || PanWeather is null) return;

        _weatherRunning = true;
        if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = false;

        try
        {
            var city = (SettingsStore.Current.WeatherCity ?? string.Empty).Trim();

            if (city.Length == 0)
            {
                PanWeatherNoCity.Visibility = Visibility.Visible;
                PanWeather.Visibility = Visibility.Collapsed;
                LabWeatherCity.Text = string.Empty;
                LabWeatherNote.Visibility = Visibility.Collapsed;
                return;
            }

            PanWeatherNoCity.Visibility = Visibility.Collapsed;
            PanWeather.Visibility = Visibility.Visible;

            LabWeatherCity.Text = city;
            LabWeatherTemp.Text = "…";
            LabWeatherDesc.Text = "正在查询天气…";
            LabWeatherDetail.Text = string.Empty;
            LabWeatherNote.Visibility = Visibility.Collapsed;

            var snapshot = await WeatherService.GetAsync(city, force);

            if (snapshot is null)
            {
                var failure = WeatherService.LastError ?? "天气数据暂时取不到，请稍后再试";

                IcoWeather.Data = WeatherGlyph.For(WeatherKind.Unknown);
                LabWeatherTemp.Text = string.Empty;
                LabWeatherDesc.Text = failure;
                LabWeatherDetail.Text = string.Empty;

                // 城市名本身没解析出来时给的是「去改设置」的提示，网络问题才提网络
                LabWeatherNote.Text = failure.Contains("找不到城市", StringComparison.Ordinal)
                    ? "到「设置 → 天气城市」里换一个城市名再试。"
                    : "天气来自 Open-Meteo，检查网络后点右上角刷新重试。";
                LabWeatherNote.Visibility = Visibility.Visible;
                return;
            }

            IcoWeather.Data = WeatherGlyph.For(snapshot.Kind);

#if DEBUG
            Log.Info($"天气自检：城市={snapshot.City} 温度={snapshot.Temperature:0.#}°C " +
                     $"体感={snapshot.ApparentTemperature:0.#}°C 湿度={snapshot.Humidity:0.#}% " +
                     $"风速={snapshot.WindSpeed:0.#}km/h 类型={snapshot.Kind} 描述={snapshot.Description} " +
                     $"今日={snapshot.TodayHigh:0.#}/{snapshot.TodayLow:0.#}°C 代码={snapshot.WeatherCode} " +
                     $"抓取时间={snapshot.FetchedAt:yyyy-MM-dd HH:mm:ss}");
#endif

            LabWeatherCity.Text = snapshot.City;
            LabWeatherTemp.Text = $"{snapshot.Temperature:0.#}°C";
            LabWeatherDesc.Text = snapshot.Description;

            var parts = new List<string>
            {
                $"体感 {snapshot.ApparentTemperature:0.#}°C",
                $"湿度 {snapshot.Humidity:0.#}%",
                $"风速 {snapshot.WindSpeed:0.#} km/h"
            };

            if (snapshot.TodayHigh is { } high && snapshot.TodayLow is { } low)
                parts.Add($"今日 {high:0.#}/{low:0.#}°C");

            LabWeatherDetail.Text = string.Join(" · ", parts);

            // LastError 仍有值说明这次是退回过期缓存的结果
            if (WeatherService.LastError is { Length: > 0 } reason)
            {
                LabWeatherNote.Text = $"{reason}，当前显示的是缓存数据。";
                LabWeatherNote.Visibility = Visibility.Visible;
            }
            else
            {
                LabWeatherNote.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            // 兜底：天气再怎么样也不能把主页搞崩
            Log.Info($"刷新天气小组件失败：{ex.Message}");
            LabWeatherDesc.Text = "天气数据暂时取不到，请稍后再试";
            LabWeatherDetail.Text = string.Empty;
        }
        finally
        {
            _weatherRunning = false;
            if (BtnRefreshWeather is not null) BtnRefreshWeather.IsEnabled = true;
        }
    }

    // ————— 小组件：常用网站 —————

    private void RefreshSites()
    {
        if (PanSites is null) return;

        var links = SettingsStore.Current.SiteLinks;

        PanSites.ItemsSource = links.Select(link => new SiteLinkRow(link)).ToList();

        LabSitesCount.Text = links.Count > 0 ? $"{links.Count} 个" : string.Empty;
        PanSitesEmpty.Visibility = links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollSites.Visibility = links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>点击站点行：用系统默认浏览器打开（只允许 http / https）。</summary>
    private void OnSiteClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;

        if (!SiteLinkCatalog.IsHttpUrl(url))
        {
            Notify($"网址不是有效的 http/https 地址：{url}\n请到「设置 → 常用网站」里修改。", "打开网站", MessageBoxImage.Warning);
            return;
        }

        ShellHelper.OpenUrl(url);
    }

    private void OnGoSettingsClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Settings);

    // ————— 小组件：扩展模块 —————

    /// <summary>
    /// 加载扩展并刷新它们的卡片。一张扩展出错只影响它自己的卡：出错原因作为可读中文显示在卡内，
    /// 其它小组件（日历 / 天气 / 常用网站 / 其它扩展）照常显示。
    ///
    /// 扩展是纯声明式配置：这里只做「读清单文件 → 按声明拼卡片」，
    /// 不加载程序集、不执行任何扩展提供的代码、不写盘。
    /// </summary>
    private async Task RefreshExtensionWidgetsAsync()
    {
        if (PanExtensionWidgets is null || _extensionsRunning) return;

        _extensionsRunning = true;

        try
        {
            // 只读扫描扩展目录（不存在就返回空，绝不创建目录）
            ExtensionStore.Reload();

            var widgets = ExtensionStore.Widgets;

            if (widgets.Count == 0)
            {
                // 没有扩展就完全不占位：小组件区看起来与以前一模一样
                PanExtensionWidgets.ItemsSource = null;
                PanExtensionWidgets.Visibility = Visibility.Collapsed;

                StopExtensionTimers();
                _extensionCards.Clear();

                return;
            }

            // 第一遍：只用清单里的静态内容与内存缓存出卡（不联网），小组件区立刻可见
            ShowExtensionCards(widgets, widgets.Select(ExtensionDataService.BuildCached).ToList());

            RebuildExtensionTimers(widgets);

            if (!widgets.Any(info => info.HasDataSource)) return;

            // 第二遍：有数据源的并发取一次（单个最多 10 秒超时，失败自动降级成静态内容 + 弱化提示），
            // 取到之后按同一套逻辑重绘；内容真的变了的卡才会播一次淡入。
            var hydrated = await Task.WhenAll(widgets.Select(info => ExtensionDataService.BuildAsync(info)));

            ShowExtensionCards(widgets, hydrated);
        }
        catch (Exception ex)
        {
            // 兜底：扩展再怎么样也不能把主页搞崩，最坏情况就是这一块不显示
            Log.Info($"刷新扩展小组件失败：{ex.Message}");
            PanExtensionWidgets.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _extensionsRunning = false;
        }
    }

    /// <summary>
    /// 把一批卡片画到小组件区。主数值 / 提示真的变了的卡才播一次淡入（首次出现也算「变了」），
    /// 免得自动刷新时整块无谓地闪。
    /// </summary>
    private void ShowExtensionCards(IReadOnlyList<ExtensionInfo> widgets, IReadOnlyList<ExtensionCard> cards)
    {
        var rows = new List<ExtensionWidgetRow>();

        for (var i = 0; i < widgets.Count && i < cards.Count; i++)
        {
            var info = widgets[i];
            var card = cards[i];

            var fingerprint = $"{card.Error}\n{card.Value}\n{card.Note}";
            var changed = !_extensionLastValues.TryGetValue(info.Id, out var last) ||
                          !string.Equals(last, fingerprint, StringComparison.Ordinal);

            _extensionLastValues[info.Id] = fingerprint;

            rows.Add(new ExtensionWidgetRow(info, card, changed));
        }

        _extensionCards.Clear();
        PanExtensionWidgets.ItemsSource = rows;
        PanExtensionWidgets.Visibility = LayoutVisible("home_extensions") && !IsSimpleMode()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 按清单里的 <see cref="ExtensionDataSource.RefreshSeconds"/> 起定时器；
    /// 没配数据源的扩展不需要定时器。间隔太短或太长都会被夹到合理范围。
    /// </summary>
    private void RebuildExtensionTimers(IReadOnlyList<ExtensionInfo> widgets)
    {
        StopExtensionTimers();

        foreach (var info in widgets)
        {
            if (info.Manifest?.DataSource is not { } source) continue;

            var seconds = Math.Clamp(source.RefreshSeconds,
                ExtensionLimits.RefreshSecondsMin, ExtensionLimits.RefreshSecondsMax);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };

            // 任意一个扩展到点就整体重刷一遍：扩展数量很少，这样比逐个维护状态简单可靠
            timer.Tick += (_, _) => _ = RefreshExtensionWidgetsAsync();
            timer.Start();

            _extensionTimers[info.Id] = timer;
        }
    }

    private void StopExtensionTimers()
    {
        foreach (var timer in _extensionTimers.Values) timer.Stop();

        _extensionTimers.Clear();
    }

    /// <summary>
    /// 扩展卡生成后的轻量淡入：只在内容真的变了（或首次出现）时播一次。
    /// 与入场错峰不冲突——入场动画作用于整块容器，这里只作用于单张卡。
    /// 顺手把「配了网址」的卡设成鼠标手型，让用户知道整张卡可以点。
    /// </summary>
    private void OnExtensionCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement card || card.DataContext is not ExtensionWidgetRow row) return;

        if (!_extensionCards.Contains(card)) _extensionCards.Add(card);

        card.Cursor = row.HasLink ? Cursors.Hand : null;

        if (!row.Animate || !AnimationEngine.IsEnabled)
        {
            card.Opacity = 1;
            return;
        }

        var key = $"{AnimPrefix}ext:{card.GetHashCode()}";
        if (AnimationEngine.RunningKeys.Contains(key)) return;

        card.Opacity = 0;
        AnimationEngine.Start(key, 0, 1, 220, Ease.OutFluent, v => card.Opacity = v);
    }

    /// <summary>点击扩展卡：清单里配了网址就用系统默认浏览器打开（只允许 http / https）。</summary>
    private void OnExtensionCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ExtensionWidgetRow row }) return;
        if (!row.HasLink) return;

        if (!SiteLinkCatalog.IsHttpUrl(row.Link))
        {
            Notify($"扩展给出的网址不是有效的 http/https 地址：{row.Link}", "扩展小组件", MessageBoxImage.Warning);
            return;
        }

        ShellHelper.OpenUrl(row.Link);
    }

    // ————— 操作 —————

    private void OnGoCreateClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Instances);

    private void OnSwitchRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;
        if (ReferenceEquals(instance, InstanceStore.Current)) return;

        InstanceManager.SetCurrent(instance.Id);
        Refresh();
    }

    // ————— 简洁模式浮层 —————

    private void OnSimpleSwitchClick(object sender, RoutedEventArgs e)
        => PopSimpleSwitch.IsOpen = !PopSimpleSwitch.IsOpen;

    private void OnSimpleSwitchItemClick(object sender, MouseButtonEventArgs e)
    {
        PopSimpleSwitch.IsOpen = false;

        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;
        if (ReferenceEquals(instance, InstanceStore.Current)) return;

        InstanceManager.SetCurrent(instance.Id);
        Refresh();
    }

    /// <summary>简洁模式的启动按钮：没有实例时跳去「游戏实例」页，其余与主启动按钮完全一致。</summary>
    private void OnSimpleLaunchClick(object sender, RoutedEventArgs e)
    {
        if (InstanceManager.Current is null)
        {
            PopSimpleSwitch.IsOpen = false;
            (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Instances);
            return;
        }

        OnLaunchClick(sender, e);
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (GameSessionHub.IsRunning)
        {
            GameSessionHub.Kill();
            Log.Info("已请求结束游戏进程");
            return;
        }

        var instance = InstanceManager.Current;
        if (instance is null)
        {
            Notify("还没有可启动的实例。", "启动游戏", MessageBoxImage.Warning);
            return;
        }

        _launching = true;
        UpdateLaunchState();

        try
        {
            // 启动成功时 GameSessionHub 会把「上次启动时间」写进实例并落盘
            if (!GameSessionHub.TryLaunch(instance, out var error))
            {
                Notify(error ?? "启动失败：未知原因", "启动游戏", MessageBoxImage.Warning);
                Log.Warn($"启动游戏失败：{error}");
                return;
            }

            ActivityLog.Write(LogSource.App, $"已启动实例「{instance.Name}」");
            Log.Info($"界面：已启动实例「{instance.Name}」");

            RefreshBanner();
            RefreshCurrentInstance();
        }
        finally
        {
            _launching = false;
            UpdateLaunchState();
        }
    }

    private void Notify(string message, string title, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);

        switch (icon)
        {
            case MessageBoxImage.Error:
                ChoiceWindow.Error(owner, title, message);
                break;
            case MessageBoxImage.Warning:
                ChoiceWindow.Warn(owner, title, message);
                break;
            default:
                ChoiceWindow.Info(owner, title, message);
                break;
        }
    }
}
