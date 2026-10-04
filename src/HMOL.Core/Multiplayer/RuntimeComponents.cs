using System.IO;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Packages;
using HMOL.Core.Updater;

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
    /// <summary>下载物存放目录（与联机补丁同一个文件夹，但不进包列表）。</summary>
    public static string DownloadDirectory => Path.Combine(Paths.Data, "MultiplayerRequired");

    /// <summary>全部组件（固定顺序，界面按这个顺序展示）。</summary>
    public static readonly RuntimeComponent[] All =
        [RuntimeComponent.EasyTier, RuntimeComponent.N2n, RuntimeComponent.Tap, RuntimeComponent.WinIpBroadcast];

    /// <summary>界面显示名。</summary>
    public static string DisplayName(RuntimeComponent component) => component switch
    {
        RuntimeComponent.EasyTier => "EasyTier 组网组件",
        RuntimeComponent.N2n => "n2n 组网组件",
        RuntimeComponent.Tap => "TAP 虚拟网卡驱动",
        RuntimeComponent.WinIpBroadcast => "WinIPBroadcast 广播转发",
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

    /// <summary>仓库里的压缩包名（同时是下载到本机后的文件名）。</summary>
    public static string ArchiveName(RuntimeComponent component) => FolderName(component) + ".zip";

    /// <summary>
    /// 分卷文件名。Gitee 对匿名 raw 下载有大小上限（超过会 403「large file require login」），
    /// 因此超过阈值的组件拆成多卷，下载后按顺序合并再解压；未拆分的组件返回整包名。
    /// </summary>
    public static string[] PartNames(RuntimeComponent component) => component switch
    {
        RuntimeComponent.EasyTier =>
            ["easytier.zip.001", "easytier.zip.002", "easytier.zip.003", "easytier.zip.004"],
        _ => [ArchiveName(component)]
    };

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
    /// 确保某个组件就绪：已安装直接返回；否则下载（双线路、必要时分卷合并）后解压到
    /// <c>runtime\&lt;组件&gt;\</c>。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureAsync(RuntimeComponent component,
        LauncherUpdateSource preferred, IProgress<double>? downloadProgress = null,
        IProgress<ProgressSample>? extractProgress = null, CancellationToken token = default)
    {
        if (IsInstalled(component)) return (true, $"{DisplayName(component)} 已就绪");

        var name = DisplayName(component);
        var archive = ArchivePath(component);

        // 合并后的压缩包已在且有效 → 无需重下
        if (!(File.Exists(archive) && ArchiveExtractor.IsSupportedArchive(archive)))
        {
            var parts = PartNames(component);

            if (parts.Length <= 1)
            {
                var download = await RequiredAssetDownloader.DownloadAsync(
                    parts[0], archive, preferred, downloadProgress, token).ConfigureAwait(false);

                if (!download.Success) return (false, download.Message);
            }
            else
            {
                var partPaths = new List<string>();

                for (var index = 0; index < parts.Length; index++)
                {
                    var partPath = Path.Combine(DownloadDirectory, parts[index]);

                    var download = await RequiredAssetDownloader.DownloadAsync(
                            parts[index], partPath, preferred, downloadProgress, token, expectArchive: false)
                        .ConfigureAwait(false);

                    if (!download.Success)
                        return (false, $"{name} 第 {index + 1}/{parts.Length} 卷下载失败：{download.Message}");

                    partPaths.Add(partPath);
                }

                if (!MergeParts(partPaths, archive)) return (false, $"{name} 分卷合并失败");

                if (!ArchiveExtractor.IsSupportedArchive(archive))
                {
                    // 分卷残缺或串了源：清掉本机分卷并删除坏合并结果，避免下次又拿坏分卷合并
                    CleanupPartFiles(component);
                    TryDelete(archive);
                    return (false, $"{name} 合并后的压缩包无效，请重试（已清掉本机分卷）");
                }
            }
        }

        var (ok, message) = RequiredAssetDownloader.ExtractInto(archive, InstallDirectory(component), extractProgress, token);

        if (!ok) return (false, $"解压 {name} 失败：{message}");
        if (!IsInstalled(component)) return (false, $"{name} 解压完成但未找到标志文件，请确认压缩包内容。");

        Log.Info($"组网组件已安装：{name} → {InstallDirectory(component)}");
        return (true, $"{name} 已就绪");
    }

    /// <summary>按顺序把分卷拼成完整压缩包。</summary>
    private static bool MergeParts(IReadOnlyList<string> partPaths, string destination)
    {
        try
        {
            using var output = File.Create(destination);

            foreach (var part in partPaths)
            {
                if (!File.Exists(part)) return false;

                using var input = File.OpenRead(part);
                input.CopyTo(output);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"合并分卷失败：{destination}", ex);
            return false;
        }
    }

    /// <summary>清掉本机已下载的分卷（不含合并后的整包）。</summary>
    private static void CleanupPartFiles(RuntimeComponent component)
    {
        foreach (var part in PartNames(component))
        {
            if (string.Equals(part, ArchiveName(component), StringComparison.OrdinalIgnoreCase)) continue;

            TryDelete(Path.Combine(DownloadDirectory, part));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理文件失败：{path}（{ex.Message}）");
        }
    }
}
