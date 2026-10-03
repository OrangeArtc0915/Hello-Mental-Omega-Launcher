using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Layout;
using HMOL.App.Services;
using HMOL.App.Theme;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.Appearance;
using HMOL.Core.Extensions;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Layout;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Updater;
using HMOL.Core.Weather;

namespace HMOL.App.Pages;

/// <summary>播放列表里的一行。标题带上「文件不存在」的提示，省得用户对着列表纳闷为什么没声。</summary>
public sealed class BgmTrackRow
{
    public BgmTrackRow(int index, string path, bool isCurrent)
    {
        Index = index;
        FilePath = path;
        IsCurrent = isCurrent;

        var name = System.IO.Path.GetFileName(path);
        Title = File.Exists(path) ? name : $"{name}（文件不存在）";
    }

    public int Index { get; }

    /// <summary>曲目的绝对路径，界面上做悬停提示用。</summary>
    public string FilePath { get; }

    public string Title { get; }

    public bool IsCurrent { get; }
}

/// <summary>设置页「常用网站」列表里的一行。列表每次刷新时整份重建，因此不需要变更通知。</summary>
public sealed class SiteLinkSettingRow
{
    public SiteLinkSettingRow(int index, string name, string url)
    {
        Index = index;
        Name = name;
        Url = url;
    }

    public int Index { get; }

    public string Name { get; }

    public string Url { get; }
}

/// <summary>设置页「扩展模块」列表里的一行。列表每次刷新时整份重建，因此不需要变更通知。</summary>
public sealed class ExtensionRow
{
    public ExtensionRow(ExtensionInfo info)
    {
        Id = info.Id;
        Name = info.DisplayName;
        MetaText = info.MetaText;
        Description = info.Description;
        HasDescription = info.Description.Length > 0;

        IsEnabled = info.Status == ExtensionStatus.Enabled;
        StatusText = $"状态：{info.StatusText}";

        ErrorText = info.Error ?? string.Empty;
        HasError = ErrorText.Length > 0;

        ToggleTone = IsEnabled ? ButtonTone.Plain : ButtonTone.Solid;
        ToggleTip = IsEnabled ? "停用这个扩展（主页上它的卡片会消失）" : "启用这个扩展";

        // 无障碍（UIA）标识：同一行里三个按钮各不相同，读屏与自动化脚本都能定位
        EditId = $"{Id}:edit";
        ToggleId = $"{Id}:toggle";
        DeleteId = $"{Id}:delete";

        A11yName = $"{Name}，{MetaText}，{StatusText}";
        StatusA11yName = HasError ? $"{StatusText}，{ErrorText}" : StatusText;
        EditA11yName = $"编辑扩展 {Name}";
        ToggleA11yName = $"{ToggleTip}：{Name}";
        DeleteA11yName = $"删除扩展 {Name}";
    }

    /// <summary>扩展 ID（清单文件名），增删改都按它定位。</summary>
    public string Id { get; }

    public string Name { get; }

    public string MetaText { get; }

    public string Description { get; }

    public bool HasDescription { get; }

    public bool IsEnabled { get; }

    public string StatusText { get; }

    public string ErrorText { get; }

    public bool HasError { get; }

    public ButtonTone ToggleTone { get; }

    public string ToggleTip { get; }

    public string EditId { get; }

    public string ToggleId { get; }

    public string DeleteId { get; }

    /// <summary>UIA（无障碍）读到的名字。</summary>
    public string A11yName { get; }

    /// <summary>UIA（无障碍）读到的状态。</summary>
    public string StatusA11yName { get; }

    public string EditA11yName { get; }

    public string ToggleA11yName { get; }

    public string DeleteA11yName { get; }
}

/// <summary>
/// 设置页。只做 <see cref="Settings"/> 里已有的字段：主题模式、强调色、窗口透明度、界面缩放、
/// 联机设置（昵称与游戏内 HUD）、天气城市、常用网站、主页背景、背景音乐。
/// 改动立即生效并自动保存。
/// </summary>
public partial class PageSettings : LauncherPage
{
    /// <summary>本页动画：键统一带 <c>set:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("set:");

    /// <summary>程序化改滑块值时不要再回写设置。</summary>
    private bool _suppressSliderChanged;

    /// <summary>程序化改透明度 / 缩放滑块时不要再回写设置。</summary>
    private bool _suppressAppearanceChanged;

    /// <summary>
    /// 透明度 / 缩放控件是否都已创建。构造期 XAML 给滑块设 Minimum 会让值从 0 被夹到最小值，
    /// 从而触发 ValueChanged（此时标签可能还没建好），必须等构造结束才开始响应。
    /// </summary>
    private bool _appearanceReady;

    private bool _suppressNicknameChanged;

    /// <summary>构造期与刷新期不让 HUD 透明度滑块的事件回写到配置。</summary>
    private bool _suppressHudOpacity;

    /// <summary>HUD 透明度滑块是否已经可以响应事件（构造收尾时才置位，理由同 <see cref="_appearanceReady"/>）。</summary>
    private bool _hudOpacityReady;

    private bool _suppressWeatherCityChanged;

    /// <summary>「检查启动器更新」正在进行时不允许重入。</summary>
    private bool _checkingUpdate;

    /// <summary>检查到的新版本。非空时按钮变成「下载并更新到 vX.Y.Z」或「打开下载页」。</summary>
    private LauncherUpdateInfo? _updateInfo;

    public PageSettings()
    {
        InitializeComponent();

        LabDataPath.Text = Paths.Data;
        LabExtensionsPath.Text = ExtensionStore.RootDirectory;

        // 入场计划：页头 → 各设置卡（延迟到 200ms 封顶，整体在 500ms 内）
        _anim.Group(0, HeaderSettings);
        _anim.Group(40,
            CardAppearance,
            CardNickname,
            CardWeatherCity,
            CardSites,
            CardBackground,
            CardMusic,
            CardLayout,
            CardExtensions,
            CardAutoStart,
            CardGamePath,
            CardUpdate,
            CardAbout);

        RefreshSelection();
        RefreshAppearance();
        RefreshNickname();
        RefreshHudOpacity();
        RefreshHudToggle();
        RefreshWeatherCity();
        RefreshSites();
        RefreshBackground();
        RefreshMusic();
        RefreshLayout();
        RefreshAutoStart();
        RefreshExtensions();
        RefreshUpdate();

        // 构造收尾：此时滑块与标签都建好了，之后才允许响应 ValueChanged（构造期的夹值事件必须忽略）
        _appearanceReady = true;
        _hudOpacityReady = true;

        // 自动换曲、播放失败跳过等状态变化都要反映到界面
        BgmPlayer.StateChanged += OnBgmStateChanged;

        // HUD 可能在别处（联机页 / 托盘 / HUD 自己的 × 按钮）被开关，这里跟着刷新按钮状态
        MultiplayerHub.StateChanged += OnHubStateChanged;
    }

