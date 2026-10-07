using System.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

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
/// 目录复制。对应旧版 <c>copy_files</c>：流式逐文件复制（不整文件读进内存）、
/// 单个文件失败只计数不中断、支持进度与取消。
/// 进度按**字节**上报（累计已复制字节 / 总字节），单个大文件也能看到条在动；
/// 覆盖已有文件前调用方可用 <see cref="OperationJournal"/> 留下备份并登记痕迹。
/// </summary>
public static class DirectoryCopier
{
    /// <summary>读写缓冲。4MB 能把顺序写的系统调用次数压下来，大文件复制更快。</summary>
    private const int BufferSize = 4 * 1024 * 1024;

    /// <summary>进度回调的最小步进（0.5%），太密只会拖慢 UI 线程。</summary>
    private const double ProgressStep = 0.005;

    /// <summary>备份元数据文件名。统计体积/文件数时一律排除它（对应旧版 backup_info.json）。</summary>
    public const string BackupInfoFileName = "backup_info.json";

    /// <summary>
    /// 把 source 目录（或单个文件）的内容复制进 target 目录。
    /// excludeRelative 里的相对路径会被跳过（例如备份的元数据文件不该被还原进游戏目录）。
    /// <paramref name="consumeSource"/> 为 true 表示 source 是「用完即弃」的暂存内容
    /// （例如刚解压出来的目录）：与目标同分区时直接改名搬过去，省掉一整趟字节复制。
    /// </summary>
    public static CopySummary Copy(string source, string target, CopyConflictPolicy policy = CopyConflictPolicy.Overwrite,
        OperationJournal? journal = null, IProgress<ProgressSample>? progress = null, CancellationToken token = default,
        IReadOnlyCollection<string>? excludeRelative = null, bool consumeSource = false)
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
                errors.Add(Loc.F("来源不存在：{0}", source));
                return new CopySummary(0, 0, 0, 0, errors);
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                errors.Add(Loc.T("目标目录为空"));
                return new CopySummary(0, 0, 0, 0, errors);
            }

            var targetRoot = Path.GetFullPath(target);
            var plan = Collect(source, targetRoot, excludeRelative);

            var pairs = plan.Pairs;
            total = pairs.Count;
            if (total == 0) return new CopySummary(0, 0, 0, 0, errors);

            var totalBytes = plan.TotalBytes;

            // 源是暂存内容且与目标同分区时，逐文件「改名」就位，不搬字节
            var sameVolume = consumeSource && PathGuard.SameVolume(source, targetRoot);

            Directory.CreateDirectory(targetRoot);

            var finished = 0;
            var doneBytes = 0L;
            var lastReported = -1.0;

            // 按字节报进度：单个 1GB 的文件也会一路有反馈，而不是只在文件收尾时跳一下
            void Report(bool force)
            {
                if (progress is null) return;

                var value = totalBytes > 0
                    ? Math.Min(1, (double)doneBytes / totalBytes)
                    : (double)finished / total;

                if (!force && value - lastReported < ProgressStep) return;

                lastReported = value;
                progress.Report(new ProgressSample(value, doneBytes, totalBytes));
            }

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
                            errors.Add(Loc.F("落点超出目标目录：{0}", pair.Source));
                            continue;
                        }

                        var directory = Path.GetDirectoryName(pair.Target);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        if (File.Exists(pair.Target)) journal?.BackupExisting(pair.Target);
                        else journal?.TrackCreated(pair.Target);

                        if (sameVolume)
                        {
                            // 同分区改名：不搬字节，GB 级内容几乎瞬间就位
                            File.Move(pair.Source, pair.Target, overwrite: true);
                            doneBytes += pair.Size;
                            Report(force: false);
                        }
                        else
                        {
                            CopyFile(pair.Source, pair.Target, chunk =>
                            {
                                doneBytes += chunk;
                                Report(force: false);
                            }, token);
                        }

                        bytes += pair.Size;
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
                    Log.Error(Loc.F("复制文件失败：{0}", pair.Source), ex);
                }

                finished++;
                Report(force: false);
            }

            progress?.Report(new ProgressSample(1));

            if (failed > 0)
                Log.Warn(Loc.F("复制完成：共 {0} 个文件，失败 {1}，跳过 {2}", total, failed, skipped));

            return new CopySummary(total, failed, skipped, bytes, errors);
        }
        catch (OperationCanceledException)
        {
            errors.Add(Loc.T("操作已取消"));
            return new CopySummary(total, failed, skipped, bytes, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            Log.Error(Loc.F("复制目录失败：{0} → {1}", source, target), ex);
            return new CopySummary(total, failed, skipped, bytes, errors);
        }
    }

    /// <summary>
    /// 流式单文件复制，避免大文件整份进内存。
    /// <paramref name="onChunk"/> 每写完一块回调一次（用于字节级进度）；
    /// <see cref="FileOptions.SequentialScan"/> 给系统一个顺序读的提示，读大文件更快。
    /// </summary>
    public static void CopyFile(string sourceFile, string targetFile, Action<int>? onChunk = null,
        CancellationToken token = default)
    {
        using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.SequentialScan);
        using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();

            output.Write(buffer, 0, read);
            onChunk?.Invoke(read);
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
            Log.Error(Loc.F("列举目录文件失败：{0}", directory), ex);
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
            Log.Warn(Loc.F("统计目录大小失败：{0}（{1}）", directory, ex.Message));
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
            Log.Warn(Loc.F("统计目录文件数失败：{0}（{1}）", directory, ex.Message));
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
                    Log.Warn(Loc.F("清理空目录失败：{0}（{1}）", parent, ex.Message));
                    break;
                }
            }
        }

        return cleaned;
    }

    /// <summary>「来源文件 → 目标文件」对照表，连同总字节数（一次枚举就把进度分母算出来）。</summary>
    private sealed record CopyPlan(List<(string Source, string Target, long Size)> Pairs, long TotalBytes);

    /// <summary>枚举「来源文件 → 目标文件」的对照表。</summary>
    private static CopyPlan Collect(string source, string targetRoot,
        IReadOnlyCollection<string>? excludeRelative)
    {
        var pairs = new List<(string Source, string Target, long Size)>();
        var bytes = 0L;

        if (File.Exists(source))
        {
            if (!IsExcluded(Path.GetFileName(source), excludeRelative))
            {
                var size = SafeLength(source);
                pairs.Add((source, Path.Combine(targetRoot, Path.GetFileName(source)), size));
                bytes += size;
            }

            return new CopyPlan(pairs, bytes);
        }

        var sourceRoot = Path.GetFullPath(source);

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (IsExcluded(relative.Replace('\\', '/'), excludeRelative)) continue;

            var size = SafeLength(file);
            pairs.Add((file, Path.GetFullPath(Path.Combine(targetRoot, relative)), size));
            bytes += size;
        }

        return new CopyPlan(pairs, bytes);
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsExcluded(string relative, IReadOnlyCollection<string>? excludeRelative)
        => excludeRelative is not null &&
           excludeRelative.Contains(relative, StringComparer.OrdinalIgnoreCase);
}
