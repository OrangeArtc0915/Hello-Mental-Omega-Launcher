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
    public const string GroupNav = "主导航";

    public const string GroupFooter = "底部";

    /// <summary>页面导航所在的分组容器。</summary>
    public const string ContainerNav = "PanSidebarNav";

    /// <summary>底部「关于」所在的分组容器。顺序只在组内调整，不会把「关于」挪进页面导航里。</summary>
    public const string ContainerFooter = "PanSidebarFooter";

    public static IReadOnlyList<LayoutElementInfo> All { get; } =
    [
        new("nav_home", "主页", "lucide/layout-dashboard", GroupNav, "NavHome", ContainerNav),
        new("nav_instances", "游戏实例", "lucide/layers", GroupNav, "NavInstances", ContainerNav),
        new("nav_packages", "包管理", "lucide/package", GroupNav, "NavPackages", ContainerNav),
        new("nav_multiplayer", "联机", "lucide/network", GroupNav, "NavMultiplayer", ContainerNav),
        new("nav_download", "下载", "lucide/download", GroupNav, "NavDownload", ContainerNav),
        new("nav_log", "运行日志", "lucide/scroll-text", GroupNav, "NavLog", ContainerNav),
        // 设置页是布局编辑器的唯一入口，允许隐藏等于把用户锁在外面，因此不可隐藏
        new("nav_settings", "设置", "lucide/settings", GroupNav, "NavSettings", ContainerNav, CanHide: false),
        new("nav_about", "关于", "lucide/info", GroupFooter, "NavAbout", ContainerFooter),
    ];

    /// <summary>侧栏元素标识。</summary>
    public static IReadOnlyList<string> NavIds { get; } = All.Select(info => info.Id).ToArray();

    /// <summary>
    /// 方案文件里允许出现的全部元素标识（侧栏 + 主页）。
    /// 这里必须两块都列上：方案读取时按它判断「未知元素」，少一个就会被当成脏数据丢掉。
    /// 只关心侧栏的地方请用 <see cref="NavIds"/>。
    /// </summary>
    public static IReadOnlyList<string> Ids { get; } = [.. NavIds, .. HomeLayoutElements.Ids];

    public static IReadOnlyList<string> ContainerNames { get; } =
        All.Select(info => info.ContainerName).Distinct().ToArray();

    public static LayoutElementInfo? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(info => string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 可自由定位的主页元素清单。主页就这几块内容，各自对应 <c>PageHome</c> 里的一个控件：
/// 方案里给了坐标的元素会被摘出流式布局绝对定位，没给坐标的照常走流式布局。
/// 主页元素不支持隐藏（藏掉启动入口就找不回来了），也不参与侧栏排序。
/// </summary>
public static class HomeLayoutElements
{
    public const string GroupHome = "主页";

    /// <summary>主页自由定位的宿主容器名（<c>PageHome</c> 里的舞台栅格）。</summary>
    public const string StageName = "PanHomeStage";

    public static IReadOnlyList<LayoutElementInfo> All { get; } =
    [
        // 启动按钮长在「当前实例」卡片里（卡片还带标题、状态、路径），整块搬动才不会把卡片掏空
        new("home_current", "当前实例卡片", "lucide/play", GroupHome, "CardCurrent", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(2, 4, 58, 60)),
        new("home_switch", "切换实例卡片", "lucide/layers", GroupHome, "CardSwitch", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(64, 4, 34, 60)),
        new("home_empty", "无实例引导卡片", "lucide/triangle-alert", GroupHome, "CardEmpty", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(28, 18, 44, 48)),
        new("home_note", "底部提示条", "lucide/info", GroupHome, "CardNote", StageName,
            CanHide: false, DefaultRect: new HomeDefaultRect(2, 70, 96, 16)),
    ];

    public static IReadOnlyList<string> Ids { get; } = All.Select(info => info.Id).ToArray();

    public static LayoutElementInfo? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : All.FirstOrDefault(info => string.Equals(info.Id, id, StringComparison.OrdinalIgnoreCase));
}
