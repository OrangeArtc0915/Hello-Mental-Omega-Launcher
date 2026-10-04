namespace HMOL.Core.App;

/// <summary>
/// 应用自身的元信息。版本号、作者、仓库与 QQ 群只在这里定义一处，
/// 界面与文档统一引用这里的常量，避免多处硬编码导致不一致。
/// </summary>
public static class AppInfo
{
    public const string Name = "Hello Mental Omega Launcher";
    public const string Version = "1.5.3";
    public const string VersionDisplay = "v1.5.3";
    public const string Author = "mmm";

    /// <summary>许可声明摘要：本项目为专有软件，保留所有权利（许可证正文见仓库根 LICENSE）。</summary>
    public const string License = "保留所有权利";

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
    /// 「关于」窗口与 README 都从这里/同一份名单取值。
    /// </summary>
    public static readonly ThanksCredit[] Thanks =
    [
        new("罒ω罒", "猫娘"),
        new("绮梦", "猪"),
        new("心弦连", "猫娘"),
        new("a114514", ""),
        new("艾尔登皮蛋", ""),
        new("安然的岛不叫安然", ""),
        new("白小梦です", "梦梦酱"),
        new("雪枫", "猪"),
        new("Drawer😳", ""),
        new("枫～", ""),
        new("飞行", ""),
        new("fs bga3", ""),
        new("鸽尔德⁧ ~咕⁧‭", ""),
        new("GinkgobilobaL", ""),
        new("靖安司管", ""),
        new("Lmsh", ""),
        new("墨笙", ""),
        new("ovide（内奸）", ""),
        new("浅梦", ""),
        new("世蓝喧", ""),
        new("月見ヤチヨ是一", ""),
        new("夜幕", ""),
        new("YUN✨RÚ", ""),
        new("zxc", ""),
        new("悲伤的天使", ""),
        new("ㅤ弃世", ""),
        new("07", "沃尔玛购物袋")
    ];
}

/// <summary>鸣谢名单里的一项：名字 + 头衔（头衔为空表示不显示）。</summary>
public sealed record ThanksCredit(string Name, string Title);
