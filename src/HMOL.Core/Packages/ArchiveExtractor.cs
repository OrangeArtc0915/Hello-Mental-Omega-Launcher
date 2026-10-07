using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Packages;

/// <summary>
/// 压缩包解压器。zip / 7z / rar / tar / gz 统一走 SharpCompress，格式靠文件头嗅探而非扩展名判断，
/// 因此把 7z 改名成 .zip 也能正确解压。条目一律流式写出，不整份读进内存；
/// 每个条目做路径穿越校验：含 ".."、绝对路径、盘符，或解析后落到目标目录之外的条目一律跳过。
/// <para>
/// zip 例外：走 .NET 内置的 <see cref="ZipArchive"/>。SharpCompress 解 zip 明显更慢，
/// 而 zip 恰恰是绝大多数包用的格式；内置实现还能直接读到每个条目的大小，进度可以按字节走。
/// </para>
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>条目读写缓冲。</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>进度回调的最小步进（0.5%）。</summary>
    private const double ProgressStep = 0.005;

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZipEmptyMagic = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21];
    private static readonly byte[] GzipMagic = [0x1F, 0x8B];
    private static readonly byte[] SevenZipMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>是否是我们支持的压缩包。</summary>
    public static bool IsSupportedArchive(string path) => DetectFormat(path) is not null;

    /// <summary>列出压缩包内的条目名（用于安装前预览，对应旧版 _peek_package_contents）。</summary>
    public static IReadOnlyList<string> ListEntries(string archivePath, int maxCount = 30)
    {
        var result = new List<string>();

        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath)) return result;

            if (DetectFormat(archivePath) == "zip")
            {
                using var zip = ZipFile.OpenRead(archivePath);

                foreach (var entry in zip.Entries)
                {
                    if (result.Count >= maxCount) break;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    result.Add(entry.FullName);
                }

                return result;
            }

            using var archive = ArchiveFactory.Open(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (result.Count >= maxCount) break;
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;

                result.Add(entry.Key);
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("读取压缩包内容失败：{0}", archivePath), ex);
        }

        return result;
    }

    /// <summary>解压到目标目录（自动识别 zip / rar / 7z / tar / gz）。</summary>
    public static bool TryExtract(string archivePath, string destinationDirectory, out string? error,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                error = Loc.F("压缩包不存在：{0}", archivePath);
                return false;
            }

            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                error = Loc.T("目标目录为空");
                return false;
            }

            var format = DetectFormat(archivePath);
            if (format is null)
            {
                error = Loc.F("不支持的压缩格式：{0}", Path.GetFileName(archivePath));
                return false;
            }

            var destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);

            token.ThrowIfCancellationRequested();

            var archiveName = Path.GetFileName(archivePath);

            // zip 走内置实现：更快，而且能直接拿到每个条目的大小，进度可以按字节走
            if (format == "zip")
                return ExtractZip(archivePath, destinationRoot, archiveName, progress, token);

            // 7z 交给随包的原生 7-Zip：SharpCompress 的托管实现解 GB 级固实包要几分钟，
            // 还会把空文件当「没有流」整批跳过（装出来的包残缺）。
            // 注意 7za.exe 是 Standalone 版，不支持 rar，所以 rar 只能继续走 SharpCompress。
            if (format == "7z" && SevenZipTool.IsAvailable)
            {
                var nativeError = ExtractNative(archivePath, destinationRoot, archiveName, progress, token);

                if (nativeError is not null)
                {
                    error = nativeError;
                    return false;
                }

                return true;
            }

            if (format == "7z")
            {
                // 回退到 SharpCompress 会慢一个数量级，日志里明说，别让人对着进度条猜
                Log.Warn(Loc.F("{0}；本次 7z 解压改用慢速实现，耗时会长很多", SevenZipTool.MissingMessage));
            }

            using var archive = ArchiveFactory.Open(archivePath);

            // .tar.gz 会被 SharpCompress 当成「一个 gz 条目（内容是一个 tar）」，
            // 这里再往里拆一层：内容是 tar 就用 TarArchive 读，否则按单文件 gzip 处理。
            // 解出来的内容可能有好几个 GB，落成临时文件而不是 MemoryStream，别把内存吃干。
            if (archive.Type == ArchiveType.GZip)
            {
                var nested = archive.Entries.FirstOrDefault(entry => !entry.IsDirectory);

                if (nested is not null)
                {
                    var temp = Path.Combine(Paths.ScratchFor(destinationRoot),
                        $"gz_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

                    try
                    {
                        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                                   BufferSize, FileOptions.SequentialScan))
                        using (var entryStream = nested.OpenEntryStream())
                        {
                            entryStream.CopyTo(file);
                        }

                        using var decompressed = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read,
                            BufferSize, FileOptions.SequentialScan);

                        if (TarArchive.IsTarFile(decompressed))
                        {
                            decompressed.Position = 0;

                            using var tar = TarArchive.Open(decompressed);
                            return ExtractEntries(tar.Entries, destinationRoot, archiveName, format, progress, token);
                        }

                        decompressed.Position = 0;
                        return ExtractSingleStream(decompressed, nested.Key, destinationRoot, archiveName, format,
                            progress, token);
                    }
                    finally
                    {
                        TryDeleteFile(temp);
                    }
                }
            }

            return ExtractEntries(archive.Entries, destinationRoot, archiveName, format, progress, token);
        }
        catch (OperationCanceledException)
        {
            error = Loc.T("操作已取消");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error(Loc.F("解压失败：{0}", archivePath), ex);
            return false;
        }
    }

    /// <summary>
    /// 解压后若只有一个顶层目录，则把该目录当作内容根（对应旧版安装时的「智能识别」）。
    /// 顶层有多项或唯一项是文件时返回原目录。
    /// </summary>
    public static string ResolveSingleTopDirectory(string extractDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(extractDirectory) || !Directory.Exists(extractDirectory))
                return extractDirectory;

            var entries = Directory.GetFileSystemEntries(extractDirectory);
            if (entries.Length != 1) return extractDirectory;

            return Directory.Exists(entries[0]) ? entries[0] : extractDirectory;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("识别解压根目录失败：{0}（{1}）", extractDirectory, ex.Message));
            return extractDirectory;
        }
    }

    /// <summary>
    /// 用随包的 7-Zip 原生程序解压。它只报百分比，所以先用元数据读出未压缩总字节数，
    /// 换算成字节后上层照样能显示速度与剩余时间。返回 null 表示成功，否则是给用户看的原因。
    /// </summary>
    private static string? ExtractNative(string archivePath, string destinationRoot, string archiveName,
        IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var totalBytes = UncompressedSizeOf(archivePath);

        var (ok, error) = SevenZipTool.Extract(archivePath, destinationRoot,
            progress is null ? null : new PercentProgress(progress, totalBytes), token);

        if (!ok) return Loc.F("解压失败：{0}", error);

        progress?.Report(new ProgressSample(1, totalBytes, totalBytes));

        Log.Info(Loc.F("解压完成：{0}（7-Zip 原生", archiveName) +
                 (totalBytes > 0 ? $"，{totalBytes / 1024d / 1024d:F1} MB" : string.Empty) +
                 $"）→ {destinationRoot}");

        return null;
    }

    /// <summary>从压缩包元数据读未压缩总字节数（只读头，不解压）。取不到返回 0。</summary>
    private static long UncompressedSizeOf(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.Open(archivePath);

            return archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Sum(entry => Math.Max(0L, entry.Size));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取压缩包总大小失败，本次进度只显示百分比：{0}（{1}）", archivePath, ex.Message));
            return 0;
        }
    }

    /// <summary>
    /// zip 解压。走 .NET 内置 ZipArchive：比 SharpCompress 快，且条目自带未压缩大小，进度可按字节走。
    /// </summary>
    private static bool ExtractZip(string archivePath, string destinationRoot, string archiveName,
        IProgress<ProgressSample>? progress, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        // 目录条目（Name 为空）不需要单独创建：解压文件时会按需建目录
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var totalBytes = files.Sum(entry => Math.Max(0L, entry.Length));

        var finished = 0;
        var rejected = 0;
        var doneBytes = 0L;
        var lastReported = -1.0;

        void Report()
        {
            if (progress is null) return;

            var value = totalBytes > 0
                ? Math.Min(1, (double)doneBytes / totalBytes)
                : files.Count > 0 ? (double)finished / files.Count : 1;

            if (value - lastReported < ProgressStep) return;

            lastReported = value;
            progress.Report(new ProgressSample(value, doneBytes, totalBytes));
        }

        foreach (var entry in files)
        {
            token.ThrowIfCancellationRequested();

            var key = entry.FullName;

            if (!PathGuard.TryResolve(destinationRoot, key, out var targetPath))
            {
                rejected++;
                Log.Warn(Loc.F("压缩包条目路径不合法，已跳过：{0}（{1}）", key, archiveName));
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var source = entry.Open();
                using var destination = File.Create(targetPath);

                // 空条目不用读流：省得为每个 0 字节条目白开一块缓冲
                if (entry.Length <= 0) continue;

                CopyWithProgress(source, destination, entry.Length, chunk =>
                {
                    doneBytes += chunk;
                    Report();
                }, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个条目失败不影响整体
                Log.Warn(Loc.F("解压条目失败，已跳过：{0}（{1}）", key, ex.Message));
            }

            finished++;
            Report();
        }

        progress?.Report(new ProgressSample(1));

        if (rejected > 0)
            Log.Warn(Loc.F("解压 {0} 时跳过了 {1} 个路径不合法的条目", archiveName, rejected));

        Log.Info(Loc.F("解压完成：{0}（格式 zip，文件 {1} 个）→ {2}", archiveName, finished, destinationRoot));
        return true;
    }

    /// <summary>按条目逐个解压到目标目录（7z / rar / tar 走这里）。单个条目失败只记 Warn，不影响整体。</summary>
    private static bool ExtractEntries(IEnumerable<IArchiveEntry> entries, string destinationRoot,
        string archiveName, string format, IProgress<ProgressSample>? progress, CancellationToken token)
    {
        // 目录条目不需要单独创建：解压文件时会按需建目录
        var files = entries.Where(entry => !entry.IsDirectory).ToList();
        var totalBytes = files.Sum(entry => Math.Max(0L, entry.Size));
        var finished = 0;
        var rejected = 0;
        var doneBytes = 0L;
        var lastReported = -1.0;

        void Report()
        {
            if (progress is null) return;

            var value = totalBytes > 0
                ? Math.Min(1, (double)doneBytes / totalBytes)
                : files.Count > 0 ? (double)finished / files.Count : 1;

            if (value - lastReported < ProgressStep) return;

            lastReported = value;
            progress.Report(new ProgressSample(value, doneBytes, totalBytes));
        }

        foreach (var entry in files)
        {
            token.ThrowIfCancellationRequested();

            var key = entry.Key ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!PathGuard.TryResolve(destinationRoot, key, out var targetPath))
            {
                rejected++;
                Log.Warn(Loc.F("压缩包条目路径不合法，已跳过：{0}（{1}）", key, archiveName));
                continue;
            }

            try
            {
                var directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                using var destination = File.Create(targetPath);

                try
                {
                    using var source = entry.OpenEntryStream();
                    CopyWithProgress(source, destination, entry.Size, chunk =>
                    {
                        doneBytes += chunk;
                        Report();
                    }, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (entry.Size <= 0)
                {
                    // Size = 0 的条目（空文件）在 SharpCompress 里「没有流」，OpenEntryStream 会抛
                    // 「File does not have a stream.」。空文件已经建出来了，不算失败——
                    // 以前这里直接当失败跳过，装出来的包会缺这些文件。
                    Log.Info(Loc.F("条目没有可读的流，按空文件处理：{0}（{1}）", key, ex.Message));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单个条目失败不影响整体
                Log.Warn(Loc.F("解压条目失败，已跳过：{0}（{1}）", key, ex.Message));
            }

            finished++;
            Report();
        }

        progress?.Report(new ProgressSample(1));

        if (rejected > 0)
            Log.Warn(Loc.F("解压 {0} 时跳过了 {1} 个路径不合法的条目", archiveName, rejected));

        Log.Info(Loc.F("解压完成：{0}（格式 {1}，文件 {2} 个）→ {3}", archiveName, format, finished, destinationRoot));
        return true;
    }

    /// <summary>单文件 gzip（非 tar）的解压。大小未知，进度只在收尾时置 1。</summary>
    private static bool ExtractSingleStream(Stream content, string? key, string destinationRoot,
        string archiveName, string format, IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var name = string.IsNullOrWhiteSpace(key) ? "gzip-content" : key;

        if (!PathGuard.TryResolve(destinationRoot, name, out var targetPath))
        {
            Log.Warn(Loc.F("压缩包条目路径不合法，已跳过：{0}（{1}）", name, archiveName));
            progress?.Report(new ProgressSample(1));
            return true;
        }

        try
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var destination = File.Create(targetPath);
            CopyWithProgress(content, destination, 0, _ => { }, token);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("解压条目失败，已跳过：{0}（{1}）", name, ex.Message));
        }

        progress?.Report(new ProgressSample(1));
        Log.Info(Loc.F("解压完成：{0}（格式 {1}，单文件 gzip）→ {2}", archiveName, format, destinationRoot));
        return true;
    }

    /// <summary>
    /// 分块搬运并回报每块字节数。expectedBytes 只用来把缓冲开得刚好够（小条目不白占 1MB），
    /// 传 0 表示大小未知。
    /// </summary>
    private static void CopyWithProgress(Stream source, Stream destination, long expectedBytes,
        Action<int> onChunk, CancellationToken token)
    {
        var size = expectedBytes > 0 && expectedBytes < BufferSize ? (int)expectedBytes : BufferSize;
        var buffer = new byte[size];

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();

            destination.Write(buffer, 0, read);
            onChunk(read);
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理临时文件失败：{0}（{1}）", file, ex.Message));
        }
    }

    /// <summary>按文件头嗅探格式；嗅探不出再退回扩展名判断。返回 null 表示不支持。</summary>
    private static string? DetectFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (File.Exists(path))
            {
                var head = new byte[8];
                int read;
                using (var stream = File.OpenRead(path))
                {
                    read = stream.Read(head, 0, head.Length);
                }

                if (read >= 4 && (StartsWith(head, read, ZipMagic) || StartsWith(head, read, ZipEmptyMagic)))
                    return "zip";
                if (read >= 4 && StartsWith(head, read, RarMagic)) return "rar";
                if (read >= 2 && StartsWith(head, read, GzipMagic)) return "gz";
                if (read >= 6 && StartsWith(head, read, SevenZipMagic)) return "7z";
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取压缩包文件头失败，改用扩展名判断：{0}（{1}）", path, ex.Message));
        }

        return ExtensionFormat(path);
    }

    private static string? ExtensionFormat(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal))
            return "tar.gz";

        return Path.GetExtension(name) switch
        {
            ".zip" => "zip",
            ".rar" => "rar",
            ".7z" => "7z",
            ".tar" => "tar",
            ".gz" => "gz",
            _ => null
        };
    }

    private static bool StartsWith(byte[] buffer, int length, byte[] magic)
    {
        if (length < magic.Length) return false;

        for (var i = 0; i < magic.Length; i++)
        {
            if (buffer[i] != magic[i]) return false;
        }

        return true;
    }
}
