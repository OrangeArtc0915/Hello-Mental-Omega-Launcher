using HMOL.Core.Updater;
using HMOL.Core.Localization;

namespace HMOL.Core.App;

public enum ThemeMode
{
    Light = 0,
    Dark = 1,
    System = 2
}

/// <summary>强调色。骨架阶段先放四套，后续按需求增删。</summary>
public enum AccentTheme
{
    Default = 0,
    Blue = 1,
    Green = 2,
    Purple = 3
}

/// <summary>主页显示模式。</summary>
public enum HomeMode
{
    /// <summary>默认模式：完整主页（欢迎横幅 + 当前实例 + 切换实例 + 小组件）。</summary>
    Default = 0,

    /// <summary>简洁模式：只留右下角一个启动入口，其余留空以尽量露出背景图。</summary>
    Simple = 1
}

/// <summary>背景填充方式。</summary>
public enum BackgroundFill
{
    /// <summary>铺满并裁剪（默认，不变形）。</summary>
    Cover = 0,

    /// <summary>完整显示，可能留边。</summary>
    Fit = 1,

    /// <summary>拉伸铺满，可能变形。</summary>
    Fill = 2,

    /// <summary>平铺（只对静态图片有意义）。</summary>
    Tile = 3
}

/// <summary>主页背景设置。素材文件会被复制进 Paths.Backgrounds，避免用户原图被移走后背景失效。</summary>
public sealed class BackgroundSettings
{
    /// <summary>背景素材在 Paths.Backgrounds 下的文件名；为空表示不使用背景。</summary>
    public string? FileName { get; set; }

    /// <summary>模糊半径（0-30），0 表示不模糊。</summary>
    public int BlurRadius { get; set; }

    /// <summary>暗化百分比（0-80），0 表示不暗化。</summary>
    public int DimPercent { get; set; } = 35;

    /// <summary>切换背景时的淡入时长（毫秒）。</summary>
    public int FadeMs { get; set; } = 320;

    /// <summary>背景素材是否来自导入的 Wallpaper Engine 壁纸包（仅用于界面提示）。</summary>
    public bool FromWallpaperPackage { get; set; }

    /// <summary>轮播清单：素材目录下的文件名。为空时只显示 <see cref="FileName"/> 这一张。</summary>
    public List<string> Playlist { get; set; } = [];

    /// <summary>是否按间隔轮播 <see cref="Playlist"/>。</summary>
    public bool RotateEnabled { get; set; }

    /// <summary>轮播间隔（秒），夹在 10-3600。</summary>
    public int RotateSeconds
    {
        get => _rotateSeconds;
        set => _rotateSeconds = Math.Clamp(value, 10, 3600);
    }

    private int _rotateSeconds = 60;

    /// <summary>轮播是否随机顺序（不重复，切到下一轮再重洗）。</summary>
    public bool RotateShuffle { get; set; }

    /// <summary>填充方式。</summary>
    public BackgroundFill Fill { get; set; } = BackgroundFill.Cover;

    /// <summary>鼠标视差：背景随光标轻微位移。</summary>
    public bool Parallax { get; set; }

    /// <summary>
    /// 每页独立背景：页面标识（home / instances / packages / multiplayer / download / log / settings）
    /// → 素材文件名。没配的页用 <see cref="FileName"/>（含轮播）。
    /// </summary>
    public Dictionary<string, string> PageOverrides { get; set; } = [];
}

/// <summary>主页小组件的显隐。默认三块都显示；旧配置文件里没有这个字段时按默认（全显示）处理。</summary>
public sealed class HomeWidgetSettings
{
    /// <summary>现实月历。</summary>
    public bool Calendar { get; set; } = true;

    /// <summary>现实天气。</summary>
    public bool Weather { get; set; } = true;

    /// <summary>常用网站。</summary>
    public bool Sites { get; set; } = true;
}

/// <summary>
/// 主页附加小组件（时钟 / 便签 / 快捷启动 / 音乐控制）的配置。默认全部关闭，用户在设置里打开后
/// 才会出现在主页，并可像其它区块一样在布局编辑器里自由拖动、缩放与隐藏。
/// </summary>
public sealed class HomeExtraWidgetSettings
{
    /// <summary>时钟小组件。</summary>
    public bool Clock { get; set; }

    /// <summary>时钟用 24 小时制；false 为 12 小时制。</summary>
    public bool Clock24Hour { get; set; } = true;

    /// <summary>时钟下面是否显示日期。</summary>
    public bool ClockShowDate { get; set; } = true;

    /// <summary>便签小组件。</summary>
    public bool Memo { get; set; }

    /// <summary>便签标题。</summary>
    public string MemoTitle { get; set; } = Loc.T("便签");

    /// <summary>便签正文（多行）。</summary>
    public string MemoText { get; set; } = string.Empty;

    /// <summary>音乐控制小组件（控制背景音乐）。</summary>
    public bool Music { get; set; }
}

/// <summary>背景音乐设置。</summary>
public sealed class BgmSettings
{
    public bool Enabled { get; set; }

