using System.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.IO;

/// <summary>同名文件的处理策略。对应旧版 copy_files 的 conflict_policy 参数。</summary>
public enum CopyConflictPolicy
{
    /// <summary>覆盖目标目录中的已有文件。</summary>
    Overwrite,

    /// <summary>跳过目标目录中已存在的文件。</summary>
    SkipExisting
}

/// <summary>一次目录复制的结果。对应旧版 copy_files 的 (success, total, failed) 三元组。</summary>
public sealed record CopySummary(
    int Total,
    int Failed,
    int Skipped,
    long BytesCopied,
    IReadOnlyList<string> Errors)
{
    /// <summary>是否所有文件都成功（跳过的算成功）。</summary>
    public bool Success => Total > 0 && Failed < Total;

    /// <summary>是否所有文件都失败。</summary>
    public bool AllFailed => Total > 0 && Failed == Total;
}

/// <summary>
/// 目录复制。对应旧版 <c>copy_files</c>：流式逐文件复制（1MB 缓冲，不整文件读进内存）、
/// 单个文件失败只计数不中断、支持进度与取消。
/// 覆盖已有文件前调用方可用 <see cref="OperationJournal"/> 留下 .bak 备份并登记痕迹。
/// </summary>
public static class DirectoryCopier
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>备份元数据文件名。统计体积/文件数时一律排除它（对应旧版 backup_info.json）。</summary>
    public const string BackupInfoFileName = "backup_info.json";

    /// <summary>
    /// 把 source 目录（或单个文件）的内容复制进 target 目录。
    /// excludeRelative 里的相对路径会被跳过（例如备份的元数据文件不该被还原进游戏目录）。
    /// </summary>
    public static CopySummary Copy(string source, string target, CopyConflictPolicy policy = CopyConflictPolicy.Overwrite,
        OperationJournal? journal = null, IProgress<double>? progress = null, CancellationToken token = default,
        IReadOnlyCollection<string>? excludeRelative = null)
    {
        var errors = new List<string>();
        var total = 0;
        var failed = 0;
        var skipped = 0;
        long bytes = 0;

        try
        {
            if (string.IsNullOrWhiteSpace(source) || (!File.Exists(source) && !Directory.Exists(source)))
            {
                errors.Add($"来源不存在：{source}");
                return new CopySummary(0, 0, 0, 0, errors);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                errors.Add("目标目录为空");
                return new CopySummary(0, 0, 0, 0, errors);
            }

            var targetRoot = Path.GetFullPath(target);
            var pairs = Collect(source, targetRoot, excludeRelative);

            total = pairs.Count;
            if (total == 0) return new CopySummary(0, 0, 0, 0, errors);

            Directory.CreateDirectory(targetRoot);

            var finished = 0;
            var lastReported = -1.0;

            foreach (var pair in pairs)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if (policy == CopyConflictPolicy.SkipExisting && File.Exists(pair.Target))
                    {
                        skipped++;
                    }
                    else
                    {
                        // 落点必须在目标目录内，否则跳过（防路径穿越）
                        if (!PathGuard.IsInside(targetRoot, pair.Target))
                        {
                            failed++;
                            errors.Add($"落点超出目标目录：{pair.Source}");
                            continue;
                        }

                        var directory = Path.GetDirectoryName(pair.Target);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        if (File.Exists(pair.Target)) journal?.BackupExisting(pair.Target);
                        else journal?.TrackCreated(pair.Target);

                        CopyFile(pair.Source, pair.Target);
                        bytes += new FileInfo(pair.Target).Length;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{pair.Source}：{ex.Message}");
                    Log.Error($"复制文件失败：{pair.Source}", ex);
                }

                finished++;
                if (progress is null) continue;

                var value = (double)finished / total;
                // 进度节流：变化不足 1% 不回调
                if (value - lastReported < 0.01 && finished != total) continue;

                lastReported = value;
                progress.Report(value);
            }

            progress?.Report(1);

            if (failed > 0)
                Log.Warn($"复制完成：共 {total} 个文件，失败 {failed}，跳过 {skipped}");

            return new CopySummary(total, failed, skipped, bytes, errors);
        }
        catch (OperationCanceledException)
        {
            errors.Add("操作已取消");
            return new CopySummary(total, failed, skipped, bytes, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            Log.Error($"复制目录失败：{source} → {target}", ex);
            return new CopySummary(total, failed, skipped, bytes, errors);
        }
    }

    /// <summary>1MB 缓冲的流式单文件复制，避免大文件整份进内存。</summary>
    public static void CopyFile(string sourceFile, string targetFile)
    {
        using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);

        var buffer = new byte[BufferSize];
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// 搬移文件或目录。Directory.Move 不能跨卷，这里在跨卷时退化为「复制 + 删除源」。
    /// </summary>
    public static void MovePath(string source, string target)
    {
        if (Directory.Exists(source))
        {
            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (IOException)
            {
                // 跨卷或目标卷不支持目录搬移，退化为复制后删除
            }

            CopyDirectory(source, target);
            Directory.Delete(source, recursive: true);
            return;
        }

        if (File.Exists(source)) File.Move(source, target, overwrite: true);
    }

    /// <summary>递归复制目录（无日志、无进度、覆盖同名文件），供搬移等内部场景使用。</summary>
    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            CopyFile(file, destination);
        }
    }

    /// <summary>递归列出目录下所有文件（相对路径，"/" 分隔）。失败只记日志。</summary>
    public static IReadOnlyList<string> ListFiles(string directory, int maxCount = 200_000)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return result;

        try
        {
            var root = Path.GetFullPath(directory);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (result.Count >= maxCount) break;
                result.Add(PathGuard.RelativeOf(root, file));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"列举目录文件失败：{directory}", ex);
        }

        return result;
    }

    /// <summary>统计目录的字节数。excludeBackupInfo 为 true 时排除 backup_info.json。</summary>
    public static long GetSize(string directory, bool excludeBackupInfo = false)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

        long total = 0;

        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (excludeBackupInfo &&
                    string.Equals(file.Name, BackupInfoFileName, StringComparison.OrdinalIgnoreCase)) continue;

                total += file.Length;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"统计目录大小失败：{directory}（{ex.Message}）");
        }

        return total;
    }

    /// <summary>统计目录的文件数。excludeBackupInfo 为 true 时排除 backup_info.json。</summary>
    public static int CountFiles(string directory, bool excludeBackupInfo = true)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

        try
        {
            return new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Count(file => !excludeBackupInfo ||
                               !string.Equals(file.Name, BackupInfoFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn($"统计目录文件数失败：{directory}（{ex.Message}）");
            return 0;
        }
    }

    /// <summary>自下而上清理空目录，但绝不动 root 本身（对应旧版卸载后的空目录清理）。</summary>
    public static int CleanEmptyDirectories(string root, IEnumerable<string> relativeFilePaths)
    {
        var rootFull = PathGuard.NormalizeRoot(root);
        if (rootFull.Length == 0) return 0;

        var cleaned = 0;

        foreach (var relative in relativeFilePaths)
        {
            var parent = Path.GetDirectoryName(Path.Combine(rootFull,
                relative.Replace('/', Path.DirectorySeparatorChar)));

            while (!string.IsNullOrEmpty(parent) &&
                   !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(parent) || Directory.EnumerateFileSystemEntries(parent).Any()) break;

                    Directory.Delete(parent);
                    cleaned++;
                    parent = Path.GetDirectoryName(parent);
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理空目录失败：{parent}（{ex.Message}）");
                    break;
                }
            }
        }

        return cleaned;
    }

    /// <summary>枚举「来源文件 → 目标文件」的对照表。</summary>
    private static List<(string Source, string Target)> Collect(string source, string targetRoot,
        IReadOnlyCollection<string>? excludeRelative)
    {
        var pairs = new List<(string, string)>();

        if (File.Exists(source))
        {
            if (!IsExcluded(Path.GetFileName(source), excludeRelative))
                pairs.Add((source, Path.Combine(targetRoot, Path.GetFileName(source))));

            return pairs;
        }

        var sourceRoot = Path.GetFullPath(source);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (IsExcluded(relative.Replace('\\', '/'), excludeRelative)) continue;

            pairs.Add((file, Path.GetFullPath(Path.Combine(targetRoot, relative))));
        }

        return pairs;
    }

    private static bool IsExcluded(string relative, IReadOnlyCollection<string>? excludeRelative)
        => excludeRelative is not null &&
           excludeRelative.Contains(relative, StringComparer.OrdinalIgnoreCase);
}
