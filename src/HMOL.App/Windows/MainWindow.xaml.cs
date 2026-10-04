using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Interop;
using HMOL.App.Layout;
using HMOL.App.Pages;
using HMOL.App.Services;
using HMOL.App.Theme;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.Layout;
using HMOL.Core.Logging;

namespace HMOL.App.Windows;

public partial class MainWindow : Window
{
    /// <summary>进入页面时每个内容块的错峰间隔与最大延迟。</summary>
    private const double StaggerStepMs = 22;
    private const double StaggerMaxDelayMs = 220;
    private const double EnterOffsetY = 16;

    /// <summary>页面缓存：同一页面在会话内只创建一次，切换时只切可见性。</summary>
    private readonly Dictionary<int, LauncherPage> _pages = new();

    /// <summary>可自定义的导航项：元素标识 → 实际控件。标识与 <see cref="LayoutElements"/> 一一对应。</summary>
    private readonly Dictionary<string, NavItem> _layoutTargets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>关于窗口。会话内只创建一次，重复点击时把已有窗口拉到前面。</summary>
    private AboutWindow? _aboutWindow;

    /// <summary>设置页的分类项，下标与 <c>Tag</c> 里的分类序号一一对应。</summary>
    private readonly NavItem[] _setupCategories;

    /// <summary>联机页的分类项（组网 / 大厅 / 对端 / 引擎日志），下标与 <c>Tag</c> 一一对应。</summary>
    private readonly NavItem[] _mpCategories;

    private readonly bool _ready;
    private int _currentPage = -1;
    private bool _glassEnabled;
    private bool _suppressNavCheck;

    /// <summary>离开设置页再回来时接着显示哪个分类。状态放在窗口里，页面只按指令渲染。</summary>
    private int _setupCategory;

    /// <summary>程序化改分类选中态时挡掉 Checked，不然会「选中 → 事件 → 再选中」地转圈。</summary>
    private bool _suppressSetupCategory;

    /// <summary>离开联机页再回来时接着显示哪个分类。</summary>
    private int _mpCategory;

    /// <summary>程序化改联机分类选中态时挡掉 Checked。</summary>
    private bool _suppressMpCategory;

    /// <summary>进入设置页之前停留的页面，供侧栏「返回」用。</summary>
    private int _pageBeforeSettings = NavPages.Home;

    /// <summary>进入联机页之前停留的页面，供侧栏「返回」用。</summary>
    private int _pageBeforeMultiplayer = NavPages.Home;

    /// <summary>用户明确要求退出（托盘菜单 / 程序自身收尾），此时才允许真正关闭窗口。</summary>
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _ready = true;

        // 设置分类项按 Tag 顺序抓成表：切分类时按下标同步选中态，不必写一堆 if
        _setupCategories =
        [
            SetupCatAppearance,
            SetupCatNickname,
            SetupCatWeatherCity,
            SetupCatSites,
            SetupCatBackground,
            SetupCatMusic,
            SetupCatLayout,
            SetupCatHome,
            SetupCatExtensions,
            SetupCatAutoStart,
            SetupCatGamePath,
            SetupCatUpdate,
            SetupCatAbout,
        ];

        // 联机分类项按 Tag 顺序抓成表，理由与设置分类栏相同
        _mpCategories =
        [
            MpCatNetwork,
            MpCatHall,
            MpCatPeers,
            MpCatLog,
        ];

        // 图标是资源引用，写错只会静默不显示；这里记一条自检日志，便于确认真的加载到了
        Log.Info($"主窗口图标：{(Icon is null ? "未设置" : $"{Icon.Width:0}×{Icon.Height:0}")}");

        // 侧栏搭好之后按启用方案落位（等价旧版 _apply_sidebar_state_to_widget）
        CollectLayoutTargets();
        ApplyLayout();

        // 透明度与界面缩放：设置已由 App 启动时载入，这里直接落地
        ApplyAppearance();

        PanBack.SizeChanged += (_, e) => RectForm.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

        Loaded += (_, _) =>
        {
            SwitchToPage(NavPages.Home);
            ApplyBackground();

            // 主题变了要重铺一次：压暗层取的是主题里的窗口底色，得跟着主题走
            ThemeService.ThemeChanged += ApplyBackground;

            // 首次运行：等界面稳定后再判定并弹向导
            QueueFirstRunWizard();
        };