    /// <summary>播放列表，存绝对路径。</summary>
    public List<string> Playlist { get; set; } = [];

    /// <summary>音量（0-100）。</summary>
    public int Volume { get; set; } = 60;

    /// <summary>是否随机播放。</summary>
    public bool Shuffle { get; set; }

    /// <summary>是否循环播放整个列表。</summary>
    public bool Loop { get; set; } = true;

    /// <summary>当前曲目在 Playlist 中的下标。</summary>
    public int CurrentIndex { get; set; }
}

/// <summary>全局设置。落盘到 Paths.SettingsFile。</summary>
public sealed class Settings
{
    /// <summary>窗口透明度允许范围：0.5（半透明）~ 1.0（不透明）。</summary>
    public const double MinWindowOpacity = 0.5;

    public const double MaxWindowOpacity = 1.0;

    /// <summary>界面缩放允许范围：0.8（缩小）~ 1.5（放大）。</summary>
    public const double MinUiScale = 0.8;

    public const double MaxUiScale = 1.5;

    /// <summary>卡片与组件（半透明承载面）透明度允许范围：0.3（很透）~ 1.0（不透明）。</summary>
    public const double MinSurfaceOpacity = 0.3;

    public const double MaxSurfaceOpacity = 1.0;

    /// <summary>主题：浅色 / 深色 / 跟随系统。</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>强调色预设。</summary>
    public AccentTheme Accent { get; set; } = AccentTheme.Default;

    /// <summary>
    /// 自定义强调色（<c>#RRGGBB</c>）。非空时优先于 <see cref="Accent"/> 预设，留空表示用预设色。
    /// </summary>
    public string CustomAccentColor { get; set; } = string.Empty;

    /// <summary>界面字体名（系统已安装的字体）。留空表示用内置默认字体。</summary>
    public string AppFontFamily { get; set; } = string.Empty;

    /// <summary>
    /// 界面语言代码（如 <c>zh-CN</c> / <c>en-US</c>）。旧配置文件里没有这个字段时按简体中文处理；
    /// 语言包放在 <c>Data\lang</c>，见 <c>HMOL.Core.Localization.Loc</c>。切换立即生效并自动保存。
    /// </summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>
    /// 「跟随系统」：开启时每次启动都按系统时区与区域重新判定语言（见 <c>Loc.DetectSystemLanguage</c>），
    /// <see cref="Language"/> 只当判不出来时的兜底。用户手动挑过一次语言就会自动关掉它。
    /// </summary>
    public bool AutoLanguage { get; set; } = true;

    /// <summary>界面动效总开关。关掉后所有过渡 / 淡入 / 呼吸效果都直接落到终态。</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>动效速度倍率（越大越快）：0.5 慢，1.0 标准，2.0 快。</summary>
    public double AnimationSpeed
    {
        get => _animationSpeed;
        set => _animationSpeed = double.IsFinite(value) ? Math.Clamp(value, 0.5, 2.0) : 1.0;
    }

    private double _animationSpeed = 1.0;

    /// <summary>
    /// 卡片 / 按钮等圆角的缩放倍率（0.5-1.6）：1.0 为默认圆角，调小更方正、调大更圆润。
    /// 胶囊（1000）不跟着变，保持正圆端。
    /// </summary>
    public double CornerRadiusScale
    {
        get => _cornerRadiusScale;
        set => _cornerRadiusScale = double.IsFinite(value) ? Math.Clamp(value, 0.5, 1.6) : 1.0;
    }

    private double _cornerRadiusScale = 1.0;

    /// <summary>最近一次使用的游戏实例 Id。为 null 表示还没选过实例。</summary>
    public string? LastInstanceId { get; set; }

    /// <summary>最近一次检查更新的时间。只作展示，不做节流。</summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>启动时是否自动检查启动器更新。默认开启；检查失败只记日志，不打扰启动。</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>
    /// 检查更新优先走哪个源。默认自动（GitHub 优先，取不到时换 Gitee）。
    /// 国内网络下可改成 Gitee 优先。
    /// </summary>
    public LauncherUpdateSource LauncherUpdateSource { get; set; } = LauncherUpdateSource.Auto;

    /// <summary>
    /// 「下载」页「更多下载」列表里文件的保存目录。留空表示用默认目录
    /// （<see cref="Paths.Downloads"/>，即 <c>%LOCALAPPDATA%\HMOL\Downloads</c>）。
    /// 补丁与组网组件的缓存不受它影响，仍留在程序数据目录。
    /// </summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 大文件是否用多线程分片并发下载（默认开启）。个别网络下并发连接会被限速或干扰，
    /// 关掉后退回单连接下载。
    /// </summary>
    public bool MultiThreadDownload { get; set; } = true;

    /// <summary>联机用的本地自定义昵称，留空表示还没设置。</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>主页背景。</summary>
    public BackgroundSettings Background { get; set; } = new();

    /// <summary>背景音乐。</summary>
    public BgmSettings Bgm { get; set; } = new();

