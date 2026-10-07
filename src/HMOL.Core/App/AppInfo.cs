
using HMOL.Core.Localization;
namespace HMOL.Core.App;

/// <summary>
/// 应用自身的元信息。版本号、作者、仓库与 QQ 群只在这里定义一处，
/// 界面与文档统一引用这里的常量，避免多处硬编码导致不一致。
/// </summary>
public static class AppInfo
{
    public const string Name = "Hello Mental Omega Launcher";
    public const string Version = "1.5.12";
    public const string VersionDisplay = "v1.5.12";
    public const string Author = "mmm";

    /// <summary>许可声明摘要：PolyForm Noncommercial 1.0.0，免费非商业使用（许可证正文见仓库根 LICENSE）。</summary>
    public const string License = "PolyForm Noncommercial 1.0.0";

    // 展示用的仓库地址不带结尾的 .git
    public const string GitHubUrl = "https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher";
    public const string GitHubIssuesUrl = GitHubUrl + "/issues";
    public const string GitHubReleasesUrl = GitHubUrl + "/releases";

    public const string GiteeUrl = "https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher";
    public const string GiteeReleasesUrl = GiteeUrl + "/releases";

    /// <summary>项目官网（GitHub Pages）。关于窗口里点它用默认浏览器打开。</summary>
    public const string WebsiteUrl = "https://orangeartc0915.github.io/Hello-Mental-Omega-Launcher/";

    public const string QqGroup = "1034243331";
    public const string QqGroupUrl = "https://qm.qq.com/q/ia8Zv2AtEY";

    /// <summary>
    /// 鸣谢：参与测试与反馈的朋友。名字按顺序展示，<see cref="ThanksCredit.Title"/> 为空表示不显示头衔标签。
    /// 「关于」窗口与 README 都从这里/同一份名单取值。属性（而非静态字段）以便切换语言后重新求值。
    /// </summary>
    public static ThanksCredit[] Thanks =>
    [
        new(Loc.T("罒ω罒"), Loc.T("猫娘")),
        new(Loc.T("绮梦"), Loc.T("猪")),
        new(Loc.T("心弦连"), Loc.T("猫娘")),
        new("a114514", ""),
        new(Loc.T("艾尔登皮蛋"), ""),
        new(Loc.T("安然的岛不叫安然"), ""),
        new(Loc.T("白小梦です"), Loc.T("梦梦酱")),
        new(Loc.T("雪枫"), Loc.T("猪")),
        new("Drawer😳", ""),
        new(Loc.T("枫～"), ""),
        new(Loc.T("飞行"), ""),
        new("fs bga3", ""),
        new(Loc.T("鸽尔德⁧ ~咕⁧‭"), ""),
        new("GinkgobilobaL", ""),
        new(Loc.T("靖安司管"), ""),
        new("Lmsh", ""),
        new(Loc.T("墨笙"), ""),
        new(Loc.T("ovide（内奸）"), ""),
        new(Loc.T("浅梦"), ""),
        new(Loc.T("世蓝喧"), ""),
        new(Loc.T("月見ヤチヨ是一"), ""),
        new(Loc.T("夜幕"), ""),
        new("YUN✨RÚ", ""),
        new("zxc", ""),
        new(Loc.T("悲伤的天使"), ""),
        new(Loc.T("ㅤ弃世"), ""),
        new("07", Loc.T("沃尔玛购物袋")),
        new(Loc.T("快猫_Channel"), Loc.T("雌小鬼"))
    ];
}

/// <summary>鸣谢名单里的一项：名字 + 头衔（头衔为空表示不显示）。</summary>
public sealed record ThanksCredit(string Name, string Title);
