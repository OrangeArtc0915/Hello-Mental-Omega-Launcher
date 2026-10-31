using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Packages;

/// <summary>解压结果：成功 / 需要密码 / 密码错误 / 其它失败。</summary>
public enum ExtractStatus
{
    Success = 0,
    PasswordRequired = 1,
    WrongPassword = 2,
    Failed = 3
}

/// <summary>一次解压的结果。<see cref="Error"/> 是给用户看的原因（失败时）。</summary>
public sealed record ExtractResult(ExtractStatus Status, string? Error = null)
{
    public bool Success => Status == ExtractStatus.Success;

    /// <summary>需要用户输入 / 重新输入密码。</summary>
    public bool NeedsPassword => Status is ExtractStatus.PasswordRequired or ExtractStatus.WrongPassword;

    public static readonly ExtractResult Ok = new(ExtractStatus.Success);

    public static ExtractResult Password(string error) => new(ExtractStatus.PasswordRequired, error);

    public static ExtractResult WrongPassword(string error) => new(ExtractStatus.WrongPassword, error);

    public static ExtractResult Fail(string error) => new(ExtractStatus.Failed, error);
}

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

    /// <summary>解压到目标目录（自动识别 zip / rar / 7z / tar / gz）；加密压缩包一律按失败处理。</summary>
    public static bool TryExtract(string archivePath, string destinationDirectory, out string? error,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        var result = Extract(archivePath, destinationDirectory, progress, token);
        error = result.Error;
        return result.Success;
    }

    /// <summary>
    /// 解压到目标目录（自动识别 zip / rar / 7z / tar / gz）。
    ///
    /// <para>
    /// <paramref name="password"/> 供加密压缩包使用：没给密码但压缩包确实加密时返回
    /// <see cref="ExtractStatus.PasswordRequired"/>；给了密码却解不开返回
    /// <see cref="ExtractStatus.WrongPassword"/>。调用方据此提示用户输入或重新输入。
    /// </para>
    /// </summary>
    public static ExtractResult Extract(string archivePath, string destinationDirectory,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default, string? password = null)
    {
        var hasPassword = !string.IsNullOrEmpty(password);

        try
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                return ExtractResult.Fail(Loc.F("压缩包不存在：{0}", archivePath));

            if (string.IsNullOrWhiteSpace(destinationDirectory))
                return ExtractResult.Fail(Loc.T("目标目录为空"));

            var format = DetectFormat(archivePath);
            if (format is null)
                return ExtractResult.Fail(Loc.F("不支持的压缩格式：{0}", Path.GetFileName(archivePath)));

            var destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);

            token.ThrowIfCancellationRequested();

            var archiveName = Path.GetFileName(archivePath);
            var encrypted = IsEncrypted(archivePath, format);

            if (encrypted && !hasPassword)
                return ExtractResult.Password(Loc.T("这个压缩包有密码，需要输入解压密码。"));

            // zip 走内置实现：更快，而且能直接拿到每个条目的大小，进度可以按字节走。
            // 加密的 zip 内置实现解不了（会当成数据损坏），只能交给 SharpCompress。
            if (format == "zip")
            {
                if (!encrypted)
                    return ExtractZip(archivePath, destinationRoot, archiveName, progress, token)
                        ? ExtractResult.Ok
                        : ExtractResult.Fail(Loc.T("解压失败"));

                return ExtractEncryptedZip(archivePath, destinationRoot, archiveName, password!, progress, token);
            }

            // 7z 交给随包的原生 7-Zip：SharpCompress 的托管实现解 GB 级固实包要几分钟，
            // 还会把空文件当「没有流」整批跳过（装出来的包残缺）。
            // 注意 7za.exe 是 Standalone 版，不支持 rar，所以 rar 只能继续走 SharpCompress。
            if (format == "7z" && SevenZipTool.IsAvailable)
                return ExtractNative(archivePath, destinationRoot, archiveName, progress, token, password, encrypted);

            if (format == "7z")
            {
                // 回退到 SharpCompress 会慢一个数量级，日志里明说，别让人对着进度条猜
                Log.Warn(Loc.F("{0}；本次 7z 解压改用慢速实现，耗时会长很多", SevenZipTool.MissingMessage));
            }

            using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = password });

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
                            return ToResult(ExtractEntries(tar.Entries, destinationRoot, archiveName, format,
                                progress, token, hasPassword));
                        }

                        decompressed.Position = 0;
                        return ExtractSingleStream(decompressed, nested.Key, destinationRoot, archiveName, format,
                            progress, token)
                            ? ExtractResult.Ok
                            : ExtractResult.Fail(Loc.T("解压失败"));
                    }
                    finally
                    {
                        TryDeleteFile(temp);
                    }
                }
            }

            return ToResult(ExtractEntries(archive.Entries, destinationRoot, archiveName, format, progress, token,
                hasPassword));
        }
        catch (OperationCanceledException)
        {
            return ExtractResult.Fail(Loc.T("操作已取消"));
        }
        catch (Exception ex) when (IsPasswordException(ex))
        {
            return hasPassword
                ? ExtractResult.WrongPassword(Loc.T("密码错误，请重新输入。"))
                : ExtractResult.Password(Loc.T("这个压缩包有密码，需要输入解压密码。"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("解压失败：{0}", archivePath), ex);
            return ExtractResult.Fail(ex.Message);
        }
    }

    private static ExtractResult ToResult((bool Ok, bool WrongPassword) outcome)
        => outcome.Ok
            ? ExtractResult.Ok
            : outcome.WrongPassword
                ? ExtractResult.WrongPassword(Loc.T("密码错误，请重新输入。"))
                : ExtractResult.Fail(Loc.T("解压失败"));

    /// <summary>
    /// 压缩包是否加密。zip / 7z / rar 都让 SharpCompress 读条目元数据判断
    /// （zip 只看第一个本地文件头会漏掉「首项是未加密目录、后面才是加密文件」的包）；
    /// SharpCompress 读不出来时，zip 再退回看文件头标志位。
    /// </summary>
    private static bool IsEncrypted(string archivePath, string format)
    {
        if (format is not ("zip" or "7z" or "rar")) return false;

        try
        {
            using var archive = ArchiveFactory.Open(archivePath);
            return archive.Entries.Any(entry => entry.IsEncrypted);
        }
        catch (Exception ex) when (IsPasswordException(ex))
        {
            // 连条目表都加密了（7z 的 -mhe），没密码根本读不出来
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("检测压缩包是否加密失败：{0}（{1}）", Path.GetFileName(archivePath), ex.Message));
            return format == "zip" && ZipHeaderEncrypted(archivePath);
        }
    }

    /// <summary>SharpCompress 与 .NET 各有一个 CryptographicException，密码不对时两种都可能抛。</summary>
    private static bool IsPasswordException(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException;

    /// <summary>zip 的第一个本地文件头里的通用标志位 bit0 表示「条目已加密」。</summary>
    private static bool ZipHeaderEncrypted(string archivePath)
    {
        try
        {
            using var stream = File.OpenRead(archivePath);
            Span<byte> head = stackalloc byte[8];

            if (stream.Read(head) < head.Length) return false;
            if (head[0] != 0x50 || head[1] != 0x4B || head[2] != 0x03 || head[3] != 0x04) return false;

            var flags = head[6] | (head[7] << 8);
            return (flags & 0x0001) != 0;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取 zip 文件头失败：{0}（{1}）", Path.GetFileName(archivePath), ex.Message));
            return false;
        }
    }

    /// <summary>加密 zip：SharpCompress 带密码解压。密码错时条目流会抛加密异常，统一报「密码错误」。</summary>
    private static ExtractResult ExtractEncryptedZip(string archivePath, string destinationRoot, string archiveName,
        string password, IProgress<ProgressSample>? progress, CancellationToken token)
    {
        using var archive = SharpCompress.Archives.Zip.ZipArchive.Open(archivePath,
            new ReaderOptions { Password = password });

        return ToResult(ExtractEntries(archive.Entries, destinationRoot, archiveName, "zip", progress, token,
            passwordSupplied: true));
    }

    /// <summary>
    /// 解压后若只有一层顶层目录，就把该目录当作内容根（对应旧版安装时的「智能识别」）。
    ///
    /// <para>
    /// 层层套娃的情况也要剥到底：很多人是「右键压缩整个文件夹」，于是包里会多出
    /// <c>Apra1\Apra2\实际文件…</c> 这样的两层壳；只剥一层的话 <c>Apra2</c> 会被整个铺进游戏根目录，
    /// 游戏反而找不到文件。这里只要当前目录里「唯一一项且是目录」就继续往下走，
    /// 直到里面有多项、或只剩文件为止。
    /// </para>
    ///
    /// <para>
    /// 但遇到游戏自己的内容目录名（<c>MapsMO</c> / <c>INI</c> / <c>Resources</c> 等）就停手：
    /// 那是包内容的一部分，剥掉会把地图、INI 铺错位置。最多剥 <see cref="MaxUnwrapDepth"/> 层，防目录成环。
    /// </para>
    /// </summary>
    public static string ResolveSingleTopDirectory(string extractDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(extractDirectory) || !Directory.Exists(extractDirectory))
                return extractDirectory;

            var current = extractDirectory;

            for (var depth = 0; depth < MaxUnwrapDepth; depth++)
            {
                var entries = Directory.GetFileSystemEntries(current);
                if (entries.Length != 1) break;

                var only = entries[0];
                if (!Directory.Exists(only)) break;

                var name = Path.GetFileName(only);
                if (ContentFolderNames.Contains(name)) break;

                Log.Info(Loc.F("识别到外层包装目录，取下一层作为内容根：{0}", name));
                current = only;
            }

            return current;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("识别解压根目录失败：{0}（{1}）", extractDirectory, ex.Message));
            return extractDirectory;
        }
    }

    /// <summary>最多剥几层外包装目录，防止异常目录结构导致死循环。</summary>
    private const int MaxUnwrapDepth = 8;

    /// <summary>
    /// 游戏根目录自带的内容目录名：遇到它们就不再往外剥，否则地图 / INI 会被铺到错误的位置。
    /// 比较不区分大小写。
    /// </summary>
    private static readonly HashSet<string> ContentFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "MapsMO", "Maps", "INI", "Resources", "Data", "Audio", "Art", "Shaders",
        "Binaries", "Movies", "Taunts", "Theater"
    };

    /// <summary>
    /// 用随包的 7-Zip 原生程序解压。它只报百分比，所以先用元数据读出未压缩总字节数，
    /// 换算成字节后上层照样能显示速度与剩余时间。
    /// 加密包用 <c>-p</c> 传密码；给了密码还失败时按「密码错误」上报。
    /// </summary>
    private static ExtractResult ExtractNative(string archivePath, string destinationRoot, string archiveName,
        IProgress<ProgressSample>? progress, CancellationToken token, string? password, bool encrypted)
    {
        var totalBytes = UncompressedSizeOf(archivePath);

        var (ok, error) = SevenZipTool.Extract(archivePath, destinationRoot,
            progress is null ? null : new PercentProgress(progress, totalBytes), token, password);

        if (!ok)
        {
            if (encrypted && !string.IsNullOrEmpty(password))
                return ExtractResult.WrongPassword(Loc.T("密码错误，请重新输入。"));

            return ExtractResult.Fail(Loc.F("解压失败：{0}", error));
        }

        progress?.Report(new ProgressSample(1, totalBytes, totalBytes));

        Log.Info(Loc.F("解压完成：{0}（7-Zip 原生", archiveName) +
                 (totalBytes > 0 ? $"，{totalBytes / 1024d / 1024d:F1} MB" : string.Empty) +
                 $"）→ {destinationRoot}");

        return ExtractResult.Ok;
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

    /// <summary>
    /// 按条目逐个解压到目标目录（7z / rar / tar / 加密 zip 走这里）。单个条目失败只记 Warn，不影响整体。
    /// <paramref name="passwordSupplied"/> 为 true 时，第一个失败的条目就当作密码错误中止——
    /// 密码错了每个条目都会解不开，没必要把整个包跑完再报错。
    /// </summary>
    private static (bool Ok, bool WrongPassword) ExtractEntries(IEnumerable<IArchiveEntry> entries,
        string destinationRoot, string archiveName, string format, IProgress<ProgressSample>? progress,
        CancellationToken token, bool passwordSupplied = false)
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
                // 有密码还解不开：基本就是密码错了，直接中止让上层重新问
                if (passwordSupplied)
                {
                    Log.Warn(Loc.F("加密压缩包解压失败（按密码错误处理）：{0}（{1}）", key, ex.Message));
                    return (false, true);
                }

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
        return (true, false);
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
