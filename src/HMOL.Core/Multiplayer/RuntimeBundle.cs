using System.IO;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;
using HMOL.Core.Updater;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 「联机功能」整包：把 EasyTier / n2n / TAP / WinIPBroadcast 四个组网组件收在一个压缩包里，
/// 下载一次即可全部就绪（原来的四个独立附件不再从界面暴露）。
///
/// <para>
/// 包内按 <c>easytier\</c> / <c>n2n\</c> / <c>tap\</c> / <c>winipbroadcast\</c> 分目录存放，
/// 解压后原样搬到 exe 旁的 <c>runtime\</c> 下，<see cref="RuntimeLocator"/> 的定位规则保持不变。
/// </para>
///
/// <para>
/// Gitee 单附件有体积上限，所以 Gitee 侧按 40 MB 切卷上传；下载器会自动探测并合并
/// （见 <see cref="RequiredAssetDownloader"/>）。GitHub 侧仍是单个完整包。
/// </para>
/// </summary>
public static class RuntimeBundle
{
    /// <summary>整包文件名（GitHub 附件 / Gitee 分卷的基名）。</summary>
    public const string ArchiveName = "multiplayer-bundle.zip";

    /// <summary>整包在本机的落地路径。</summary>
    public static string ArchivePath => Path.Combine(RuntimeComponents.DownloadDirectory, ArchiveName);

    /// <summary>「联机功能」是否已安装：四个组件全部就绪。</summary>
    public static bool IsInstalled => RuntimeComponents.All.All(RuntimeComponents.IsInstalled);

    /// <summary>还缺哪几个组件（已安装时为空）。</summary>
    public static IReadOnlyList<RuntimeComponent> Missing
        => RuntimeComponents.All.Where(component => !RuntimeComponents.IsInstalled(component)).ToArray();

    /// <summary>
    /// 安装或补全整包：已有整包就直接解压分发，否则先下载。
    /// 已经就绪的组件会被一并覆盖成包内版本，保证四个组件版本一致。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureAsync(LauncherUpdateSource preferred,
        IProgress<double>? downloadProgress = null, IProgress<ProgressSample>? extractProgress = null,
        CancellationToken token = default)
    {
        if (IsInstalled) return (true, Loc.T("联机功能已就绪"));

        var archive = ArchivePath;

        if (!File.Exists(archive))
        {
            var download = await RequiredAssetDownloader
                .DownloadAsync(ArchiveName, archive, preferred, downloadProgress, token)
                .ConfigureAwait(false);

            if (!download.Success)
                return (false, Loc.F("下载联机功能失败：{0}", download.Message));
        }

        var bundleRoot = Path.Combine(Paths.ScratchFor(archive), $"bundle_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        var bundleDir = Path.Combine(bundleRoot, "content");

        try
        {
            var (ok, message) = RequiredAssetDownloader.ExtractInto(archive, bundleDir, extractProgress, token);

            if (!ok) return (false, Loc.F("解压联机功能失败：{0}", message));

            var distributed = 0;

            foreach (var component in RuntimeComponents.All)
            {
                token.ThrowIfCancellationRequested();

                var source = FindComponentDirectory(bundleDir, component);

                if (source is null)
                    return (false, Loc.F("联机功能包里缺少 {0}", RuntimeComponents.DisplayName(component)));

                CopyDirectory(source, RuntimeComponents.InstallDirectory(component));
                distributed++;
            }

            var missing = Missing;

            if (missing.Count > 0)
            {
                var names = string.Join(Loc.T("、"), missing.Select(RuntimeComponents.DisplayName));

                return (false, Loc.F("联机功能解压完成，但以下组件没找到标志文件：{0}", names));
            }

            // 安装成功后删掉整包，避免一直占着几十兆
            TryDeleteFile(archive);

            Log.Info(Loc.F("联机功能已安装：{0} 个组件 → {1}", distributed, RuntimeLocator.RuntimeRoot));
            return (true, Loc.T("联机功能已就绪"));
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
        }
    }

    /// <summary>
    /// 在解压出来的内容根里找某个组件：优先用同名子目录，找不到再按标志文件名递归搜索。
    /// </summary>
    private static string? FindComponentDirectory(string bundleDir, RuntimeComponent component)
    {
        var preferred = Path.Combine(bundleDir, RuntimeComponents.FolderName(component));

        if (File.Exists(Path.Combine(preferred, RuntimeComponents.MarkerFile(component)))) return preferred;

        var marker = RuntimeComponents.MarkerFile(component);

        try
        {
            foreach (var file in Directory.EnumerateFiles(bundleDir, marker, SearchOption.AllDirectories))
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrWhiteSpace(directory)) return directory;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("在联机功能包里查找 {0} 失败：{1}", RuntimeComponents.DisplayName(component), ex.Message));
        }

        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn(Loc.F("清理临时文件失败：{0}（{1}）", path, ex.Message)); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Warn(Loc.F("清理临时目录失败：{0}（{1}）", path, ex.Message)); }
    }
}
