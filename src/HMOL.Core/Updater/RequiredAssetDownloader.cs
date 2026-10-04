using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Packages;

namespace HMOL.Core.Updater;

/// <summary>
/// 从仓库 <c>Resources</c> 分支的「HMOL Online Required Files」目录下载必要文件（组网组件 / 7-Zip / MO 联机补丁）。
///
/// <para>
/// 线路顺序与启动器自更新共用同一份偏好（<see cref="Settings.LauncherUpdateSource"/>）：
/// GitHub 优先或 Gitee 优先，一个源失败自动换另一个。落地后按文件头复核是不是真压缩包，
/// 避免服务器用 HTML 错误页顶替文件。
/// </para>
/// </summary>
public static class RequiredAssetDownloader
{
    /// <summary>资源所在仓库分支（GitHub 与 Gitee 同名）。</summary>
    public const string BranchName = "Resources";

    /// <summary>资源目录（已 URL 编码，两种线路共用）。</summary>
    private const string FolderPath = "HMOL%20Online%20Required%20Files";

    /// <summary>GitHub 的 raw 直链。</summary>
    public static string GitHubUrl(string fileName)
        => AppInfo.GitHubUrl
               .Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
           + $"/{BranchName}/{FolderPath}/{Uri.EscapeDataString(fileName)}";

    /// <summary>Gitee 的 raw 直链。</summary>
    public static string GiteeUrl(string fileName)
        => $"{AppInfo.GiteeUrl}/raw/{BranchName}/{FolderPath}/{Uri.EscapeDataString(fileName)}";

    /// <summary>分卷文件的最小合理大小。小于它就当作错误页（Gitee 403 页才 55 字节）。</summary>
    private const long MinPartBytes = 64 * 1024;

    /// <summary>
    /// 下载 <paramref name="fileName"/> 到 <paramref name="destinationPath"/>，双线路兜底。
    /// 目标已存在且是有效压缩包时会跳过下载（由 <see cref="ResumableDownloader"/> 判定）。
    /// <paramref name="expectArchive"/> 为 false 时按「分卷」处理：不做压缩包校验，只卡最小体积。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string fileName, string destinationPath,
        LauncherUpdateSource preferred, IProgress<double>? progress = null, CancellationToken token = default,
        bool expectArchive = true)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return new DownloadResult(false, null, 0, "文件名为空");

        var order = preferred == LauncherUpdateSource.Gitee
            ? new[] { ("Gitee", GiteeUrl(fileName)), ("GitHub", GitHubUrl(fileName)) }
            : new[] { ("GitHub", GitHubUrl(fileName)), ("Gitee", GiteeUrl(fileName)) };

        var errors = new List<string>();

        foreach (var (name, url) in order)
        {
            token.ThrowIfCancellationRequested();

            // 换源前清掉上一段的半截分片：不同主机的 Range 混在一起会拼出坏文件
            TryDelete(destinationPath + ".part");

            var result = await ResumableDownloader.DownloadAsync(url, destinationPath, progress, token)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                errors.Add($"{name}：{result.Message}");
                Log.Warn($"从 {name} 下载 {fileName} 失败：{result.Message}");
                continue;
            }

            // 服务器可能用 200 页面顶替文件（路径写错时返回 HTML），落地后按文件头复核
            if (!IsAcceptable(destinationPath, expectArchive))
            {
                errors.Add($"{name}：{(expectArchive ? "下载到的不是有效压缩包" : "下载到的分卷过小，疑似错误页")}");
                Log.Warn($"从 {name} 下载的 {fileName} 不可用，已删除");
                TryDelete(destinationPath);
                continue;
            }

            Log.Info($"已下载 {fileName}（{name}）：{destinationPath}（{ResumableDownloader.FormatSize(LengthOf(destinationPath))}）");
            return result;
        }

        return new DownloadResult(false, null, 0, "两个线路都下载失败：" + string.Join("；", errors));
    }

    /// <summary>落地文件是否可用：整包看是不是压缩包，分卷看体积是否不像错误页。</summary>
    private static bool IsAcceptable(string path, bool expectArchive)
        => expectArchive ? ArchiveExtractor.IsSupportedArchive(path) : LengthOf(path) >= MinPartBytes;

    /// <summary>把下载物解压到目标目录。压缩包内若只有一个顶层目录，则以其为内容根。</summary>
    public static (bool Ok, string Message) ExtractInto(string archivePath, string destinationDirectory,
        IProgress<HMOL.Core.IO.ProgressSample>? progress = null, CancellationToken token = default)
    {
        try
        {
            if (!File.Exists(archivePath)) return (false, $"压缩包不存在：{archivePath}");

            var scratch = Path.Combine(Paths.ScratchFor(destinationDirectory),
                $"req_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

            try
            {
                Directory.CreateDirectory(scratch);

                if (!ArchiveExtractor.TryExtract(archivePath, scratch, out var error, progress, token))
                    return (false, string.IsNullOrWhiteSpace(error) ? "解压失败" : error);

                if (!Directory.EnumerateFileSystemEntries(scratch).Any())
                    return (false, "压缩包内容为空");

                var contentRoot = ArchiveExtractor.ResolveSingleTopDirectory(scratch);

                Directory.CreateDirectory(destinationDirectory);
                CopyContents(contentRoot, destinationDirectory);

                return (true, "解压完成");
            }
            finally
            {
                TryDeleteDirectory(scratch);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "操作已取消");
        }
        catch (Exception ex)
        {
            Log.Error($"解压失败：{archivePath} → {destinationDirectory}", ex);
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
            Log.Warn($"清理临时文件失败：{path}（{ex.Message}）");
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
            Log.Warn($"清理临时目录失败：{directory}（{ex.Message}）");
        }
    }
}