    /// <summary>主页显示模式。旧配置文件里没有这个字段时按默认（完整主页）处理。</summary>
    public HomeMode HomeMode { get; set; } = HomeMode.Default;

    /// <summary>主页小组件（日历 / 天气 / 常用网站）的显隐。</summary>
    public HomeWidgetSettings HomeWidgets { get; set; } = new();

    /// <summary>主页附加小组件（时钟 / 便签 / 快捷启动 / 音乐控制）。</summary>
    public HomeExtraWidgetSettings ExtraWidgets { get; set; } = new();

    /// <summary>自定义窗口标题文字；留空表示用默认的应用名。</summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>自定义窗口图标文件名（存在个性化素材目录）；留空表示用内置图标。</summary>
    public string WindowIconFile { get; set; } = string.Empty;

    /// <summary>启动音效文件名（存在个性化素材目录）；留空表示不播放。</summary>
    public string StartupSoundFile { get; set; } = string.Empty;

    /// <summary>关闭音效文件名（存在个性化素材目录）；留空表示不播放。</summary>
    public string ShutdownSoundFile { get; set; } = string.Empty;

    /// <summary>当前启用的自定义布局方案 Id；为空表示使用默认布局。</summary>
    public string? ActiveLayoutSchemeId { get; set; }

    /// <summary>
    /// 主页天气小组件用的城市名（Open-Meteo 的 geocoding 按它解析经纬度），留空表示还没设置。
    /// 旧配置文件里没有这个字段时按空串处理（即「还没设置城市」）。
    /// </summary>
    public string WeatherCity { get; set; } = string.Empty;

    /// <summary>
    /// 扩展模块的网络总开关。关闭后扩展只显示清单里写死的静态内容，宿主一个请求都不发
    /// （见 <c>HMOL.Core.Extensions.ExtensionDataService</c>）。默认开启。
    /// </summary>
    public bool AllowExtensionNetwork { get; set; } = true;

    /// <summary>「扩展模块」的首次安全提示是否已经弹过。弹过一次就不再打扰。</summary>
    public bool ExtensionsNoticeShown { get; set; }

    /// <summary>
    /// 主页「常用网站」小组件的站点列表。旧配置文件里没有这个字段时给出默认清单；
    /// 用户自己清空（存成空数组）后保持为空，不回填默认值。
    /// </summary>
    public List<SiteLinkSetting> SiteLinks
    {
        get => _siteLinks;
        set => _siteLinks = value is null
            ? SiteLinkCatalog.Defaults()
            : SiteLinkCatalog.Normalize(value);
    }

    private List<SiteLinkSetting> _siteLinks = SiteLinkCatalog.Defaults();

    /// <summary>
    /// 主窗口透明度（<see cref="MinWindowOpacity"/> ~ <see cref="MaxWindowOpacity"/>，1.0 = 完全不透明）。
    /// 旧配置文件里没有这个字段时按 1.0 处理；越界或非法值在写入时夹回范围，
    /// 免得手工改坏配置后窗口直接看不见。
    /// </summary>
    public double WindowOpacity
    {
        get => _windowOpacity;
        set => _windowOpacity = double.IsFinite(value)
            ? Math.Clamp(value, MinWindowOpacity, MaxWindowOpacity)
            : MaxWindowOpacity;
    }

    private double _windowOpacity = MaxWindowOpacity;

    /// <summary>
    /// 主界面缩放比例（<see cref="MinUiScale"/> ~ <see cref="MaxUiScale"/>，1.0 = 原尺寸）。
    /// 旧配置文件里没有这个字段时按 1.0 处理；越界或非法值在写入时夹回范围。
    /// </summary>
    public double UiScale
    {
        get => _uiScale;
        set => _uiScale = double.IsFinite(value)
            ? Math.Clamp(value, MinUiScale, MaxUiScale)
            : 1.0;
    }

    private double _uiScale = 1.0;

    /// <summary>
    /// 卡片与组件（半透明承载面）透明度：1.0 = 不透明（默认观感），越小透出的主页背景越多，
    /// 但只作用在承载面底色的 alpha 上，文字与图标仍保持清晰（区别于整体「窗口透明度」）。
    /// 旧配置文件里没有这个字段时按 1.0 处理；越界或非法值在写入时夹回范围。
    /// </summary>
    public double SurfaceOpacity
    {
        get => _surfaceOpacity;
        set => _surfaceOpacity = double.IsFinite(value)
            ? Math.Clamp(value, MinSurfaceOpacity, MaxSurfaceOpacity)
            : MaxSurfaceOpacity;
    }

    private double _surfaceOpacity = MaxSurfaceOpacity;

    /// <summary>
    /// 是否已经历过「首次运行配置向导」。
    /// 老用户升级上来时该字段是新增的、值为 false，主窗口在启动判定里会顺手补写成 true，
    /// 避免他们之后把实例删空又被向导打扰。见 <c>MainWindow.QueueFirstRunWizard</c>。
    /// </summary>
    public bool FirstRunCompleted { get; set; }
}
