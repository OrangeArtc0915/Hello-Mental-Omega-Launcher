
using HMOL.Core.Localization;
namespace HMOL.App.Layout;

/// <summary>
/// 一个可自定义的界面元素。<c>Id</c> 是写进方案文件的标识；<c>ControlName</c> / <c>ContainerName</c>
/// 是主窗口里对应的控件名，界面按名字取控件，标识与控件不会各写一份而对不上。
/// </summary>
public sealed record LayoutElementInfo(
    string Id,
    string DisplayName,
    string Icon,
    string Group,
    string ControlName,
    string ContainerName,
    bool CanHide = true,
    HomeDefaultRect? DefaultRect = null);

/// <summary>
/// 主页元素在编辑器预览里的默认示意矩形（百分比，0-100）。
/// 只用于编辑器里「还没指定坐标」时把方块画在像样一点的地方，不写进方案文件。
/// </summary>
public sealed record HomeDefaultRect(double XPercent, double YPercent, double WidthPercent, double HeightPercent);

/// <summary>
/// 可自定义的左侧导航元素清单（主窗口侧栏 = 6 个页面 + 底部「关于」）。
/// 侧栏是固定布局：只支持顺序 / 显隐 / 改名，不参与自由定位。
/// </summary>
public static class LayoutElements
{
    /// <summary>侧栏分组显示名（布局编辑器里可见，跟随界面语言）。</summary>
    public static string GroupNav => Loc.T("主导航");

    /// <summary>底部「关于」所在分组的显示名。</summary>
    public static string GroupFooter => Loc.T("底部");

    /// <summary>页面导航所在的分组容器。</summary>
    public const string ContainerNav = "PanSidebarNav";

    /// <summary>底部「关于」所在的分组容器。顺序只在组内调整，不会把「关于」挪进页面导航里。</summary>
    public const string ContainerFooter = "PanSidebarFooter";

    /// <summary>元素清单。属性而非静态字段：显示名随语言变化，切换语言后重新求值。</summary>
    public static IReadOnlyList<LayoutElementInfo> All =>
    [
        new("nav_home", Loc.T("主页"), "lucide/layout-dashboard", GroupNav, "NavHome", ContainerNav),
        new("nav_instances", Loc.T("游戏实例"), "lucide/layers", GroupNav, "NavInstances", ContainerNav),
        new("nav_packages", Loc.T("包管理"), "lucide/package", GroupNav, "NavPackages", ContainerNav),
        new("nav_multiplayer", Loc.T("联机"), "lucide/network", GroupNav, "NavMultiplayer", ContainerNav),
        new("nav_download", Loc.T("下载"), "lucide/download", GroupNav, "NavDownload", ContainerNav),
        new("nav_log", Loc.T("运行日志"), "lucide/scroll-text", GroupNav, "NavLog", ContainerNav),
        // 设置页是布局编辑器的唯一入口，允许隐藏等于把用户锁在外面，因此不可隐藏
        new("nav_settings", Loc.T("设置"), "lucide/settings", GroupNav, "NavSettings", ContainerNav, CanHide: false),
        new("nav_about", Loc.T("关于"), "lucide/info", GroupFooter, "NavAbout", ContainerFooter),
    ];

    /// <summary>侧栏元素标识。</summary>
    public static IReadOnlyList<string> NavIds => All.Select(info => info.Id).ToArray();

    /// <summary>
    /// 方案文件里允许出现的全部元素标识（侧栏 + 主页）。
    /// 这里必须两块都列上：方案读取时按它判断「未知元素」，少一个就会被当成脏数据丢掉。
    /// 只关心侧栏的地方请用 <see cref="NavIds"/>。
    /// </summary>
    public static IReadOnlyList<string> Ids => [.. NavIds, .. HomeLayoutElements.Ids];

    public static IReadOnlyList<string> ContainerNames =>
        All.Select(info => info.ContainerName).Distinct().ToArray();

    public static LayoutElementInfo? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(info => string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 可自由定位的主页元素清单。主页上的每一块都在这里登记，各自对应 <c>PageHome</c> 里的一个控件：
/// 方案里给了坐标的元素会被摘出流式布局、整页自由定位（拖动 + 缩放），没给坐标的照常走流式布局。
/// 启动入口（当前实例卡 / 无实例引导卡）不允许隐藏，免得把「启动游戏」藏没了找不回来。
/// </summary>
public static class HomeLayoutElements
{
    /// <summary>主页分组显示名。</summary>
    public static string GroupHome => Loc.T("主页");

    /// <summary>主页自由定位的宿主容器名（<c>PageHome</c> 里的整页内容栅格）。</summary>
    public const string StageName = "PanHomeStage";

    public static IReadOnlyList<LayoutElementInfo> All =>
    [
        new("home_banner", Loc.T("欢迎横幅"), "lucide/flag", GroupHome, "CardBanner", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(0, 0, 100, 13)),
        // 启动按钮长在「当前实例」卡片里（卡片还带标题、状态、路径），整块搬动才不会把卡片掏空
        new("home_current", Loc.T("当前实例卡片"), "lucide/play", GroupHome, "CardCurrent", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(2, 16, 56, 50)),
        new("home_switch", Loc.T("切换实例卡片"), "lucide/layers", GroupHome, "CardSwitch", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(62, 16, 36, 50)),
        new("home_empty", Loc.T("无实例引导卡片"), "lucide/triangle-alert", GroupHome, "CardEmpty", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(28, 22, 44, 40)),
        new("home_note", Loc.T("底部提示条"), "lucide/info", GroupHome, "CardNote", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(2, 68, 96, 9)),
        new("home_calendar", Loc.T("日历小组件"), "lucide/grid-2x2", GroupHome, "CardCalendar", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(2, 78, 31, 20)),
        new("home_weather", Loc.T("天气小组件"), "lucide/earth", GroupHome, "CardWeather", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(35, 78, 30, 20)),
        new("home_sites", Loc.T("常用网站小组件"), "lucide/link-2", GroupHome, "CardSites", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(67, 78, 31, 20)),
        new("home_extensions", Loc.T("扩展小组件区"), "lucide/puzzle", GroupHome, "PanExtensionWidgets", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(2, 92, 96, 8)),
        new("home_clock", Loc.T("时钟小组件"), "lucide/scroll-text", GroupHome, "CardClock", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(2, 78, 31, 20)),
        new("home_memo", Loc.T("便签小组件"), "lucide/book-marked", GroupHome, "CardMemo", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(35, 78, 30, 20)),
        new("home_music", Loc.T("音乐控制小组件"), "lucide/music", GroupHome, "CardMusicWidget", StageName,
            CanHide: true, DefaultRect: new HomeDefaultRect(67, 78, 31, 20)),
    ];

    public static IReadOnlyList<string> Ids => All.Select(info => info.Id).ToArray();

    public static LayoutElementInfo? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(info => string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase));
}