        Closed += (_, _) => ThemeService.ThemeChanged -= ApplyBackground;

        // 关闭按钮按托盘策略走：托盘可用时只收起窗口，真正退出由托盘菜单决定
        Closing += OnClosing;

        // 最小化时停掉背景里的动图与视频，别白烧 CPU
        StateChanged += (_, _) => BackgroundView.SetPaused(WindowState == WindowState.Minimized);
    }

    /// <summary>
    /// 首次运行向导：必须等主窗口完全显示之后再弹，让出一轮消息循环。
    /// 在构造函数或 Loaded 里直接 ShowDialog，向导没有 Owner，会跑到屏幕正中抢焦点，像野弹窗。
    ///
    /// <para>
    /// 判定：没跑过向导 且 一个实例都没有。原设计里的第三条「设置里没记过游戏目录」在 HMOL 不适用——
    /// 游戏目录只存在于实例上（<see cref="GameInstance.GameDir"/>），设置里没有对应字段。
    /// </para>
    ///
    /// <para>
    /// 任一条不成立时顺手把标记补上：老用户升级上来时该字段是新增的、值为 false，
    /// 不补的话他哪天把实例全删了，判定会突然成立，向导就会在一个用了半年的老用户面前弹出来。
    /// </para>
    /// </summary>
    private void QueueFirstRunWizard()
        => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var settings = SettingsStore.Current;

            var isFirstRun = !settings.FirstRunCompleted && InstanceStore.All.Count == 0;

            if (!isFirstRun)
            {
                if (!settings.FirstRunCompleted)
                {
                    settings.FirstRunCompleted = true;
                    SettingsStore.Save();
                }

                return;
            }

            // 首次启动、向导之前：先提示补齐 7-Zip 组件（缺失时可跳过，不影响单机）
            await RequiredFilesFlow.EnsureSevenZipOnStartupAsync(this);

            new ConfigWizardWindow { Owner = this }.ShowDialog();
        }));

    // ————— 关闭与托盘 —————

    /// <summary>从托盘恢复窗口。</summary>
    internal void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        Log.Info("已从系统托盘恢复主窗口");
    }

    /// <summary>真正退出程序（托盘菜单「退出程序」）。</summary>
    internal void RequestExit()
    {
        _exitRequested = true;
        Close();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;

        // 没有托盘就无法恢复窗口，这时关窗即退出（ShutdownMode 是 OnExplicitShutdown，必须自己收尾）
        if (!App.TrayAvailable)
        {
            _exitRequested = true;
            Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
            return;
        }

        e.Cancel = true;
        Hide();
        Log.Info("主窗口已收起到系统托盘（要退出请用托盘菜单）");
    }

    /// <summary>按设置重铺整窗背景。设置页改完直接调这里，不用重启。</summary>
    internal void ApplyBackground()
    {
        var background = SettingsStore.Current.Background;

        BackgroundView.Apply(background.FileName, background.BlurRadius, background.DimPercent, background.FadeMs);
    }

    /// <summary>
    /// 按设置应用主窗口透明度与界面缩放（设置页改完直接调这里，不用重启）。
    /// 缩放用 <see cref="LayoutTransform"/>：参与布局计算，滚动 / 命中测试 / 弹窗都由 WPF 统一换算，
    /// 不会像 RenderTransform 那样只改显示、留着错位的点击热区。
    /// </summary>
    internal void ApplyAppearance()
    {
        var settings = SettingsStore.Current;

        Opacity = Math.Clamp(settings.WindowOpacity, Settings.MinWindowOpacity, Settings.MaxWindowOpacity);

        var scale = Math.Clamp(settings.UiScale, Settings.MinUiScale, Settings.MaxUiScale);

        // 1.0 时清掉变换（等价于不动），避免整棵可视树白白挂一层 ScaleTransform
        RootGrid.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new ScaleTransform(scale, scale);
    }

    // ————— 界面布局 —————

    /// <summary>按元素清单把可自定义的导航项抓成表；标识与控件名字在清单里定义，不各写一份。</summary>
    private void CollectLayoutTargets()
    {
        _layoutTargets.Clear();

        foreach (var info in LayoutElements.All)
        {
            if (FindName(info.ControlName) is NavItem item) _layoutTargets[info.Id] = item;
            else Log.Warn($"布局元素「{info.Id}」找不到控件 {info.ControlName}，本次跳过");
        }
    }

    /// <summary>
    /// 按启用方案重排 / 显隐 / 重命名左侧导航，并落位主页控件的自由定位。
    /// 布局编辑器与设置页改完直接调这里。
    /// </summary>
    internal void ApplyLayout()
    {
        LayoutStore.EnsureLoaded(LayoutElements.Ids);

        var scheme = LayoutStore.Active;
        ApplyScheme(scheme);
        ApplyHomeScheme(scheme);
    }

    /// <summary>把方案里的主页控件坐标应用到主页（主页还没创建时跳过，创建时再补上）。</summary>
    private void ApplyHomeScheme(LayoutScheme scheme)
    {
        if (_pages.TryGetValue(NavPages.Home, out var page) && page is PageHome home) home.ApplyHomeLayout(scheme);
    }

    /// <summary>
    /// 应用一套方案。顺序只在各自的分组容器内调整（页面导航 / 底部「关于」），
    /// 隐藏的导航项留在可视树上只设 Collapsed —— 与旧版「隐藏仍占位」的做法一致。
    /// </summary>
    private void ApplyScheme(LayoutScheme scheme)
    {
        foreach (var containerName in LayoutElements.ContainerNames)
        {
            if (FindName(containerName) is not Panel host) continue;

            host.Children.Clear();

            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var layoutItem in scheme.Items)
            {
                var info = LayoutElements.Find(layoutItem.ElementId);
                if (info is null || !string.Equals(info.ContainerName, containerName, StringComparison.Ordinal)) continue;
                if (!_layoutTargets.TryGetValue(info.Id, out var item)) continue;
                if (!placed.Add(info.Id)) continue;

                ApplyItem(item, info, layoutItem);
                host.Children.Add(item);
            }

            // 方案里缺项时也不能把控件弄丢，按清单顺序补在末尾
            foreach (var info in LayoutElements.All)
            {
                if (!string.Equals(info.ContainerName, containerName, StringComparison.Ordinal)) continue;
                if (!placed.Add(info.Id)) continue;
                if (!_layoutTargets.TryGetValue(info.Id, out var item)) continue;

                ApplyItem(item, info, layoutItem: null);
                host.Children.Add(item);
            }
        }
    }

    private static void ApplyItem(NavItem item, LayoutElementInfo info, LayoutItem? layoutItem)
    {
        item.Text = string.IsNullOrWhiteSpace(layoutItem?.DisplayName) ? info.DisplayName : layoutItem!.DisplayName!;

        // 不可隐藏的元素（设置页是布局编辑器的入口）即便被手工改过的方案标成隐藏也不隐藏，免得用户把自己锁在外面
        var hidden = layoutItem is not null && !layoutItem.Visible;
        item.Visibility = hidden && info.CanHide ? Visibility.Collapsed : Visibility.Visible;

        item.ToolTip = item.Text;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        _glassEnabled = WindowInterop.TryExtendFrameIntoClientArea(handle);

        if (_glassEnabled)
        {
            // 渲染层允许 Alpha 通道通过，外圈留白才能透出桌面并显示自绘投影
            var source = HwndSource.FromHwnd(handle);
            if (source is not null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            Log.Info("窗口已启用玻璃化，边缘投影正常显示");
        }
        else
        {
            // 无法玻璃化时退化为纯色窗口并去掉留白，避免出现黑边
            PanBack.Margin = new Thickness(0);
            RootGrid.SetResourceReference(Panel.BackgroundProperty, "Surface.Window");
            Log.Warn("系统未启用 DWM 合成，窗口退化为纯色模式");
        }
    }

    // ————— 导航 —————

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressNavCheck) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var page)) return;

        SwitchToPage(page);
    }

    /// <summary>切换到指定页面。页面实例会被缓存，切换时只切可见性并触发进入动画。</summary>
    public void SwitchToPage(int page)
    {
        if (_currentPage == page) return;

        var target = GetPage(page);
        var previous = CurrentPage;
        if (ReferenceEquals(previous, target)) return;

        var from = _currentPage;

        previous?.OnLeave();
        if (previous is not null) previous.Visibility = Visibility.Collapsed;

#if DEBUG
        // 自检：页面退出时应当用 StopWhere 把自己的动画收干净（键带页面前缀），
        // 这里记一下还剩哪些正在运行的动画，便于确认没有留下永久运行的动画。
        if (previous is not null)
        {
            var leftover = string.Join(", ", AnimationEngine.RunningKeys);
            Log.Info($"页面自检：离开「{previous.GetType().Name}」后仍在运行的动画" +
                     (leftover.Length == 0 ? "：无" : $"：{leftover}"));
        }
#endif

        // 记下是从哪个页面进的设置 / 联机，它们侧栏里的「返回」要回到那儿
        if (page == NavPages.Settings && from >= 0) _pageBeforeSettings = from;
        if (page == NavPages.Multiplayer && from >= 0) _pageBeforeMultiplayer = from;

        _currentPage = page;
        SyncNavSelection(page);
        ApplySidebarMode(page);
        Log.Info($"切换到页面 {page}");

        target.Visibility = Visibility.Visible;
        target.OnEnter();
        RunEnterAnimation(target);
    }

    /// <summary>当前显示的页面。给自检用。</summary>
    internal LauncherPage? CurrentPage
        => _currentPage >= 0 && _pages.TryGetValue(_currentPage, out var page) ? page : null;

    private LauncherPage GetPage(int page)
    {
        if (_pages.TryGetValue(page, out var cached)) return cached;

        LauncherPage created = page switch
        {
            NavPages.Instances => new PageInstances(),
            NavPages.Packages => new PagePackages(),
            NavPages.Multiplayer => new PageMultiplayer(),
            NavPages.Download => new PageDownload(),
            NavPages.Log => new PageLog(),
            NavPages.Settings => new PageSettings(),
            _ => new PageHome()
        };

        // 全部页面挂在同一个栅格上，靠可见性切换；Collapsed 的子元素不参与布局与渲染
        created.Visibility = Visibility.Collapsed;
        PanContent.Children.Add(created);

        // 主页控件可能带自由定位坐标，创建后立刻落位（此时还没量出尺寸，等首次布局时再算像素）
        if (created is PageHome home) home.ApplyHomeLayout(LayoutStore.Active);

        _pages[page] = created;
        return created;
    }

    /// <summary>把侧栏选中态同步到当前页面。</summary>
    private void SyncNavSelection(int page)
    {
        _suppressNavCheck = true;

        NavHome.IsChecked = page == NavPages.Home;
        NavInstances.IsChecked = page == NavPages.Instances;
        NavPackages.IsChecked = page == NavPages.Packages;
        NavMultiplayer.IsChecked = page == NavPages.Multiplayer;
        NavDownload.IsChecked = page == NavPages.Download;
        NavLog.IsChecked = page == NavPages.Log;
        NavSettings.IsChecked = page == NavPages.Settings;

        _suppressNavCheck = false;
    }

    // ————— 设置页的分类侧栏 —————

    /// <summary>
    /// 在设置页 / 联机页用各自的分类栏顶替主导航栏，切到别的页面再换回来。
    /// 只切三个 StackPanel 自己的 Visibility，主导航项各自的显隐（<see cref="ApplyLayout"/> 设的）不受影响。
    /// </summary>
    private void ApplySidebarMode(int page)
    {
        var inSetup = page == NavPages.Settings;
        var inMultiplayer = page == NavPages.Multiplayer;

        PanSetupNav.Visibility = inSetup ? Visibility.Visible : Visibility.Collapsed;
        PanMultiplayerNav.Visibility = inMultiplayer ? Visibility.Visible : Visibility.Collapsed;
        PanSidebarNav.Visibility = inSetup || inMultiplayer ? Visibility.Collapsed : Visibility.Visible;

        if (inSetup) SelectSetupCategory(_setupCategory);
        else if (inMultiplayer) SelectMultiplayerCategory(_mpCategory);
    }

    private void OnSetupCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressSetupCategory) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var index)) return;

        SelectSetupCategory(index);
    }

    /// <summary>侧栏「返回」：回到进设置页之前停留的那个页面。</summary>
    private void OnSetupBackClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem item) item.IsChecked = false;

        SwitchToPage(_pageBeforeSettings);
    }

    /// <summary>切到某个分类：同步侧栏选中态，再让设置页换上对应的卡片。</summary>
    internal void SelectSetupCategory(int index)
    {
        if (index < 0 || index >= _setupCategories.Length) index = 0;
        _setupCategory = index;

        // 程序化改选中态同样会触发 Checked，这里挡掉避免回环
        _suppressSetupCategory = true;

        for (var i = 0; i < _setupCategories.Length; i++)
            _setupCategories[i].IsChecked = i == index;

        _suppressSetupCategory = false;

        (GetPage(NavPages.Settings) as PageSettings)?.SwitchCategory(index);
    }

    // ————— 联机页的分类侧栏 —————

    private void OnMultiplayerCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressMpCategory) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var index)) return;

        SelectMultiplayerCategory(index);
    }

    /// <summary>侧栏「返回」：回到进联机页之前停留的那个页面。</summary>
    private void OnMultiplayerBackClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem item) item.IsChecked = false;

        SwitchToPage(_pageBeforeMultiplayer);
    }

    /// <summary>切到联机页的某个分类：同步侧栏选中态，再让联机页换上对应的视图。</summary>
    internal void SelectMultiplayerCategory(int index)
    {
        if (index < 0 || index >= _mpCategories.Length) index = 0;
        _mpCategory = index;

        // 程序化改选中态同样会触发 Checked，这里挡掉避免回环
        _suppressMpCategory = true;

        for (var i = 0; i < _mpCategories.Length; i++)
            _mpCategories[i].IsChecked = i == index;

        _suppressMpCategory = false;

        (GetPage(NavPages.Multiplayer) as PageMultiplayer)?.SwitchCategory(index);
    }

    // ————— 页面进入动画 —————

    /// <summary>页面内容错峰进入：逐块淡入并轻微上移。页面自己管入场动画时（见 <see cref="LauncherPage.HandlesEnterAnimation"/>）交给它。</summary>
    private static void RunEnterAnimation(LauncherPage page)
    {
        if (page.HandlesEnterAnimation) return;

        var host = FindStaggerHost(page.Content);
        var targets = host is not null
            ? host.Children.OfType<UIElement>().ToList()
            : new List<UIElement> { page };

        if (!AnimationEngine.IsEnabled)
        {
            foreach (var element in targets) element.Opacity = 1;
            return;
        }

        var index = 0;
        foreach (var element in targets)
        {
            if (element is not FrameworkElement target) continue;

            var offset = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
            target.RenderTransform = offset;
            target.Opacity = 0;

            var delay = Math.Min(index * StaggerStepMs, StaggerMaxDelayMs);

            AnimationEngine.Start($"enter:{target.GetHashCode()}", 0, 1, 280, Ease.OutFluent, v =>
            {
                target.Opacity = v;
                offset.Y = EnterOffsetY * (1 - v);
            }, delayMs: delay);

            index++;
        }
    }

    /// <summary>沿单一子级链路向下找到第一个真正承载多个内容块的面板。</summary>
    private static Panel? FindStaggerHost(object? content)
    {
        while (content is not null)
        {
            if (content is Panel panel) return panel;

            content = content switch
            {
                ScrollViewer scrollViewer => scrollViewer.Content,
                Border border => border.Child,
                ContentControl contentControl => contentControl.Content,
                Decorator decorator => decorator.Child,
                _ => null
            };
        }

        return null;
    }

    // ————— 标题栏按钮 —————

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        try { DragMove(); }
        catch { /* 鼠标已释放时 DragMove 会抛异常，忽略即可 */ }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return false;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => WindowState =
        WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 左侧导航「关于」：打开独立窗口。
    /// 该项单独分组，不影响主页面选中态；点击后主动取消自身选中，避免留下高亮。
    /// 窗口没关就复用，重复点击只把已有窗口激活到前台；关掉之后必须重建 ——
    /// 已关闭的窗口不能再 Show，否则会抛「关闭窗口后，无法设置可见性」。
    /// </summary>
    private void OnAboutNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is NavItem item) item.IsChecked = false;

        if (_aboutWindow is { IsLoaded: true })
        {
            if (_aboutWindow.WindowState == WindowState.Minimized)
                _aboutWindow.WindowState = WindowState.Normal;

            _aboutWindow.Activate();
            return;
        }

        _aboutWindow = new AboutWindow { Owner = this };
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show();
        Log.Info("已打开关于窗口");
    }
}
