using HMOL.Core.Updater;

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

    /// <summary>主题：浅色 / 深色 / 跟随系统。</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>强调色。</summary>
    public AccentTheme Accent { get; set; } = AccentTheme.Default;

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

    /// <summary>联机用的本地自定义昵称，留空表示还没设置。</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>主页背景。</summary>
    public BackgroundSettings Background { get; set; } = new();

    /// <summary>背景音乐。</summary>
    public BgmSettings Bgm { get; set; } = new();

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
    /// 启动时是否自动检测游戏路径（对应旧版配置键 auto_detect_path）。
    /// 旧版只有开关、没有实现；新版在后台按「常见盘符 + 常见目录名」扫描，
    /// 结果只作建议、绝不静默改写实例配置。默认开启。
    /// </summary>
    public bool AutoDetectGamePath { get; set; } = true;
}
