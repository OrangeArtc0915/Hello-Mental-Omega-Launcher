using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Packages;
using HMOL.Core.Localization;

namespace HMOL.Core.Updater;

/// <summary>
/// 下载页组件的下载器（补丁 / 组网组件 / 运行库）。
///
/// <para>
/// 这些组件集中放在仓库一个专用发行版的**附件**里（见 <see cref="ReleaseTag"/>）：
/// 发行版附件在 GitHub 与 Gitee 上都能匿名下载、且不受 Gitee 匿名 raw 的 10 MB 上限影响，
/// 因此大文件不必再分卷。
/// </para>
///
/// <para>
/// 线路顺序与启动器自更新共用同一份偏好（<see cref="Settings.LauncherUpdateSource"/>）：
/// 一个源失败自动换另一个。落地后按文件头复核是不是真压缩包，避免服务器用 HTML 错误页顶替文件。
/// </para>
/// </summary>
public static class RequiredAssetDownloader
{
    /// <summary>下载页组件所在发行版的 tag。</summary>
    public const string ReleaseTag = "components";

    /// <summary>
    /// Gitee 对单个附件的体积有上限，超过这个大小的包按 40 MB 切卷上传（<c>.001</c> / <c>.002</c>…），
    /// 客户端探测到第一卷就逐卷下载并合并。GitHub 不限制，仍传单个完整包。
    /// </summary>
    public const long VolumeSize = 40L * 1024 * 1024;

    /// <summary>分卷编号上限，避免探测时无限循环。</summary>
    private const int MaxVolumes = 99;

    /// <summary>第 <paramref name="index"/> 卷（从 1 开始）的文件名，如 <c>foo.zip.001</c>。</summary>
    public static string VolumeName(string fileName, int index) => $"{fileName}.{index:000}";

    /// <summary>GitHub 的发行版附件直链。</summary>
    public static string GitHubUrl(string fileName)
        => $"{AppInfo.GitHubUrl}/releases/download/{ReleaseTag}/{Uri.EscapeDataString(fileName)}";

    /// <summary>Gitee 的发行版附件直链。</summary>
    public static string GiteeUrl(string fileName)
        => $"{AppInfo.GiteeUrl}/releases/download/{ReleaseTag}/{Uri.EscapeDataString(fileName)}";

    /// <summary>
    /// 下载 <paramref name="fileName"/> 到 <paramref name="destinationPath"/>，双线路兜底。
    /// 目标已存在且是有效压缩包时会跳过下载（由 <see cref="ResumableDownloader"/> 判定）。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string fileName, string destinationPath,
        LauncherUpdateSource preferred, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return new DownloadResult(false, null, 0, Loc.T("文件名为空"));

        var order = preferred == LauncherUpdateSource.Gitee
            ? new[] { ("Gitee", GiteeUrl(fileName)), ("GitHub", GitHubUrl(fileName)) }
            : new[] { ("GitHub", GitHubUrl(fileName)), ("Gitee", GiteeUrl(fileName)) };

        var errors = new List<string>();

        foreach (var (name, url) in order)
        {
            token.ThrowIfCancellationRequested();

            // 换源前清掉上一段的半截分片：不同主机的 Range 混在一起会拼出坏文件
            TryDelete(destinationPath + ".part");

            var result = name == "Gitee"
                ? await DownloadFromGiteeAsync(fileName, destinationPath, progress, token).ConfigureAwait(false)
                : await ResumableDownloader.DownloadAsync(url, destinationPath, progress, token).ConfigureAwait(false);

            if (!result.Success)
            {
                errors.Add($"{name}：{result.Message}");
                Log.Warn(Loc.F("从 {0} 下载 {1} 失败：{2}", name, fileName, result.Message));
                continue;
            }

            // 服务器可能用 200 页面顶替文件（路径写错时返回 HTML），落地后按文件头复核
            if (!ArchiveExtractor.IsSupportedArchive(destinationPath))
            {
                errors.Add(Loc.F("{0}：下载到的不是有效压缩包", name));
                Log.Warn(Loc.F("从 {0} 下载的 {1} 不是有效压缩包，已删除", name, fileName));
                TryDelete(destinationPath);
                continue;
            }

            Log.Info(Loc.F("已下载 {0}（{1}）：{2}（{3}）", fileName, name, destinationPath, ResumableDownloader.FormatSize(LengthOf(destinationPath))));
            return result;
        }

