namespace HMOL.Core.App;

/// <summary>
/// 应用自身的元信息。版本号、作者、仓库与 QQ 群只在这里定义一处，
/// 界面与文档统一引用这里的常量，避免多处硬编码导致不一致。
/// </summary>
public static class AppInfo
{
    public const string Name = "Hello Mental Omega Launcher";
    public const string Version = "1.1.0";
    public const string VersionDisplay = "v1.1.0";
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
}
