using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.Backup;

/// <summary>
/// 备份 / 还原 / 原版备份。目录与命名规则按旧版：
/// <list type="bullet">
/// <item>原版备份固定放 <c>backup\MO</c>，名称不可改。</item>
/// <item>用户备份放 <c>backup\game\&lt;名称&gt;</c>，名称需通过校验（禁止 MO / mo / Mo / mO / 原版 等保留名）。</item>
/// <item>每个备份目录里可有 <c>backup_info.json</c> 元数据。</item>
/// </list>
/// 备份一律先写到临时目录、写完元数据后再整体搬到目标位置，避免半成品被当成可用备份；
/// 还原时先把目标目录现有内容搬进隔离区，失败或取消会搬回来。
/// </summary>
public static class BackupService
{
    /// <summary>原版游戏备份目录名（固定，用户不可重命名）。</summary>
    public const string OriginalBackupDirectoryName = "MO";

    /// <summary>用户备份目录根。</summary>
    public const string UserBackupRootName = "game";

    /// <summary>备份元数据文件名。</summary>
    public const string BackupInfoFileName = DirectoryCopier.BackupInfoFileName;

    /// <summary>不允许作为用户备份名称的保留名（大小写变体逐字列出，与旧版一致）。</summary>
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.Ordinal)
    {
        "MO", "mo", "Mo", "mO",
        "原版", "原版游戏", "原版游戏备份",
        "MO.mo.mO"
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 用户可能手工改过元数据，键名大小写不敏感
        PropertyNameCaseInsensitive = true,
        // 不转义中文，保证用户手工查看元数据时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>备份根目录。</summary>
    public static string BackupRoot => Paths.Backup;

    /// <summary>原版游戏备份目录：<c>backup\MO</c>。</summary>
    public static string OriginalBackupPath => Path.Combine(BackupRoot, OriginalBackupDirectoryName);

    /// <summary>用户备份根目录：<c>backup\game</c>。</summary>
    public static string UserBackupRoot => Path.Combine(BackupRoot, UserBackupRootName);

    /// <summary>指定名称的用户备份目录：<c>backup\game\&lt;名称&gt;</c>。</summary>
    public static string PathOf(string name)
        => Path.Combine(UserBackupRoot, PathGuard.SanitizeFileName(name).Trim().TrimEnd('.'));

    /// <summary>校验备份名称（对应旧版 is_valid_backup_name）。</summary>
    public static bool IsValidName(string? name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "备份名称不能为空";
            return false;
        }

        var trimmed = name.Trim();

        if (trimmed.Length == 0)
        {
            error = "备份名称不能仅包含空白字符";
            return false;
        }

        if (ForbiddenNames.Contains(trimmed))
        {
            error = $"备份名称「{trimmed}」为系统保留名称，禁止使用。\n" +
                    "保留名称包括：MO / mo / Mo / mO / MO.mo.mO / 原版 / 原版游戏 / 原版游戏备份";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Any(char.IsControl))
        {
            error = "备份名称包含非法字符，请避免使用：< > : \" / \\ | ? * 以及控制字符";
            return false;
        }

        if (trimmed.Length > 100)
        {
            error = "备份名称过长（最大 100 字符）";
            return false;
        }

        return true;
    }

    /// <summary>读取备份目录的元数据；不存在或损坏返回 null。</summary>
    public static BackupInfo? ReadInfo(string backupDirectory)
    {
        var file = Path.Combine(backupDirectory, BackupInfoFileName);
        if (!File.Exists(file)) return null;

        try
        {
            return JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(file), Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"备份元数据解析失败：{file}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>枚举所有可用备份：原版备份排在最前，随后是用户备份。</summary>
    public static IReadOnlyList<BackupEntry> List()
    {
        var result = new List<BackupEntry>();

        if (Directory.Exists(OriginalBackupPath))
            result.Add(BuildEntry(BackupKind.Original, "原版游戏", OriginalBackupPath));

        try
        {
            if (Directory.Exists(UserBackupRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(UserBackupRoot).OrderBy(path => path))
                    result.Add(BuildEntry(BackupKind.User, Path.GetFileName(directory), directory));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"枚举用户备份失败：{UserBackupRoot}", ex);
        }

        return result;
    }

    /// <summary>
    /// 备份一个游戏目录为用户备份（对应旧版 _backup_game）。
    /// overwrite 为 false 时，同名备份已存在直接失败，由调用方决定覆盖还是改名。
    /// </summary>
    public static BackupOutcome Backup(string sourceDirectory, string backupName,
        string sourceInstanceId = "", string sourceInstanceName = "",
        IReadOnlyDictionary<string, List<string>>? installedPackages = null, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (!IsValidName(backupName, out var error)) return Fail(error);

        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return Fail($"源目录无效，无法备份：{sourceDirectory}");

        var name = backupName.Trim();
        var target = PathOf(name);

        if (Directory.Exists(target) && !overwrite)
            return Fail($"已存在同名备份：{target}");

        var staging = Path.Combine(BackupRoot, $"staging_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

        try
        {
            Directory.CreateDirectory(staging);

            var summary = DirectoryCopier.Copy(sourceDirectory, staging, CopyConflictPolicy.Overwrite,
                null, progress, token);

            if (summary.Total == 0)
            {
                TryDeleteDirectory(staging);
                return Fail("实例中没有可备份的文件");
            }

            if (summary.AllFailed)
            {
                TryDeleteDirectory(staging);
                return Fail($"备份失败：所有 {summary.Total} 个文件均无法复制");
            }

            if (token.IsCancellationRequested)
            {
                TryDeleteDirectory(staging);
                return new BackupOutcome(false, true, "备份已取消", 0, 0, 0);
            }

            // 完整性校验：文件数不能为 0；体积差异超过容差只记警告（源目录可能正在变化）
            var sourceSize = DirectoryCopier.GetSize(sourceDirectory);
            var backupSize = DirectoryCopier.GetSize(staging);
            var fileCount = DirectoryCopier.CountFiles(staging);

            if (fileCount == 0)
            {
                TryDeleteDirectory(staging);
                return Fail("备份完整性校验失败：备份目录为空");
            }

            var tolerance = Math.Max(1024, sourceSize / 1000);
            if (sourceSize > 0 && Math.Abs(sourceSize - backupSize) > tolerance)
            {
                Log.Warn($"备份体积与源目录不一致：源 {sourceSize} 字节，备份 {backupSize} 字节");
            }

            WriteInfo(staging, new BackupInfo
            {
                Type = "user",
                Name = name,
                SourceInstance = sourceInstanceName,
                SourceInstanceId = sourceInstanceId,
                SourcePath = sourceDirectory,
                FileCount = summary.Total,
                FailedCount = summary.Failed,
                SourceSizeBytes = sourceSize,
                BackupSizeBytes = backupSize,
                InstalledPackages = installedPackages is null
                    ? new()
                    : installedPackages.ToDictionary(pair => pair.Key, pair => pair.Value.ToList())
            });

            Publish(staging, target);
            progress?.Report(1);

            var sizeText = FormatSize(backupSize);
            var message = $"已备份到：\n{target}\n\n共 {summary.Total} 个文件，共 {sizeText}" +
                          (summary.Failed > 0 ? $"\n（其中 {summary.Failed} 个文件复制失败）" : string.Empty);

            Log.Info($"备份完成：{name}，{summary.Total} 个文件，{sizeText} → {target}");

            return new BackupOutcome(true, false, message, summary.Total, summary.Failed, backupSize);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(staging);
            return new BackupOutcome(false, true, "备份已取消", 0, 0, 0);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(staging);
            Log.Error($"备份失败：{backupName}", ex);
            return Fail($"备份失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 备份原版游戏到 <c>backup\MO</c>（对应旧版 _backup_original_game）。
    /// overwrite 为 false 且已存在原版备份时直接失败。
    /// </summary>
    public static BackupOutcome BackupOriginal(string sourceDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return Fail($"源目录不存在：{sourceDirectory}");

        if (!GameLocator.IsMoDirectory(sourceDirectory))
        {
            return Fail("所选目录不是有效的心灵终结游戏目录：\n" + sourceDirectory +
                        "\n\n有效目录需满足：含 Mental_Omega 子目录，或目录/父目录内含 " +
                        "MentalOmegaClient.exe 或 \"Mental Omega.exe\"。");
        }

        if (Directory.Exists(OriginalBackupPath) && !overwrite)
            return Fail($"原版游戏备份已存在：{OriginalBackupPath}");

        var staging = Path.Combine(BackupRoot, "staging_original");
        TryDeleteDirectory(staging);

        try
        {
            Directory.CreateDirectory(staging);

            var summary = DirectoryCopier.Copy(sourceDirectory, staging, CopyConflictPolicy.Overwrite,
                journal: null, progress, token);

            if (summary.Total == 0)
            {
                TryDeleteDirectory(staging);
                return Fail("所选目录没有可备份的文件");
            }

            if (summary.AllFailed)
            {
                TryDeleteDirectory(staging);
                return Fail($"备份失败：所有 {summary.Total} 个文件均无法复制");
            }

            if (token.IsCancellationRequested)
            {
                TryDeleteDirectory(staging);
                return new BackupOutcome(false, true, "备份已取消", 0, 0, 0);
            }

            if (summary.Failed > 0)
                Log.Warn($"原版备份：有 {summary.Failed}/{summary.Total} 个文件复制失败");

            WriteInfo(staging, new BackupInfo
            {
                Type = "original",
                Name = "原版游戏",
                SourceInstanceId = "original",
                SourcePath = sourceDirectory,
                FileCount = summary.Total,
                FailedCount = summary.Failed
            });

            Publish(staging, OriginalBackupPath);
            progress?.Report(1);

            var message = $"原版游戏备份创建成功\n备份位置：{OriginalBackupPath}\n共 {summary.Total} 个文件" +
                          (summary.Failed > 0 ? $"\n（其中 {summary.Failed} 个文件复制失败）" : string.Empty);

            Log.Info($"原版备份完成：{summary.Total} 个文件 → {OriginalBackupPath}");

            return new BackupOutcome(true, false, message, summary.Total, summary.Failed,
                DirectoryCopier.GetSize(OriginalBackupPath));
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(staging);
            return new BackupOutcome(false, true, "备份已取消", 0, 0, 0);
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(staging);
            Log.Error("原版备份失败", ex);
            return Fail($"备份失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 用备份覆盖目标游戏目录（对应旧版 _restore_game / 全量卸载的恢复）。
    /// 目标目录现有内容先整体搬进隔离区，复制失败或取消会原样搬回来。
    /// </summary>
    public static BackupOutcome Restore(string backupDirectory, string targetDirectory,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory) || !Directory.Exists(backupDirectory))
            return Fail($"备份目录不存在：{backupDirectory}");

        if (string.IsNullOrWhiteSpace(targetDirectory))
            return Fail("目标目录为空");

        var target = PathGuard.NormalizeRoot(targetDirectory);
        if (target.Length == 0) return Fail($"目标目录无法解析：{targetDirectory}");

        var journal = new OperationJournal(Paths.Temp);

        try
        {
            Directory.CreateDirectory(target);

            foreach (var entry in Directory.GetFileSystemEntries(target))
            {
                token.ThrowIfCancellationRequested();

                if (journal.MoveToQuarantine(entry) is null)
                    throw new IOException($"无法移走目标目录现有内容：{entry}");
            }

            // 元数据文件不是游戏文件，不还原进游戏目录
            var summary = DirectoryCopier.Copy(backupDirectory, target, CopyConflictPolicy.Overwrite, journal,
                progress, token, excludeRelative: [BackupInfoFileName]);

            if (summary.Total == 0)
            {
                journal.Rollback();
                return Fail("备份内容为空，未恢复任何文件");
            }

            if (summary.AllFailed)
            {
                journal.Rollback();
                return Fail($"恢复失败：所有 {summary.Total} 个文件均无法复制");
            }

            if (token.IsCancellationRequested)
            {
                journal.Rollback();
                return new BackupOutcome(false, true, "恢复已取消，已回滚本次改动", 0, 0, 0);
            }

            journal.Commit();
            progress?.Report(1);

            var warning = GameLocator.IsMoDirectory(target)
                ? string.Empty
                : "\n\n⚠️ 恢复后未能识别为有效的心灵终结游戏目录，请手动检查游戏文件是否完整。";

            Log.Info($"恢复完成：{backupDirectory} → {target}，{summary.Total} 个文件，失败 {summary.Failed}");

            return new BackupOutcome(summary.Failed == 0, false,
                $"恢复成功\n共 {summary.Total} 个文件" +
                (summary.Failed > 0 ? $"\n（其中 {summary.Failed} 个文件复制失败）" : string.Empty) + warning,
                summary.Total, summary.Failed, summary.BytesCopied);
        }
        catch (OperationCanceledException)
        {
            journal.Rollback();
            return new BackupOutcome(false, true, "恢复已取消，已回滚本次改动", 0, 0, 0);
        }
        catch (Exception ex)
        {
            journal.Rollback();
            Log.Error($"恢复失败：{backupDirectory} → {target}", ex);
            return Fail($"恢复失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 抽样校验备份完整性（对应旧版 _verify_backup_integrity）：
    /// 随机抽最多 sampleSize 个文件逐个流式算 SHA256，读得通才算通过。
    /// </summary>
    public static BackupVerifyResult Verify(string backupDirectory, int sampleSize = 20,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory) || !Directory.Exists(backupDirectory))
            return new BackupVerifyResult(0, 0, 0, "备份目录不存在");

        var files = DirectoryCopier.ListFiles(backupDirectory)
            .Where(relative => !string.Equals(relative, BackupInfoFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0) return new BackupVerifyResult(0, 0, 0, "备份目录为空");

        var sample = files.Count <= sampleSize
            ? files
            : files.OrderBy(_ => Random.Shared.Next()).Take(sampleSize).ToList();

        var verified = 0;
        var missing = 0;
        var mismatched = 0;

        foreach (var relative in sample)
        {
            token.ThrowIfCancellationRequested();

            var full = Path.Combine(backupDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full))
            {
                missing++;
                continue;
            }

            try
            {
                if (ComputeHash(full).Length > 0) verified++;
                else mismatched++;
            }
            catch (Exception ex)
            {
                mismatched++;
                Log.Warn($"备份校验读取失败：{full}（{ex.Message}）");
            }
        }

        return new BackupVerifyResult(verified, missing, mismatched, string.Empty);
    }

    /// <summary>人类可读的体积文字（对应旧版 _format_size）。</summary>
    public static string FormatSize(long bytes)
    {
        var value = (double)bytes;

        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (value < 1024.0) return unit == "B" ? $"{(long)value} B" : $"{value:F1} {unit}";

            value /= 1024.0;
        }

        return $"{value:F1} PB";
    }

    // ————— 内部 —————

    private static BackupEntry BuildEntry(BackupKind kind, string name, string directory)
    {
        var info = ReadInfo(directory);
        var fileCount = DirectoryCopier.CountFiles(directory);
        var size = DirectoryCopier.GetSize(directory, excludeBackupInfo: true);

        DateTime? created = info?.CreatedAt;

        if (created is null)
        {
            try
            {
                created = Directory.GetLastWriteTime(directory);
            }
            catch
            {
                created = null;
            }
        }

        return new BackupEntry(kind, name, directory, created, fileCount, size,
            info?.SourceInstance ?? string.Empty);
    }

    /// <summary>先写临时目录，写完元数据再整体搬到目标位置；同名旧备份先删掉（与旧版一致）。</summary>
    private static void Publish(string staging, string target)
    {
        // Directory.Move 要求目标父目录已存在
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? BackupRoot);

        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);

        Directory.Move(staging, target);
    }

    private static void WriteInfo(string directory, BackupInfo info)
    {
        try
        {
            info.CreatedAt = DateTime.Now;
            File.WriteAllText(Path.Combine(directory, BackupInfoFileName),
                JsonSerializer.Serialize(info, Options));
        }
        catch (Exception ex)
        {
            Log.Error($"写入备份元数据失败：{directory}", ex);
        }
    }

    private static string ComputeHash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时目录失败：{directory}（{ex.Message}）");
        }
    }

    private static BackupOutcome Fail(string message)
    {
        Log.Warn($"备份操作失败：{message}");
        return new BackupOutcome(false, false, message, 0, 0, 0);
    }
}
