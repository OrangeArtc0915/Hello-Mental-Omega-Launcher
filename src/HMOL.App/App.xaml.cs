using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Interop;
using HMOL.App.Pages;
using HMOL.App.Services;
using HMOL.App.Theme;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.Extensions;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Packages;
using HMOL.Core.Security;
using HMOL.Core.Updater;

namespace HMOL.App;

public partial class App : Application
{
    /// <summary>单实例互斥量名。进程存活期间一直持有，退出时由系统回收。</summary>
    private const string MutexName = "HMOL.SingleInstance";

    /// <summary>日志文件的大小上限与保留份数。</summary>
    private const long MaxLogFileSize = 8 * 1024 * 1024;
    private const int MaxLogFileCount = 16;

    private Mutex? _singleInstanceMutex;
    private TrayIcon? _tray;
    private ContextMenu? _trayMenu;

    public static Logger? Logger { get; private set; }

    /// <summary>系统托盘是否可用。不可用时主窗口关闭就直接退出（退化为「最小化到任务栏」）。</summary>
    internal static bool TrayAvailable { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 所有对话框窗口统一套上启动器外壳：无系统标题栏、自绘关闭按钮、顶部可拖动。
        // 之所以不用隐式样式：WPF 的隐式样式只认精确类型，放在 Application.Resources 里的
        // 无 key「Style TargetType="Window"」不会作用到 Window 的派生类，而对话框全是派生类。
        // 类处理器注册在 Window 上，派生类实例同样会触发。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

        // 窗口外壳模板（Controls.xaml）里的自绘关闭按钮要绑命令，因为样式表里挂不了事件处理器。
        // 按 Window 类型注册，所有窗口一次生效——CanExecute 必须同时放行，否则按钮呈灰色不可点。
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            AppCommands.CloseWindow,
            (sender, _) => ((Window)sender).Close(),
            (sender, args) => args.CanExecute = true));

        // 提权实例（--elevated <任务名> --result <结果文件>）必须最先分流：
        // 它要和主实例并存，不能占单实例互斥量，也不能建窗口与托盘。
        if (ElevatedTasks.TryParse(e.Args, out var elevated))
        {
            InitializeLogging();
            Log.Info($"以管理员身份执行提权任务：{elevated.Task}");

            var code = 1;

            try { code = ElevatedTasks.RunAsync(elevated).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("提权任务执行失败", ex); }

            Shutdown(code);
            return;
        }

        // 开机自启拉起：注册表里的命令行带 --autostart，启动后只驻留托盘、不显示主窗口
        var autoStart = AutoStartManager.IsAutoStartLaunch(e.Args);

        // 单实例判定必须最先做：重复双击时第二个进程只提示一下然后退出，不显示窗口
        _singleInstanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            InitializeLogging();
            Log.Info("检测到已有实例在运行，本次启动已退出");

            // 自启拉起时已经有实例了，安静退出即可，不弹窗打扰用户
            if (!autoStart)
            {
                ChoiceWindow.Info(null, AppInfo.Name, $"{AppInfo.Name} 已经在运行了。");
            }

            Shutdown();
            return;
        }

        InitializeLogging();
        RunSecuritySelfCheck();

        // 上一次自动更新的收尾：清掉残留的 .new / .new.zip / .old，再把失败标记读走并翻译成中文。
        // 顺序不能反：有失败标记时要先让用户看见它，并跳过本次自动检查（刚失败过，立刻再弹一次没意义）。
        LauncherUpdater.CleanupStaleFiles();
        var updateFailure = LauncherUpdater.TakePendingFailureNote();

        // 注册表里的自启项指向旧路径（程序被挪过目录）时顺手改正，已经正确就一个字节都不动
        AutoStartManager.RepairIfStale();

        // 门锁（严格模式）：必须联网校验通过才放行——读到 false 拦住，**读不到（断网 / 被墙 / 超时）也拦住**。
        // 读不到时会自动再试一轮，最坏等待 = 轮数 × 单次超时（见 SurviveGate.Verify）。
        var verdict = SurviveGate.Verify();

        if (!verdict.Allowed)
        {
            BlockOnSurviveGate(verdict, talkative: !autoStart);
            Shutdown();
            return;
        }

        Log.Info("门锁校验通过，继续启动");

        ThemeService.Initialize(SettingsStore.Current.ThemeMode, SettingsStore.Current.Accent);
        MultiplayerSettingsStore.Load();

        // 扩展：把界面层实际存在的图标登记给核心（清单校验要用），再扫一遍扩展目录。
        // 全程只读文件、只读 JSON，不加载任何程序集、不执行任何代码、不写盘、不联网。
        // 真正渲染在主页 / 设置页进入时再各取一次快照。
        ExtensionIcons.RegisterAvailable(SvgIconLoader.AvailableKeys());
        ExtensionStore.Reload();

        // 未处理异常写进日志并提示，不让界面直接崩掉
        DispatcherUnhandledException += OnUnhandledException;