        return new DownloadResult(false, null, 0, Loc.T("两个线路都下载失败：") + string.Join("；", errors));
    }

    /// <summary>
    /// Gitee 优先按分卷处理：探测到 <c>.001</c> 就逐卷下载、按顺序拼回原文件；
    /// 没有分卷则回退成普通单文件下载。
    /// </summary>
    private static async Task<DownloadResult> DownloadFromGiteeAsync(string fileName, string destinationPath,
        IProgress<double>? progress, CancellationToken token)
    {
        var volumes = await TryDownloadGiteeVolumesAsync(fileName, destinationPath, progress, token)
            .ConfigureAwait(false);

        if (volumes is not null) return volumes;

        return await ResumableDownloader.DownloadAsync(GiteeUrl(fileName), destinationPath, progress, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 探测并下载 Gitee 分卷。返回 null 表示该文件不是分卷，调用方应改走单文件下载。
    /// </summary>
    private static async Task<DownloadResult?> TryDownloadGiteeVolumesAsync(string fileName, string destinationPath,
        IProgress<double>? progress, CancellationToken token)
    {
        var firstUrl = GiteeUrl(VolumeName(fileName, 1));
        var firstSize = await HttpDownloader.GetContentLengthAsync(firstUrl, LauncherUpdater.UserAgent, token)
            .ConfigureAwait(false);

        if (firstSize is null or <= 0) return null;

        var volumes = new List<(string Url, long Size)> { (firstUrl, firstSize.Value) };

        for (var index = 2; index <= MaxVolumes; index++)
        {
            token.ThrowIfCancellationRequested();

            var size = await HttpDownloader
                .GetContentLengthAsync(GiteeUrl(VolumeName(fileName, index)), LauncherUpdater.UserAgent, token)
                .ConfigureAwait(false);

            if (size is null or <= 0) break;

            volumes.Add((GiteeUrl(VolumeName(fileName, index)), size.Value));
        }

        Log.Info(Loc.F("Gitee 上 {0} 按 {1} 卷上传，开始逐卷下载", fileName, volumes.Count));

        var totalBytes = volumes.Sum(volume => volume.Size);
        var received = 0L;
        var parts = new List<string>();

        try
        {
            for (var index = 0; index < volumes.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                var partPath = destinationPath + ".vol" + (index + 1).ToString("000");
                var (url, size) = volumes[index];

                TryDelete(partPath);

                var baseBytes = received;
                var report = progress is null
                    ? null
                    : new Progress<double>(value =>
                    {
                        var overall = totalBytes > 0
                            ? (baseBytes + Math.Clamp(value, 0, 1) * size) / (double)totalBytes
                            : 0;

                        progress.Report(Math.Clamp(overall, 0, 0.999));
                    });

                var result = await ResumableDownloader.DownloadAsync(url, partPath, report, token)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    TryDelete(partPath);
                    return new DownloadResult(false, null, received,
                        Loc.F("第 {0}/{1} 卷下载失败：{2}", index + 1, volumes.Count, result.Message));
                }

                parts.Add(partPath);
                received += LengthOf(partPath);
            }

            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var part in parts)
                {
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }

            foreach (var part in parts) TryDelete(part);

            progress?.Report(1);

            Log.Info(Loc.F("已合并 {0} 个分卷：{1}（{2}）", volumes.Count, destinationPath,
                ResumableDownloader.FormatSize(received)));

            return new DownloadResult(true, destinationPath, received,
                Loc.F("已合并 {0} 个分卷", volumes.Count));
        }
        catch (OperationCanceledException)
        {
            foreach (var part in parts) TryDelete(part);
            return new DownloadResult(false, null, received, Loc.T("下载已取消"));
        }
        catch (Exception ex)
        {
            foreach (var part in parts) TryDelete(part);

            Log.Error(Loc.F("合并分卷失败：{0}", destinationPath), ex);
            return new DownloadResult(false, null, received, ex.Message);
        }
    }

    /// <summary>把下载物解压到目标目录。压缩包内若只有一个顶层目录，则以其为内容根。</summary>
    public static (bool Ok, string Message) ExtractInto(string archivePath, string destinationDirectory,
        IProgress<HMOL.Core.IO.ProgressSample>? progress = null, CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(archivePath)) return (false, Loc.F("压缩包不存在：{0}", archivePath));

            var scratch = Path.Combine(Paths.ScratchFor(destinationDirectory),
                $"req_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

            try
            {
                Directory.CreateDirectory(scratch);

                if (!ArchiveExtractor.TryExtract(archivePath, scratch, out var error, progress, token))
                    return (false, string.IsNullOrWhiteSpace(error) ? Loc.T("解压失败") : error);

                if (!Directory.EnumerateFileSystemEntries(scratch).Any())
                    return (false, Loc.T("压缩包内容为空"));

                var contentRoot = ArchiveExtractor.ResolveSingleTopDirectory(scratch);

                Directory.CreateDirectory(destinationDirectory);
                CopyContents(contentRoot, destinationDirectory);

                return (true, Loc.T("解压完成"));
            }
            finally
            {
                TryDeleteDirectory(scratch);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, Loc.T("操作已取消"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("解压失败：{0} → {1}", archivePath, destinationDirectory), ex);
            return (false, ex.Message);
        }
    }

    private static void CopyContents(string sourceRoot, string destinationRoot)
    {
        if (File.Exists(sourceRoot))
        {
            var name = Path.GetFileName(sourceRoot);
            if (name.Length > 0) File.Copy(sourceRoot, Path.Combine(destinationRoot, name), overwrite: true);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destinationRoot, relative);

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Copy(file, target, overwrite: true);
        }
    }

    private static long LengthOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理临时文件失败：{0}（{1}）", path, ex.Message));
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理临时目录失败：{0}（{1}）", directory, ex.Message));
        }
    }
}
