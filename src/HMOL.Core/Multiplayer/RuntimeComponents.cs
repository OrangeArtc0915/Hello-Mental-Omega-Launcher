using System.IO;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Packages;
using HMOL.Core.Updater;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>联机要用的组网组件（原来随包放在 runtime\ 下，现改为按需下载）。</summary>
public enum RuntimeComponent
{
    /// <summary>EasyTier 三层组网（easytier-core.exe / easytier-cli.exe）。</summary>
    EasyTier,

    /// <summary>n2n 二层组网（edge.exe / supernode.exe）。</summary>
    N2n,

    /// <summary>TAP 虚拟网卡驱动（n2n 依赖）。</summary>
    Tap,

    /// <summary>WinIPBroadcast 局域网广播转发服务。</summary>
    WinIpBroadcast
}

/// <summary>
/// 组网组件（runtime）的下载、解压与状态查询。
///
/// <para>
/// 发行包不再附带 <c>runtime\</c>：组件按种类拆成独立压缩包放在仓库 Resources 分支，
/// 需要时下载并解压回 exe 旁的 <c>runtime\&lt;组件&gt;\</c>（<see cref="RuntimeLocator"/> 的定位规则不变）。
/// 这样发行包体积小、组件也能单独更新。
/// </para>
/// </summary>
public static class RuntimeComponents
{
    /// <summary>下载物存放目录（按需下载的组件都先落这里，但不进包列表）。</summary>
    public static string DownloadDirectory => Path.Combine(Paths.Data, "MultiplayerRequired");

    /// <summary>全部组件（固定顺序，界面按这个顺序展示）。</summary>
    public static readonly RuntimeComponent[] All =
        [RuntimeComponent.EasyTier, RuntimeComponent.N2n, RuntimeComponent.Tap, RuntimeComponent.WinIpBroadcast];

    /// <summary>界面显示名。</summary>
    public static string DisplayName(RuntimeComponent component) => component switch
    {
        RuntimeComponent.EasyTier => Loc.T("EasyTier 组网组件"),
        RuntimeComponent.N2n => Loc.T("n2n 组网组件"),
        RuntimeComponent.Tap => Loc.T("TAP 虚拟网卡驱动"),
        RuntimeComponent.WinIpBroadcast => Loc.T("WinIPBroadcast 广播转发"),
        _ => component.ToString()
    };

    /// <summary>运行时目录名（<c>runtime\&lt;目录名&gt;\</c>）。</summary>
    public static string FolderName(RuntimeComponent component) => component switch
    {
        RuntimeComponent.EasyTier => "easytier",
        RuntimeComponent.N2n => "n2n",
        RuntimeComponent.Tap => "tap",
        RuntimeComponent.WinIpBroadcast => "winipbroadcast",
        _ => component.ToString().ToLowerInvariant()
    };

    /// <summary>发行版附件里的压缩包名（同时是下载到本机后的文件名）。</summary>
    public static string ArchiveName(RuntimeComponent component) => FolderName(component) + ".zip";

    /// <summary>判断组件是否齐备用的标志文件。</summary>
    public static string MarkerFile(RuntimeComponent component) => component switch
    {
        RuntimeComponent.EasyTier => "easytier-core.exe",
        RuntimeComponent.N2n => "edge.exe",
        RuntimeComponent.Tap => "tapinstall.exe",
        RuntimeComponent.WinIpBroadcast => "WinIPBroadcast-1.6.exe",
        _ => string.Empty
    };

    /// <summary>组件的安装目录：exe 旁 <c>runtime\&lt;组件&gt;\</c>。</summary>
    public static string InstallDirectory(RuntimeComponent component)
        => Path.Combine(RuntimeLocator.RuntimeRoot, FolderName(component));

    /// <summary>组件压缩包下载到本机后的路径。</summary>
    public static string ArchivePath(RuntimeComponent component)
        => Path.Combine(DownloadDirectory, ArchiveName(component));

    /// <summary>某个组件是否已就绪。</summary>
    public static bool IsInstalled(RuntimeComponent component)
    {
        try
        {
            var marker = MarkerFile(component);
            if (marker.Length == 0) return false;

            return File.Exists(Path.Combine(InstallDirectory(component), marker));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>某个方案需要哪些组件。</summary>
    public static RuntimeComponent[] RequiredFor(NetworkEngineKind kind) => kind switch
    {
        NetworkEngineKind.EasyTier => [RuntimeComponent.EasyTier],
        NetworkEngineKind.N2n => [RuntimeComponent.N2n, RuntimeComponent.Tap],
        _ => []
    };

    /// <summary>某个方案缺少的组件（齐备时为空）。</summary>
    public static IReadOnlyList<RuntimeComponent> MissingFor(NetworkEngineKind kind)
        => RequiredFor(kind).Where(component => !IsInstalled(component)).ToArray();

    /// <summary>
    /// 确保某个组件就绪：已安装直接返回；否则从发行版附件下载后解压到 <c>runtime\&lt;组件&gt;\</c>。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureAsync(RuntimeComponent component,
        LauncherUpdateSource preferred, IProgress<double>? downloadProgress = null,
        IProgress<ProgressSample>? extractProgress = null, CancellationToken token = default)
    {
        if (IsInstalled(component)) return (true, Loc.F("{0} 已就绪", DisplayName(component)));

        var name = DisplayName(component);
        var archive = ArchivePath(component);

        // 压缩包已在且有效 → 无需重下
        if (!(File.Exists(archive) && ArchiveExtractor.IsSupportedArchive(archive)))
        {
            var download = await RequiredAssetDownloader.DownloadAsync(
                ArchiveName(component), archive, preferred, downloadProgress, token).ConfigureAwait(false);

            if (!download.Success) return (false, download.Message);
        }

        var (ok, message) = RequiredAssetDownloader.ExtractInto(archive, InstallDirectory(component), extractProgress, token);

        if (!ok) return (false, Loc.F("解压 {0} 失败：{1}", name, message));
        if (!IsInstalled(component)) return (false, Loc.F("{0} 解压完成但未找到标志文件，请确认压缩包内容。", name));

        Log.Info(Loc.F("组网组件已安装：{0} → {1}", name, InstallDirectory(component)));
        return (true, Loc.F("{0} 已就绪", name));
    }
}