#if DEBUG
        SelfCheck.Run();
#endif

        var window = new MainWindow();
        MainWindow = window;

        // 托盘先建好：自启模式全靠它把主窗口叫出来
        InitializeTray(window);

        // 自启拉起且托盘可用时只驻留托盘，用户点托盘图标或菜单里的「显示主窗口」才打开界面；
        // 托盘不可用时没人能唤起窗口，那就退化为正常显示，免得用户以为程序没启动
        var stayInTray = autoStart && TrayAvailable;

        if (stayInTray)
        {
            // 这时先不初始化背景音乐，等用户真把主窗口叫出来再放（见 ShowMainWindow）
            Log.Info("以开机自启方式启动，只驻留托盘，不显示主窗口");
        }
        else
        {
            if (autoStart) Log.Warn("自启模式下系统托盘不可用，改为直接显示主窗口");

            // 背景音乐：设置里开着就随程序启动自动播放。MediaPlayer 绑 UI 线程，所以在这里初始化
            BgmPlayer.Initialize();
            window.Show();

            // 启动时自动检测游戏路径（设置里开启时）：后台扫描，发现疑似目录且还没有任何实例时提示一次；
            // 结果只作建议、绝不自动写配置。截图自检模式跳过，免得弹窗打断截图。
            var quiet = false;
#if DEBUG
            quiet = DebugCapture.TryReadPath(e.Args, out _);
#endif
            if (!quiet) _ = SuggestGamePathAsync(window);

            // 自动检查启动器更新：让出一轮消息循环再弹窗，那时弹窗才有 owner，位置和焦点才正常。
            // 自启驻留托盘时不检查（上面那个分支），截图自检模式也不打扰。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                _ = LauncherUpdateFlow.StartupCheckAsync(window, updateFailure, silenced: quiet)));

#if DEBUG
            // 调试期自动截图（--capture-home <png 路径> [--capture-size 宽x高]）：等界面渲染、动效收敛后截一帧，截完直接退出
            if (DebugCapture.TryReadPath(e.Args, out var capturePath))
                DebugCapture.Attach(window, capturePath!, e.Args);
#endif
        }
    }

    // ————— 启动时自动检测游戏路径 —————

    /// <summary>
    /// 设置里开启「启动时自动检测游戏路径」时，在后台扫描固定磁盘。
    /// 只在当前一个实例都没有时提示一次（老用户可以到设置页用「立即检测」），
    /// 结果只作建议：用户点「添加为实例」才创建，绝不静默改写配置。
    /// </summary>
    private static async Task SuggestGamePathAsync(MainWindow window)
    {
        if (!SettingsStore.Current.AutoDetectGamePath) return;
        if (InstanceManager.All.Count > 0) return;

        // 等界面稳定下来再跑，别和启动动画、门锁提示抢
        try { await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true); }
        catch { return; }

        IReadOnlyList<GameDirectoryCandidate> candidates;

        try
        {
            candidates = await Task.Run(() => GameLocator.FindCandidates()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"启动时自动检测游戏路径失败：{ex.Message}");
            return;
        }

        if (candidates.Count == 0)
        {
            Log.Info("启动时自动检测游戏路径：未发现候选目录");
            return;
        }

        Log.Info($"启动时自动检测游戏路径：发现 {candidates.Count} 个候选，首个为 {candidates[0].Directory}");

        var candidate = candidates[0];

        try
        {
            var choice = ChoiceWindow.Ask(window, "发现可能的游戏目录",
                $"在「{candidate.Directory}」发现疑似心灵终结目录。",
                "要把它添加为一个游戏实例吗？\n" +
                "检测结果只作建议，不会自动改动你的配置；也可以到「设置 → 游戏路径」查看全部候选。",
                new ChoiceOption("添加为实例", "add", ButtonTone.Solid),
                new ChoiceOption("忽略", "skip"));

            Log.Info($"启动时自动检测游戏路径：建议窗返回 {choice ?? "<关闭>"}");

            if (choice != "add") return;

            var baseName = Path.GetFileName(
                candidate.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "心灵终结";

            var result = InstanceManager.Add(baseName, candidate.Directory);

            Log.Info($"启动时自动检测：{(result.Success ? "已按建议创建实例" : "未创建实例")}「{baseName}」：{result.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"启动时自动检测的提示处理失败：{ex.Message}");
        }
    }

    // ————— 门锁（远程开关）—————

    /// <summary>
    /// 显示门锁提示并留日志。自启驻留托盘时只记日志、不弹窗，避免开机时打扰用户
    /// （与「已有实例」那处的安静退出保持一致）。
    /// 「读到 false」和「读不到配置」都走这里，但提示词不同，免得让用户把网络问题误当成版本被停用。
    /// </summary>
    private static void BlockOnSurviveGate(SurviveGate.GateVerdict verdict, bool talkative)
    {
        var headline = verdict.Undetected ? "未能验证启动授权，已禁用启动" : SurviveGate.BlockedMessage;

        var reason = verdict.Undetected
            ? $"原因：{SurviveGate.SourcesText} 都读不到配置（断网 / 被墙 / 超时）"
            : verdict.State!.Describe();

        var hint = verdict.Undetected
            ? "请确认网络可用后重新打开；若一直如此，请联系域管理员。"
            : "如果这与你的版本无关，请联系域管理员。";

        Log.Warn($"门锁拦截启动：{headline}（错误代码 {SurviveGate.ErrorCode}；{reason}）");

        if (!talkative) return;

        ChoiceWindow.Ask(null, "启动已暂停", headline,
            $"错误代码：{SurviveGate.ErrorCode}\n{reason}\n\n{hint}",
            new ChoiceOption("退出", "exit", ButtonTone.Danger));
    }

    // ————— 系统托盘 —————

    /// <summary>
    /// 初始化托盘图标（自己用 Shell_NotifyIcon 实现，见 <see cref="TrayIcon"/>）。
    /// 注册失败时保持 <see cref="TrayAvailable"/> 为 false，主窗口关闭即退出。
    /// </summary>
    private void InitializeTray(MainWindow window)
    {
        try
        {
            var tray = new TrayIcon($"{AppInfo.Name} · {MultiplayerHub.Summary}");

            if (!tray.IsAvailable)
            {
                tray.Dispose();
                Log.Warn("系统托盘不可用，主窗口关闭时直接退出");
                return;
            }

            tray.LeftClick += () => ShowMainWindow(window);
            tray.ContextMenuRequested += point => ShowTrayMenu(window, point);

            _trayMenu = BuildTrayMenu(window);
            _tray = tray;
            TrayAvailable = true;

            MultiplayerHub.StateChanged += OnMultiplayerStateChanged;
            UpdateTrayTooltip();

            Log.Info("系统托盘已就绪");
        }
        catch (Exception ex)
        {
            Log.Warn($"初始化系统托盘失败：{ex.Message}");
            _tray = null;
            TrayAvailable = false;
        }
    }

    private ContextMenu BuildTrayMenu(MainWindow window)
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "显示主窗口" };
        show.Click += (_, _) => ShowMainWindow(window);

        var toggle = new MenuItem { Header = "连接联机" };
        toggle.Click += async (_, _) => await ToggleMultiplayerAsync(window);

        var exit = new MenuItem { Header = "退出程序" };
        exit.Click += (_, _) => window.RequestExit();

        menu.Items.Add(show);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        // 每次弹出前刷新文案：反映连接状态，以及主窗口当前是不是还收着（自启驻留时没显示）
        menu.Opened += (_, _) =>
        {
            show.Header = window.IsVisible ? "显示主窗口" : "显示主窗口（当前未显示）";
            toggle.Header = MultiplayerHub.IsConnected || MultiplayerHub.IsBusy ? "断开联机" : "连接联机";
        };

        return menu;
    }

    /// <summary>
    /// 把主窗口从托盘叫出来。自启驻留时是这里首次初始化背景音乐（设置里没启用就不会播放）；
    /// <see cref="BgmPlayer.Initialize"/> 幂等，重复调用没有副作用。
    /// </summary>
    private static void ShowMainWindow(MainWindow window)
    {
        window.ShowFromTray();
        BgmPlayer.Initialize();
    }

    private async Task ToggleMultiplayerAsync(MainWindow window)
    {
        var session = MultiplayerHub.Session;

        if (session.IsConnected || session.IsBusy)
        {
            await session.DisconnectAsync();
            return;
        }

        // 连接参数在联机页上填，托盘只负责把你带到那儿
        ShowMainWindow(window);
        window.SwitchToPage(NavPages.Multiplayer);

        ChoiceWindow.Info(window, "连接联机",
            "请在「联机」页里选好组网方案、节点与房间名，然后点「连接」。");
    }

    private void ShowTrayMenu(MainWindow window, Point devicePoint)
    {
        var menu = _trayMenu;
        if (menu is null) return;

        try
        {
            var point = ToDeviceIndependent(window, devicePoint);

            // 主窗口还没显示过（自启驻留托盘）时它不接 PresentationSource，不能当定位基准，
            // 这时留空 PlacementTarget，改按屏幕绝对坐标摆放
            menu.PlacementTarget = PresentationSource.FromVisual(window) is null ? null : window;
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = point.X;
            menu.VerticalOffset = point.Y;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"弹出托盘菜单失败：{ex.Message}");
        }
    }

    /// <summary>托盘坐标是设备像素，WPF 的菜单定位用的是设备无关单位，缩放屏上必须换算。</summary>
    private static Point ToDeviceIndependent(Visual visual, Point devicePoint)
    {
        try
        {
            var source = PresentationSource.FromVisual(visual);
            var matrix = source?.CompositionTarget?.TransformFromDevice;

            return matrix is null ? devicePoint : matrix.Value.Transform(devicePoint);
        }
        catch
        {
            return devicePoint;
        }
    }

    private void OnMultiplayerStateChanged() => UpdateTrayTooltip();

    private void UpdateTrayTooltip() => _tray?.SetTooltip($"{AppInfo.Name} · {MultiplayerHub.Summary}");


    /// <summary>
    /// 启动安全自检：调试器 / 可疑运行环境 / 程序本体完整性。
    /// 移植自旧版 HMOL_QT/anti_debug.py 的 verify_runtime_integrity(strict=False) + get_self_hash()，
    /// 与旧版行为一致：只写日志告警，绝不因为检测到调试器之类就退出。
    /// </summary>
    private static void RunSecuritySelfCheck()
    {
        try
        {
            var report = StartupSecurityCheck.Verify(strict: false);

            foreach (var note in report.Notes) Log.Info($"[安全] {note}");
            foreach (var issue in report.Issues) Log.Warn($"[安全] {issue}");

            if (IntegrityChecker.SelfPath is { } selfPath)
            {
                var hash = IntegrityChecker.SelfSha256();
                Log.Info($"[安全] 程序文件：{selfPath}，大小：{IntegrityChecker.SelfSize():N0} 字节，" +
                         $"SHA256：{(hash.Length == 0 ? "计算失败" : hash)}");
            }
            else
            {
                Log.Warn("[安全] 取不到自身文件路径，跳过自身哈希");
            }
        }
        catch (Exception ex)
        {
            // 自检本身出问题也不能拦住启动
            Log.Warn($"[安全] 自检异常：{ex.Message}");
        }
    }

    private static void InitializeLogging()
    {
        Paths.Init();
        SettingsStore.Load();

        Logger = new Logger(Paths.Log, MaxLogFileSize, MaxLogFileCount, LogLevel.Debug);
        Log.Init(Logger);

        // 实例列表与四类插件包目录：界面与包管理都依赖这两步，放在日志就绪之后，载入结果才能落盘
        InstanceStore.Load();
        PackageTypes.EnsureDirectories();

        Log.Info($"{AppInfo.Name} 启动，数据目录：{Paths.Data}");
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal("界面线程未处理异常", e.Exception);
        ChoiceWindow.Error(null, $"{AppInfo.Name} 遇到了一个问题", e.Exception.Message);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 先把联机侧收干净（断开引擎、停守护、关 HUD 与文件接收、停自建节点）
        MultiplayerHub.StateChanged -= OnMultiplayerStateChanged;
        MultiplayerHub.Shutdown();

        try { _tray?.Dispose(); }
        catch (Exception ex) { Log.Warn($"释放系统托盘失败：{ex.Message}"); }

        _tray = null;
        _trayMenu = null;
        TrayAvailable = false;

        // 先停背景音乐再落盘：停播会释放媒体文件句柄
        BgmPlayer.Shutdown();
        SettingsStore.Save();
        ThemeService.Shutdown();
        Log.Info($"{AppInfo.Name} 退出");
        Logger?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 给对话框窗口套上启动器外壳（无系统标题栏 + 右上角自绘关闭按钮 + 顶部可拖动）。
    /// 自管外壳的窗口（主窗口、联机 HUD）在 XAML 里用 Style="{x:Null}" 显式退出。
    /// </summary>
    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;

        // Style="{x:Null}" 留下的本地值就是 null，据此识别「自己管外壳」的窗口
        if (window.ReadLocalValue(FrameworkElement.StyleProperty) is null) return;

        // AllowsTransparency 与 WindowChrome 互斥，会直接抛异常
        if (window.AllowsTransparency) return;

        if (Application.Current?.TryFindResource("DialogWindow") is Style shell) window.Style = shell;
    }
}