    public override void OnEnter()
    {
        RefreshSelection();
        RefreshAppearance();
        RefreshNickname();
        RefreshHudOpacity();
        RefreshHudToggle();
        RefreshWeatherCity();
        RefreshSites();
        RefreshBackground();
        RefreshMusic();
        RefreshLayout();
        RefreshAutoStart();

        // 扩展列表：只读一遍扩展目录（不执行任何扩展内容、不写盘）
        RefreshExtensions();
        RefreshUpdate();

        // 首次进入时弹一次扩展模块的安全说明；排到本页入场动画之后再弹，别打断动效
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowExtensionNoticeOnce));

        _anim.Play();
    }

    /// <summary>离开页面时补一次落盘，避免改了昵称 / 城市没点保存就切走；同时收掉本页动画。</summary>
    public override void OnLeave()
    {
        CommitNickname();
        CommitWeatherCity();

        _anim.Stop();
    }

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    // ————— 分类卡片 —————

    /// <summary>设置分类数量。窗口侧栏的分类项、下面的卡片表都按这个数对齐。</summary>
    private const int CategoryCount = 12;

    /// <summary>一个分类一张卡片，只给自检用。</summary>
    public override int SubViewCount => CategoryCount;

    /// <summary>
    /// 自检也走窗口那条路：选中态、侧栏切换、卡片显隐三件事一次全覆盖，测的就是用户真实点一下走的路。
    /// </summary>
    public override void SelectSubView(int index)
        => (Window.GetWindow(this) as MainWindow)?.SelectSetupCategory(index);

    /// <summary>
    /// 分类卡片同处一格，只把选中的那张显示出来，折叠的卡片不占布局。
    /// 侧栏的选中态由 <see cref="MainWindow"/> 负责，这里只管卡片本身。
    /// </summary>
    internal void SwitchCategory(int index)
    {
        if (index < 0 || index >= CategoryCount) index = 0;

        SurfaceCard[] cards =
        [
            CardAppearance,
            CardNickname,
            CardWeatherCity,
            CardSites,
            CardBackground,
            CardMusic,
            CardLayout,
            CardExtensions,
            CardAutoStart,
            CardGamePath,
            CardUpdate,
            CardAbout,
        ];

        for (var i = 0; i < cards.Length; i++)
            cards[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        // 换分类后回到顶部，否则会停在上一个分类的滚动位置
        ScrollCategory.ScrollToTop();
    }

    // ————— 首次运行向导 —————

    /// <summary>
    /// 老用户的手动入口：无条件开窗，既不做首次运行判定、也不动那个标记。
    /// </summary>
    private void OnRunWizardClick(object sender, RoutedEventArgs e)
        => new ConfigWizardWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    // ————— 主题与强调色 —————

    private void OnThemeModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<ThemeMode>(tag, out var mode)) return;

        ThemeService.SetTheme(mode, ThemeService.Accent);
        SettingsStore.Save();
        RefreshSelection();

        Log.Info($"主题模式已切换为 {tag}");
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<AccentTheme>(tag, out var accent)) return;

        ThemeService.SetTheme(ThemeService.Mode, accent);
        SettingsStore.Save();
        RefreshSelection();

        Log.Info($"强调色已切换为 {tag}");
    }

    private void RefreshSelection()
    {
        BtnThemeLight.Tone = ThemeService.Mode == ThemeMode.Light ? ButtonTone.Solid : ButtonTone.Outline;
        BtnThemeDark.Tone = ThemeService.Mode == ThemeMode.Dark ? ButtonTone.Solid : ButtonTone.Outline;
        BtnThemeSystem.Tone = ThemeService.Mode == ThemeMode.System ? ButtonTone.Solid : ButtonTone.Outline;

        BtnAccentDefault.Tone = AccentTone(AccentTheme.Default);
        BtnAccentBlue.Tone = AccentTone(AccentTheme.Blue);
        BtnAccentGreen.Tone = AccentTone(AccentTheme.Green);
        BtnAccentPurple.Tone = AccentTone(AccentTheme.Purple);

        ButtonTone AccentTone(AccentTheme theme)
            => ThemeService.Accent == theme ? ButtonTone.Solid : ButtonTone.Outline;
    }

    // ————— 窗口透明度 / 界面缩放 —————

    private void OnWindowOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceReady || _suppressAppearanceChanged) return;

        var value = Math.Round(e.NewValue, 2);
        if (Math.Abs(value - SettingsStore.Current.WindowOpacity) < 0.001) return;

        SettingsStore.Current.WindowOpacity = value;
        SettingsStore.Save();

        RefreshAppearanceLabels();
        ApplyAppearanceToWindow();
    }

    private void OnUiScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceReady || _suppressAppearanceChanged) return;

        var value = Math.Round(e.NewValue, 2);
        if (Math.Abs(value - SettingsStore.Current.UiScale) < 0.001) return;

        SettingsStore.Current.UiScale = value;
        SettingsStore.Save();

        RefreshAppearanceLabels();
        ApplyAppearanceToWindow();
    }

    /// <summary>
    /// 卡片 / 组件透明度：只重算承载面画刷，不走整窗口的 <see cref="ApplyAppearanceToWindow"/>，
    /// 也不触发 <c>ThemeChanged</c>，省得主页背景跟着白重铺一遍。
    /// </summary>
    private void OnSurfaceOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceReady || _suppressAppearanceChanged) return;

        var value = Math.Round(e.NewValue, 2);
        if (Math.Abs(value - SettingsStore.Current.SurfaceOpacity) < 0.001) return;

        SettingsStore.Current.SurfaceOpacity = value;
        SettingsStore.Save();

        RefreshAppearanceLabels();
        ThemeService.ApplySurfaceOpacity();
    }

    private void RefreshAppearance()
    {
        if (SldOpacity is null || SldScale is null || SldSurfaceOpacity is null) return;

        _suppressAppearanceChanged = true;
        SldOpacity.Value = Math.Clamp(SettingsStore.Current.WindowOpacity, Settings.MinWindowOpacity, Settings.MaxWindowOpacity);
        SldScale.Value = Math.Clamp(SettingsStore.Current.UiScale, Settings.MinUiScale, Settings.MaxUiScale);
        SldSurfaceOpacity.Value = Math.Clamp(SettingsStore.Current.SurfaceOpacity, Settings.MinSurfaceOpacity, Settings.MaxSurfaceOpacity);
        _suppressAppearanceChanged = false;

        RefreshAppearanceLabels();
    }

    private void RefreshAppearanceLabels()
    {
        if (LabOpacity is not null) LabOpacity.Text = $"{SettingsStore.Current.WindowOpacity * 100:0}%";
        if (LabScale is not null) LabScale.Text = $"{SettingsStore.Current.UiScale:0.00}×";
        if (LabSurfaceOpacity is not null) LabSurfaceOpacity.Text = $"{SettingsStore.Current.SurfaceOpacity * 100:0}%";
    }

    /// <summary>让主窗口按最新设置重设透明度与缩放。</summary>
    private void ApplyAppearanceToWindow()
        => (Window.GetWindow(this) as MainWindow)?.ApplyAppearance();

    // ————— 程序更新 —————

    /// <summary>刷新更新相关的控件：开关、线路选择、上次检查时间，以及按钮的三态文案。</summary>
    private void RefreshUpdate()
    {
        if (BtnAutoCheckUpdate is null || BtnCheckUpdate is null) return;

        var enabled = SettingsStore.Current.CheckUpdateOnStartup;

        BtnAutoCheckUpdate.Content = enabled ? "已开启" : "已关闭";
        BtnAutoCheckUpdate.Tone = enabled ? ButtonTone.Solid : ButtonTone.Outline;

        var source = SettingsStore.Current.LauncherUpdateSource;

        BtnUpdateSourceAuto.Tone = source == LauncherUpdateSource.Auto ? ButtonTone.Solid : ButtonTone.Outline;
        BtnUpdateSourceGitHub.Tone = source == LauncherUpdateSource.GitHub ? ButtonTone.Solid : ButtonTone.Outline;
        BtnUpdateSourceGitee.Tone = source == LauncherUpdateSource.Gitee ? ButtonTone.Solid : ButtonTone.Outline;

        LabUpdateLastCheck.Text = SettingsStore.Current.LastUpdateCheck is { } checkedAt
            ? $"上次检查：{checkedAt:yyyy-MM-dd HH:mm}"
            : "上次检查：还没有检查过";

        // 三态按钮：还没有结果时是「检查」；查到了但发布页没挂可自动更新的文件时只能去发布页
        BtnCheckUpdate.Content = _updateInfo switch
        {
            null => "检查启动器更新",
            { Asset: null } => "打开下载页",
            _ => $"下载并更新到 v{_updateInfo.Version}"
        };

        BtnCheckUpdate.Tone = ButtonTone.Solid;
    }

    private void OnAutoCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        var enabled = !SettingsStore.Current.CheckUpdateOnStartup;

        SettingsStore.Current.CheckUpdateOnStartup = enabled;
        SettingsStore.Save();

        RefreshUpdate();
        SetUpdateStatus(enabled
            ? "已开启：下次启动会在后台检查一次新版本。"
            : "已关闭：启动时不再检查，仍可点下面的按钮手动检查。", warn: false);

        Log.Info($"启动时自动检查更新已{(enabled ? "开启" : "关闭")}");
    }

    private void OnUpdateSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<LauncherUpdateSource>(tag, out var source)) return;

        SettingsStore.Current.LauncherUpdateSource = source;
        SettingsStore.Save();

        RefreshUpdate();
        SetUpdateStatus($"更新线路已改为「{tag}」。", warn: false);

        Log.Info($"更新线路已切换为 {tag}");
    }

    /// <summary>
    /// 三态按钮：第一次点是检查；查出新版本后变成「下载并更新到 vX.Y.Z」或「打开下载页」。
    /// 更新成功后本进程会自行退出，由替换脚本换掉 exe 再重新打开。
    /// </summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;

        var window = Window.GetWindow(this);
        if (window is null) return;

        if (_updateInfo is { } known)
        {
            await LauncherUpdateFlow.InstallAsync(window, known);
            return;
        }

        _checkingUpdate = true;
        BtnCheckUpdate.IsEnabled = false;
        SetUpdateStatus("正在检查新版本…", warn: false);

        try
        {
            var result = await LauncherUpdateFlow.CheckAsync();

            if (result.Update is not null) _updateInfo = result.Update;

            SetUpdateStatus(result.Message, warn: result.Status == UpdateCheckStatus.Failed);
        }
        catch (Exception ex)
        {
            Log.Warn($"检查启动器更新失败：{ex.Message}");
            SetUpdateStatus($"检查更新失败：{ex.Message}", warn: true);
        }
        finally
        {
            _checkingUpdate = false;
            BtnCheckUpdate.IsEnabled = true;
            RefreshUpdate();
        }
    }

    private void SetUpdateStatus(string message, bool warn)
    {
        LabUpdateStatus.Text = message;
        LabUpdateStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 联机设置：昵称 —————

    /// <summary>
    /// 联机昵称与 CnCNet 客户端的玩家名（Handle）共用同一个值：
    /// 游戏里有 Handle 就以它为准（顺手同步回 HMOL 设置），没有就沿用 HMOL 里存的名字。
    /// </summary>
    private void RefreshNickname()
    {
        if (TxtNickname is null) return;

        var gameDir = InstanceManager.Current?.GameDir;
        var handle = CnCNetProfile.ReadHandle(gameDir);

        if (handle is not null && !string.Equals(handle, SettingsStore.Current.Nickname, StringComparison.Ordinal))
        {
            SettingsStore.Current.Nickname = handle;
            SettingsStore.Save();

            Log.Info($"已按 CnCNet 玩家名同步联机昵称：{handle}");
        }

        // 有游戏目录时按客户端的 MaxNameLength 限制输入长度，避免两个名字被截成不一样
        TxtNickname.MaxLength = string.IsNullOrWhiteSpace(gameDir) ? 0 : CnCNetProfile.MaxNameLength(gameDir);

        _suppressNicknameChanged = true;
        TxtNickname.Text = SettingsStore.Current.Nickname;
        _suppressNicknameChanged = false;

        RefreshNicknameHint();
    }

    private void OnNicknameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNicknameChanged || LabNicknameHint is null) return;

        LabNicknameHint.Text = "有未保存的改动，点「保存昵称」后生效（会同时写入 CnCNet 玩家名）。";
        LabNicknameHint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    private void OnSaveNicknameClick(object sender, RoutedEventArgs e) => CommitNickname();

    /// <summary>
    /// 保存昵称：写回 HMOL 设置，并同步写入 CnCNet 客户端的 <c>[MultiPlayer] Handle</c>，
    /// 让启动器与 CnCNet 的玩家名始终一致。
    /// </summary>
    private void CommitNickname()
    {
        if (TxtNickname is null) return;

        var nickname = (TxtNickname.Text ?? string.Empty).Trim();

        if (!string.Equals(nickname, SettingsStore.Current.Nickname, StringComparison.Ordinal))
        {
            SettingsStore.Current.Nickname = nickname;
            SettingsStore.Save();

            Log.Info("已更新联机昵称设置");
        }

        var gameDir = InstanceManager.Current?.GameDir;

        if (string.IsNullOrWhiteSpace(gameDir) || nickname.Length == 0)
        {
            RefreshNicknameHint();
            return;
        }

        var error = CnCNetProfile.WriteHandle(gameDir, nickname);

        if (error is null)
        {
            Log.Info($"CnCNet 玩家名已同步为：{nickname}");
            RefreshNicknameHint();
            return;
        }

        SetNicknameHint($"CnCNet 玩家名没同步成功：{error}", warn: true);
    }

    private void RefreshNicknameHint()
    {
        if (LabNicknameHint is null) return;

        var nickname = SettingsStore.Current.Nickname;
        var gameDir = InstanceManager.Current?.GameDir;
        var file = CnCNetProfile.SettingsFileOf(gameDir);

        var head = string.IsNullOrWhiteSpace(nickname)
            ? "未设置昵称，联机时会使用默认名称。"
            : $"当前昵称：{nickname}";

        var sync = file is null
            ? "没找到 CnCNet 设置文件（如 RA2MO.ini），保存后只改 HMOL 的昵称。"
            : $"与 CnCNet 玩家名共用同一个值：保存后会写入 {Path.GetFileName(file)} 的 [MultiPlayer] Handle（最长 {CnCNetProfile.MaxNameLength(gameDir!)} 个字符）。";

        SetNicknameHint($"{head}\n{sync}", warn: false);
    }

    private void SetNicknameHint(string message, bool warn)
    {
        LabNicknameHint.Text = message;
        LabNicknameHint.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 游戏内 HUD —————

    /// <summary>把联机配置里的 HUD 透明度刷到滑块上（构造、进入页面、切分类时调用）。</summary>
    private void RefreshHudOpacity()
    {
        if (SldHudOpacity is null) return;

        _suppressHudOpacity = true;
        SldHudOpacity.Value = Math.Clamp(MultiplayerSettingsStore.Current.HudOpacity, SldHudOpacity.Minimum, 1);
        _suppressHudOpacity = false;

        UpdateHudOpacityLabel();
    }

    /// <summary>
    /// HUD 透明度即时生效：写进联机配置（<c>Data\multiplayer.json</c>）后，
    /// 让已经开着的 HUD 立刻刷新；还没开过就等下次显示时自然带上。
    /// </summary>
    private void OnHudOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressHudOpacity || !_hudOpacityReady) return;

        var value = Math.Round(e.NewValue, 2);

        MultiplayerSettingsStore.Update(settings => settings.HudOpacity = value);
        MultiplayerHub.ApplyHudOpacity();

        UpdateHudOpacityLabel();
    }

    private void UpdateHudOpacityLabel()
        => LabHudOpacity.Text = $"{(int)Math.Round(SldHudOpacity.Value * 100)}%";

    /// <summary>刷新「游戏 HUD」开关：按钮文案 / 色调与状态说明都跟着实际可见性走。</summary>
    private void RefreshHudToggle()
    {
        if (BtnHudToggle is null) return;

        var visible = MultiplayerHub.HudVisible;

        BtnHudToggle.Content = visible ? "关闭游戏 HUD" : "打开游戏 HUD";
        BtnHudToggle.Tone = visible ? ButtonTone.Danger : ButtonTone.Solid;

        LabHudStatus.Text = visible
            ? "正在显示：可在屏幕上拖动；点浮窗右上角的 × 只是隐藏，这里会同步变回「打开」。"
            : "点左边按钮打开置顶浮窗，联机时看状态与对端，也方便先调好看不看清楚。";
    }

    /// <summary>开 / 关游戏内 HUD。不用连接房间也能开，便于调试外观与可读性。</summary>
    private void OnHudToggleClick(object sender, RoutedEventArgs e)
    {
        MultiplayerHub.ToggleHud();
        RefreshHudToggle();
    }

    /// <summary>HUD 在别处被开关（联机页 / 托盘 / 浮窗自己的 ×）时同步按钮状态。可能来自后台线程。</summary>
    private void OnHubStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshHudToggle);
            return;
        }

        RefreshHudToggle();
    }

    // ————— 天气城市 —————

    private void RefreshWeatherCity()
    {
        if (TxtWeatherCity is null) return;

        _suppressWeatherCityChanged = true;
        TxtWeatherCity.Text = SettingsStore.Current.WeatherCity ?? string.Empty;
        _suppressWeatherCityChanged = false;

        RefreshWeatherCityHint();
    }

    private void OnWeatherCityChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressWeatherCityChanged || LabWeatherCityHint is null) return;

        SetWeatherCityHint("有未保存的改动，点「保存城市」后主页才会用它。", warn: false);
    }

    private void OnSaveWeatherCityClick(object sender, RoutedEventArgs e)
    {
        CommitWeatherCity();
        _ = VerifyWeatherCityAsync(saved: true);
    }

    private void OnCheckWeatherCityClick(object sender, RoutedEventArgs e) => _ = VerifyWeatherCityAsync(saved: false);

    /// <summary>把输入框里的城市写回设置并落盘；没有变化时不做无谓的写文件。</summary>
    private void CommitWeatherCity()
    {
        if (TxtWeatherCity is null) return;

        var city = (TxtWeatherCity.Text ?? string.Empty).Trim();

        if (!string.Equals(city, SettingsStore.Current.WeatherCity ?? string.Empty, StringComparison.Ordinal))
        {
            SettingsStore.Current.WeatherCity = city;
            SettingsStore.Save();

            Log.Info(city.Length == 0 ? "已清空天气城市设置" : $"天气城市已设置为：{city}");
        }

        RefreshWeatherCityHint();
    }

    /// <summary>
    /// 用 Open-Meteo 的 geocoding 验证城市名能不能解析出经纬度。
    /// <paramref name="saved"/> 为 true 表示刚点过「保存城市」，提示语里说明已经生效。
    /// </summary>
    private async Task VerifyWeatherCityAsync(bool saved)
    {
        if (TxtWeatherCity is null) return;

        var city = (TxtWeatherCity.Text ?? string.Empty).Trim();

        if (city.Length == 0)
        {
            SetWeatherCityHint("还没填城市名，填好后再验证。", warn: true);
            return;
        }

        SetWeatherCityHint($"正在用 Open-Meteo 解析「{city}」…", warn: false);
        BtnSaveWeatherCity.IsEnabled = false;
        BtnCheckWeatherCity.IsEnabled = false;

        try
        {
            var coordinates = await WeatherService.GeocodeAsync(city);

            if (coordinates is null)
            {
                SetWeatherCityHint(WeatherService.LastError ?? "城市解析失败，换个写法再试。", warn: true);
                return;
            }

            var suffix = saved ? "已保存，回主页即可看到天气。" : "点「保存城市」后主页才会用它。";

            SetWeatherCityHint(
                $"解析成功：{city}（纬度 {coordinates.Value.Latitude:0.##}、经度 {coordinates.Value.Longitude:0.##}）。{suffix}",
                warn: false);
        }
        catch (Exception ex)
        {
            SetWeatherCityHint($"验证城市失败：{ex.Message}", warn: true);
        }
        finally
        {
            BtnSaveWeatherCity.IsEnabled = true;
            BtnCheckWeatherCity.IsEnabled = true;
        }
    }

    private void RefreshWeatherCityHint()
    {
        if (LabWeatherCityHint is null) return;

        var city = SettingsStore.Current.WeatherCity ?? string.Empty;

        SetWeatherCityHint(string.IsNullOrWhiteSpace(city)
            ? "当前未设置城市，主页天气卡会提示去设置。"
            : $"当前城市：{city}", warn: false);
    }

    private void SetWeatherCityHint(string message, bool warn)
    {
        LabWeatherCityHint.Text = message;
        LabWeatherCityHint.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 常用网站 —————

    private void RefreshSites()
    {
        if (ListSites is null) return;

        var links = SettingsStore.Current.SiteLinks;

        ListSites.ItemsSource = links
            .Select((link, index) => new SiteLinkSettingRow(index, link.DisplayName, link.Url))
            .ToList();

        LabSitesEmptyHint.Visibility = links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SetSitesHint(links.Count == 0 ? string.Empty : $"共 {links.Count} 个站点，增删都会立即保存。", warn: false);
    }

    private void OnAddSiteClick(object sender, RoutedEventArgs e)
    {
        if (TxtSiteName is null || TxtSiteUrl is null) return;

        var name = (TxtSiteName.Text ?? string.Empty).Trim();
        var url = SiteLinkCatalog.CompleteUrl(TxtSiteUrl.Text);

        if (name.Length == 0 || url.Length == 0)
        {
            SetSitesHint("名称与网址都要填。", warn: true);
            return;
        }

        if (!SiteLinkCatalog.IsHttpUrl(url))
        {
            SetSitesHint($"网址不合法：只支持 http / https 且要有完整主机名（现在是 {url}）。", warn: true);
            return;
        }

        var links = SettingsStore.Current.SiteLinks;

        if (links.Any(link => string.Equals(link.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            SetSitesHint("这个网址已经在列表里了。", warn: true);
            return;
        }

        links.Add(new SiteLinkSetting { Name = name, Url = url });
        SettingsStore.Save();

        TxtSiteName.Text = string.Empty;
        TxtSiteUrl.Text = string.Empty;

        RefreshSites();
        SetSitesHint($"已添加「{name}」。", warn: false);

        Log.Info($"已添加常用网站：{name}（{url}）");
    }

    private void OnRemoveSiteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int index }) return;

        var links = SettingsStore.Current.SiteLinks;
        if (index < 0 || index >= links.Count) return;

        var removed = links[index].DisplayName;

        links.RemoveAt(index);
        SettingsStore.Save();

        RefreshSites();
        SetSitesHint($"已移除「{removed}」。", warn: false);

        Log.Info($"已移除常用网站：{removed}");
    }

    private void OnRestoreSitesClick(object sender, RoutedEventArgs e)
    {
        var choice = ChoiceWindow.Ask(Window.GetWindow(this), "恢复默认列表", "把常用网站恢复成默认清单？",
            "会覆盖当前列表，替换为项目自带的默认站点（仓库、官网、QQ 群）。",
            new ChoiceOption("恢复", "restore", ButtonTone.Danger), new ChoiceOption("取消", "cancel"));

        if (choice != "restore") return;

        SettingsStore.Current.SiteLinks = SiteLinkCatalog.Defaults();
        SettingsStore.Save();

        RefreshSites();
        SetSitesHint($"已恢复默认列表（{SettingsStore.Current.SiteLinks.Count} 个站点）。", warn: false);

        Log.Info("常用网站已恢复默认列表");
    }

    private void SetSitesHint(string message, bool warn)
    {
        LabSitesHint.Text = message;
        LabSitesHint.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 主页背景 —————

    /// <summary>选一张图片 / 动图 / 视频当背景。</summary>
    private void OnPickBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择主页背景",
            Filter = $"图片与视频 ({BackgroundService.PickPattern})|{BackgroundService.PickPattern}|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var result = BackgroundService.ImportFile(dialog.FileName);

        if (!result.Ok)
        {
            SetBackgroundStatus(result.Message, warn: true);
            return;
        }

        ApplyBackgroundImport(result.FileName, result.Message, fromWallpaperPackage: false);
    }

    /// <summary>导入 Wallpaper Engine 壁纸包。解包要调外部进程，期间先禁用按钮等结果。</summary>
    private async void OnImportWallpaperClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Wallpaper Engine 壁纸包",
            Filter = "壁纸包 (*.pkg;*.mpkg)|*.pkg;*.mpkg|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SetBackgroundStatus($"正在解包 {Path.GetFileName(dialog.FileName)}，请稍候…", warn: false);
        BtnImportWallpaper.IsEnabled = false;

        try
        {
            var result = await WallpaperPackageService.ImportAsync(dialog.FileName);

            if (!result.Ok)
            {
                SetBackgroundStatus(result.Message, warn: true);
                return;
            }

            ApplyBackgroundImport(result.FileName, result.Message, fromWallpaperPackage: true);
        }
        finally
        {
            BtnImportWallpaper.IsEnabled = true;
        }
    }

    private void OnClearBackgroundClick(object sender, RoutedEventArgs e)
    {
        BackgroundService.Clear();

        var background = SettingsStore.Current.Background;
        background.FileName = null;
        background.FromWallpaperPackage = false;
        SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();

        SetBackgroundStatus("已清除背景素材，恢复主题渐变。", warn: false);
    }

    private void OnBackgroundBlurChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChanged) return;

        var radius = (int)Math.Round(e.NewValue);
        if (radius == SettingsStore.Current.Background.BlurRadius) return;

        SettingsStore.Current.Background.BlurRadius = radius;
        SettingsStore.Save();

        RefreshBackgroundLabels();
        ApplyBackgroundToWindow();
    }

    private void OnBackgroundDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChanged) return;

        var dim = (int)Math.Round(e.NewValue);
        if (dim == SettingsStore.Current.Background.DimPercent) return;

        SettingsStore.Current.Background.DimPercent = dim;
        SettingsStore.Save();

        RefreshBackgroundLabels();
        ApplyBackgroundToWindow();
    }

    /// <summary>导入成功后落盘并立刻生效，不用重启程序。</summary>
    private void ApplyBackgroundImport(string fileName, string message, bool fromWallpaperPackage)
    {
        var background = SettingsStore.Current.Background;

        background.FileName = fileName;
        background.FromWallpaperPackage = fromWallpaperPackage;

        SettingsStore.Save();

        RefreshBackground();
        ApplyBackgroundToWindow();

        SetBackgroundStatus(message, warn: false);
    }

    /// <summary>让主窗口按最新设置重铺背景。</summary>
    private void ApplyBackgroundToWindow()
        => (Window.GetWindow(this) as MainWindow)?.ApplyBackground();

    private void RefreshBackground()
    {
        var background = SettingsStore.Current.Background;

        _suppressSliderChanged = true;
        SldBlur.Value = Math.Clamp(background.BlurRadius, 0, 30);
        SldDim.Value = Math.Clamp(background.DimPercent, 0, 80);
        _suppressSliderChanged = false;

        var path = BackgroundService.ResolveExistingFile(background.FileName);

        if (path is null)
        {
            LabBackground.Text = string.IsNullOrWhiteSpace(background.FileName)
                ? "未设置，使用主题渐变。"
                : "原先的素材已丢失，请重新选择。";
        }
        else
        {
            var kind = BackgroundService.DescribeKind(BackgroundService.DetectKind(path));

            LabBackground.Text = $"当前：{kind}　{Path.GetFileName(path)}" +
                                 (background.FromWallpaperPackage ? "（来自壁纸包）" : string.Empty);
        }

        RefreshBackgroundLabels();
    }

    private void RefreshBackgroundLabels()
    {
        var background = SettingsStore.Current.Background;

        LabBlur.Text = background.BlurRadius <= 0 ? "关闭" : background.BlurRadius.ToString();
        LabDim.Text = $"{background.DimPercent}%";
    }

    private void SetBackgroundStatus(string message, bool warn)
    {
        LabBackgroundStatus.Text = message;
        LabBackgroundStatus.SetResourceReference(TextBlock.ForegroundProperty,
            warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 背景音乐 —————

    private void OnBgmToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        var bgm = SettingsStore.Current.Bgm;
        var enabledChanged = false;

        switch (tag)
        {
            case "Enabled":
                bgm.Enabled = !bgm.Enabled;
                enabledChanged = true;
                break;

            case "Loop":
                bgm.Loop = !bgm.Loop;
                break;

            case "Shuffle":
                bgm.Shuffle = !bgm.Shuffle;
                break;

            default:
                return;
        }

        SettingsStore.Save();
        RefreshMusic();

        // 开关一改就启停播放，不用再点播放按钮
        if (enabledChanged) BgmPlayer.ApplyEnabled();
    }

    private void OnBgmPlayPauseClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.TogglePlayPause();
        RefreshMusic();
    }

    private void OnBgmPreviousClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.Previous();
        RefreshMusic();
    }

    private void OnBgmNextClick(object sender, RoutedEventArgs e)
    {
        BgmPlayer.Next();
        RefreshMusic();
    }

    private void OnBgmVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderChanged) return;

        var volume = (int)Math.Round(e.NewValue);
        if (volume == SettingsStore.Current.Bgm.Volume) return;

        SettingsStore.Current.Bgm.Volume = volume;
        SettingsStore.Save();

        BgmPlayer.ApplyVolume();
        RefreshMusicLabels();
    }

    private void OnBgmAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加背景音乐",
            Multiselect = true,
            Filter = $"音频文件 ({BgmPlayer.PickPattern})|{BgmPlayer.PickPattern}|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var (added, skipped) = BgmPlayer.AddTracks(dialog.FileNames);

        SetBgmHint(skipped == 0
                ? $"已添加 {added} 首曲目。"
                : $"已添加 {added} 首，跳过 {skipped} 首（重复、格式不支持或文件不存在）。",
            warn: added == 0 && skipped > 0);

        RefreshMusic();
    }

    private void OnBgmRemoveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int index }) return;

        BgmPlayer.RemoveTrack(index);
        RefreshMusic();
    }

    private void OnBgmRemoveCurrentClick(object sender, RoutedEventArgs e)
    {
        var bgm = SettingsStore.Current.Bgm;

        if (bgm.Playlist.Count == 0)
        {
            SetBgmHint("播放列表是空的。", warn: true);
            return;
        }

        BgmPlayer.RemoveTrack(Math.Clamp(bgm.CurrentIndex, 0, bgm.Playlist.Count - 1));
        RefreshMusic();
    }

    /// <summary>播放器状态变化（换曲、暂停、失败跳过）时刷新界面。</summary>
    private void OnBgmStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshMusic);
            return;
        }

        RefreshMusic();
    }

    private void RefreshMusic()
    {
        var bgm = SettingsStore.Current.Bgm;

        _suppressSliderChanged = true;
        SldVolume.Value = Math.Clamp(bgm.Volume, 0, 100);
        _suppressSliderChanged = false;

        BtnBgmEnabled.Content = bgm.Enabled ? "已启用" : "已关闭";
        BtnBgmEnabled.Tone = bgm.Enabled ? ButtonTone.Solid : ButtonTone.Outline;

        BtnBgmLoop.Content = bgm.Loop ? "循环：开" : "循环：关";
        BtnBgmLoop.Tone = bgm.Loop ? ButtonTone.Solid : ButtonTone.Outline;

        BtnBgmShuffle.Content = bgm.Shuffle ? "随机：开" : "随机：关";
        BtnBgmShuffle.Tone = bgm.Shuffle ? ButtonTone.Solid : ButtonTone.Outline;

        BtnBgmPlayPause.Content = BgmPlayer.IsPlaying ? "暂停" : "播放";
        BtnBgmPlayPause.IsEnabled = bgm.Playlist.Count > 0;

        var index = Math.Clamp(bgm.CurrentIndex, 0, Math.Max(0, bgm.Playlist.Count - 1));
        var playing = BgmPlayer.CurrentTrackName;

        LabBgmNow.Text = !string.IsNullOrEmpty(playing)
            ? $"{(BgmPlayer.IsPlaying ? "正在播放" : "当前曲目")}：{playing}（{index + 1}/{bgm.Playlist.Count}）"
            : bgm.Playlist.Count == 0
                ? "播放列表为空，先添加曲目。"
                : "未播放";

        ListBgm.ItemsSource = bgm.Playlist
            .Select((path, i) => new BgmTrackRow(i, path, i == bgm.CurrentIndex))
            .ToList();

        RefreshMusicLabels();
    }

    private void RefreshMusicLabels() => LabVolume.Text = $"{SettingsStore.Current.Bgm.Volume}%";

    private void SetBgmHint(string message, bool warn)
    {
        LabBgmHint.Text = message;
        LabBgmHint.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 界面布局 —————

    /// <summary>打开布局编辑器。窗口是模态的，回来时刷新方案名显示。</summary>
    private void OnOpenLayoutEditorClick(object sender, RoutedEventArgs e)
    {
        LayoutEditorWindow.Open(Window.GetWindow(this));
        RefreshLayout();
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        LayoutStore.EnsureLoaded(LayoutElements.Ids);

        var choice = ChoiceWindow.Ask(Window.GetWindow(this), "恢复默认布局", "把界面布局恢复成默认？",
            "会清空「默认布局」方案里的自定义顺序、显隐与名称，并切回默认布局；自己存的其它方案不受影响。",
            new ChoiceOption("恢复", "restore", ButtonTone.Danger), new ChoiceOption("取消", "cancel"));

        if (choice != "restore") return;

        LayoutStore.RestoreDefault(LayoutElements.Ids);
        ApplyLayoutToWindow();
        RefreshLayout();

        SetLayoutStatus("已恢复默认布局。");
        Log.Info("界面布局已恢复默认");
    }

    private void RefreshLayout()
    {
        if (LabLayoutScheme is null) return;

        LayoutStore.EnsureLoaded(LayoutElements.Ids);

        var active = LayoutStore.Active;
        var overridden = active.Items.Count(item => !string.IsNullOrWhiteSpace(item.DisplayName) || !item.Visible);

        LabLayoutScheme.Text = $"当前方案：{active.Name}（共 {LayoutStore.All.Count} 套）" +
                               (overridden == 0 ? "，未做任何调整。" : $"，已调整 {overridden} 项。");

        RefreshWidgetToggles();
    }

    /// <summary>主页三块小组件的开关：显示时用强调色实心，隐藏时描边，文案里直接写清当前状态。</summary>
    private void RefreshWidgetToggles()
    {
        if (BtnWidgetCalendar is null) return;

        var widgets = SettingsStore.Current.HomeWidgets;

        Sync(BtnWidgetCalendar, "日历", widgets.Calendar);
        Sync(BtnWidgetWeather, "天气", widgets.Weather);
        Sync(BtnWidgetSites, "常用网站", widgets.Sites);

        static void Sync(OutlineButton button, string name, bool shown)
        {
            button.Content = $"{name}：{(shown ? "显示" : "隐藏")}";
            button.Tone = shown ? ButtonTone.Solid : ButtonTone.Outline;
        }
    }

    /// <summary>切换某块主页小组件的显隐。改动立即落盘，回主页时按新设置重排。</summary>
    private void OnWidgetToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        var widgets = SettingsStore.Current.HomeWidgets;
        var shown = tag switch
        {
            "Calendar" => widgets.Calendar = !widgets.Calendar,
            "Weather" => widgets.Weather = !widgets.Weather,
            "Sites" => widgets.Sites = !widgets.Sites,
            _ => (bool?)null
        };
        if (shown is null) return;

        SettingsStore.Save();
        RefreshWidgetToggles();

        Log.Info($"主页小组件「{tag}」已改为{(shown.Value ? "显示" : "隐藏")}");
    }

    /// <summary>让主窗口按启用方案重排侧栏。</summary>
    private void ApplyLayoutToWindow()
        => (Window.GetWindow(this) as MainWindow)?.ApplyLayout();

    private void SetLayoutStatus(string message)
    {
        LabLayoutStatus.Text = message;
        LabLayoutStatus.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    // ————— 开机自启 —————

    /// <summary>
    /// 刷新开关。<paramref name="operation"/> 不为空表示刚点过开关，直接报这次的结果；否则按注册表实际状态描述。
    /// 状态一律以注册表为准，配置文件里的 AutoStart 字段只作记录。
    /// </summary>
    private void RefreshAutoStart(AutoStartResult? operation = null)
    {
        if (BtnAutoStart is null) return;

        var state = AutoStartManager.Query();

        BtnAutoStart.Content = state.IsEnabled ? "已开启" : "已关闭";
        BtnAutoStart.Tone = state.IsEnabled ? ButtonTone.Solid : ButtonTone.Outline;

        if (operation is not null)
        {
            SetAutoStartStatus(operation.Message, warn: !operation.Ok);
            return;
        }

        switch (state.Status)
        {
            case AutoStartStatus.Enabled:
                SetAutoStartStatus($"自启命令：{state.Command}", warn: false);
                break;

            // 注册表里那条指向别的 exe（程序被挪过）：开关先按「关」显示，再点一次就改成当前位置
            case AutoStartStatus.PointsToOtherExe:
                SetAutoStartStatus($"{state.Message}再点一次开关即可改成当前位置。", warn: true);
                break;

            case AutoStartStatus.Failed:
                SetAutoStartStatus(state.Message, warn: true);
                break;

            default:
                SetAutoStartStatus("未设置开机自启。", warn: false);
                break;
        }
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        var state = AutoStartManager.Query();

        // 注册表都读不出来时先别瞎改，把原因摆给用户看
        if (state.Status == AutoStartStatus.Failed)
        {
            SetAutoStartStatus(state.Message, warn: true);
            return;
        }

        var enable = !state.IsEnabled;
        var result = enable ? AutoStartManager.Enable() : AutoStartManager.Disable();

        if (result.Ok)
        {
            // 改注册表成功才同步配置字段，两边保持一致
            MultiplayerSettingsStore.Update(settings => settings.AutoStart = enable);
        }
        else
        {
            Log.Warn($"开机自启设置失败：{result.Message}");
        }

        RefreshAutoStart(result);
    }

    private void SetAutoStartStatus(string message, bool warn)
    {
        LabAutoStartStatus.Text = message;
        LabAutoStartStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 扩展模块 —————

    /// <summary>本进程内是否已经弹过「扩展是纯声明式」的首次提示。弹过就不再打扰。</summary>
    private static bool _extensionNoticeShown;

    /// <summary>重新读一遍扩展目录，再刷新列表。纯读文件，不执行任何扩展内容。</summary>
    private void RefreshExtensions()
    {
        if (ListExtensions is null) return;

        ExtensionStore.Reload();

        var all = ExtensionStore.All;

        ListExtensions.ItemsSource = all.Select(info => new ExtensionRow(info)).ToList();

        ScrollExtensions.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PanExtensionsEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshExtensionNetworkButton();

        if (all.Count == 0)
        {
            SetExtensionsHint(string.Empty, warn: false);
            return;
        }

        var failed = all.Count(info => info.Status == ExtensionStatus.Failed);

        SetExtensionsHint(
            $"共 {all.Count} 个扩展：已启用 {all.Count(info => info.Status == ExtensionStatus.Enabled)}，" +
            $"已停用 {all.Count(info => info.Status == ExtensionStatus.Disabled)}，加载失败 {failed}。" +
            "启用 / 停用会立即写回清单文件。",
            warn: failed > 0);
    }

    /// <summary>数据源联网总开关的按钮文案。</summary>
    private void RefreshExtensionNetworkButton()
    {
        if (BtnExtensionNetwork is null) return;

        var allowed = SettingsStore.Current.AllowExtensionNetwork;

        BtnExtensionNetwork.Content = allowed ? "联网：开" : "联网：关";
        BtnExtensionNetwork.Tone = allowed ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnToggleExtensionNetworkClick(object sender, RoutedEventArgs e)
    {
        var allowed = !SettingsStore.Current.AllowExtensionNetwork;

        SettingsStore.Current.AllowExtensionNetwork = allowed;
        SettingsStore.Save();

        RefreshExtensionNetworkButton();
        SetExtensionsHint(allowed
            ? "已允许扩展读取数据源（只读 http/https GET，带超时与内存缓存）。"
            : "已关闭扩展联网：扩展只显示清单里写死的静态内容。", warn: false);

        Log.Info($"扩展数据源联网总开关已{(allowed ? "打开" : "关闭")}");
    }

    private void OnReloadExtensionsClick(object sender, RoutedEventArgs e)
    {
        SetExtensionsHint("正在重新读取扩展目录…", warn: false);

        RefreshExtensions();

        Log.Info("界面：已重新加载扩展目录");
    }

    private void OnOpenExtensionsFolderClick(object sender, RoutedEventArgs e)
    {
        if (!ExtensionStore.OpenFolder()) SetExtensionsHint("扩展目录不存在或无法打开。", warn: true);
    }

    /// <summary>新建：先问一个扩展名（= 清单文件名），再用表单建内容。</summary>
    private void OnNewExtensionClick(object sender, RoutedEventArgs e)
    {
        var id = TextInputWindow.Ask(Window.GetWindow(this), "新建扩展", "给这个扩展起一个名字",
            "我的小组件", "会保存成「扩展目录\\<名字>.json」，只允许普通文件名（不能带路径分隔符）。",
            name => ExtensionValidator.ValidateId(name));

        if (string.IsNullOrWhiteSpace(id)) return;

        if (ExtensionStore.Find(id) is not null)
        {
            SetExtensionsHint($"已经有一个叫「{id}」的扩展了，换一个名字或直接编辑它。", warn: true);
            return;
        }

        OpenExtensionEditor(id, new ExtensionManifest());
    }

    /// <summary>编辑：把清单读出来回填到表单里。</summary>
    private void OnEditExtensionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExtensionRow row }) return;

        if (!ExtensionStore.TryRead(row.Id, out var manifest, out var error))
        {
            SetExtensionsHint($"打不开「{row.Name}」的清单：{error}", warn: true);
            return;
        }

        OpenExtensionEditor(row.Id, manifest);
    }

    /// <summary>打开表单窗口；窗口里保存成功（返回 true）后刷新列表。</summary>
    private void OpenExtensionEditor(string id, ExtensionManifest manifest)
    {
        var window = new ExtensionEditorWindow(id, manifest) { Owner = Window.GetWindow(this) };

        var saved = window.ShowDialog() == true;

        RefreshExtensions();

        if (saved) SetExtensionsHint($"已保存「{id}」，回主页就能看到它的卡片。", warn: false);
    }

    /// <summary>启用 / 停用：改清单里的 enabled 字段并落盘。</summary>
    private void OnToggleExtensionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExtensionRow row }) return;

        var enable = !row.IsEnabled;

        if (!ExtensionStore.TrySetEnabled(row.Id, enable, out var error))
        {
            SetExtensionsHint($"操作失败：{error}", warn: true);
            return;
        }

        RefreshExtensions();

        SetExtensionsHint(enable
            ? $"已启用「{row.Name}」，主页上会出现它的卡片。"
            : $"已停用「{row.Name}」，主页上它的卡片会消失。", warn: false);
    }

    private void OnDeleteExtensionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExtensionRow row }) return;

        var choice = ChoiceWindow.Ask(Window.GetWindow(this), "删除扩展",
            $"要删除扩展「{row.Name}」吗？",
            $"只会删掉扩展目录里的那份清单文件（{row.Id}.json），不会动你电脑上的其它文件。",
            new ChoiceOption("删除", "delete", ButtonTone.Danger),
            new ChoiceOption("取消", "cancel"));

        if (choice != "delete") return;

        if (!ExtensionStore.TryDelete(row.Id, out var error))
        {
            SetExtensionsHint($"删除失败：{error}", warn: true);
            return;
        }

        RefreshExtensions();
        SetExtensionsHint($"已删除「{row.Name}」。", warn: false);
    }

    /// <summary>
    /// 首次进入设置页时弹一次安全说明：讲清扩展是纯声明式配置 —— 不执行代码、不读写电脑上的文件、
    /// 不改动系统，数据源只做 http/https 的只读 GET。确认过一次就不再弹。
    /// </summary>
    private void ShowExtensionNoticeOnce()
    {
        if (_extensionNoticeShown || SettingsStore.Current.ExtensionsNoticeShown) return;

        _extensionNoticeShown = true;

        ChoiceWindow.Ask(Window.GetWindow(this), "扩展模块说明", "扩展是纯声明式配置，不会执行任何代码",
            "本启动器的扩展只是一份 JSON 清单：标题、图标、文字行，可选一个只读数据源与一个点击网址。\n\n" +
            "· 启动器不会加载任何程序集、不会运行脚本，也没有任何执行用户代码的入口；\n" +
            "· 扩展目录只被读取：启动器不会为扩展写缓存文件、不会写注册表、不会改动系统；\n" +
            "· 数据源只做 http/https 的只读 GET，带超时与内存缓存，还可以在下面一键关掉；\n" +
            "· 唯一会被写入的文件，就是你在这里新建 / 编辑的那份扩展清单本身。",
            new ChoiceOption("我知道了", "ok", ButtonTone.Solid));

        SettingsStore.Current.ExtensionsNoticeShown = true;
        SettingsStore.Save();
    }

    private void SetExtensionsHint(string message, bool warn)
    {
        LabExtensionsHint.Text = message;
        LabExtensionsHint.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }

    // ————— 数据目录 —————

    private void OnOpenDataFolderClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenFolder(Paths.Data);
}
