using System.IO;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Backup;
using HMOL.Core.Games;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Packages;
using HMOL.Core.Localization;

namespace HMOL.Core.Instances;

/// <summary>
/// 实例导出包里的 <c>instance_info.json</c>。
/// 键名沿用旧版（snake_case），这样旧版导出的实例包也能直接导入。
/// </summary>
public sealed class InstanceArchiveInfo
{
    [JsonPropertyName("format")] public string Format { get; set; } = InstanceArchive.FormatName;

    [JsonPropertyName("format_version")] public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("original_path")] public string OriginalPath { get; set; } = string.Empty;

    [JsonPropertyName("export_date")] public DateTime ExportDate { get; set; } = DateTime.Now;

    [JsonPropertyName("created_time")] public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonPropertyName("instance_id")] public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("game_files_count")] public int GameFilesCount { get; set; }

    [JsonPropertyName("total_size_bytes")] public long TotalSizeBytes { get; set; }

    /// <summary>导出时该实例已安装的包。旧版有七类键，读取时会并入 plugin。</summary>
    [JsonPropertyName("installed_packages")] public Dictionary<string, List<string>> InstalledPackages { get; set; } = [];

    /// <summary>游戏类型。旧版导出的包没有这个键，按心灵终结处理。</summary>
    [JsonPropertyName("game_kind")] public GameKind Kind { get; set; } = GameKind.MentalOmega;

    /// <summary>用户指定的主程序（游戏目录内时是相对路径）。旧版包没有这个键，按自动探测处理。</summary>
    [JsonPropertyName("executable")] public string Executable { get; set; } = string.Empty;
}

/// <summary>
/// 实例的导出 / 导入。包结构沿用旧版：根下 <c>instance_info.json</c> + <c>game_files\</c>（游戏目录内容）。
/// 导入时按包里的游戏类型校验 <c>game_files</c> 是有效的游戏目录，并把游戏文件复制到实例数据目录。
/// </summary>
public static class InstanceArchive
{
    public const string FormatName = "hmol-instance";

    public const string InfoFileName = "instance_info.json";

    public const string GameFilesDirectoryName = "game_files";

    /// <summary>读写缓冲（导出打包与导入搬移共用）。</summary>
    private const int CopyBufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 用户可能手工改过导出信息，键名大小写不敏感
        PropertyNameCaseInsensitive = true,
        // 不转义中文，保证用户手工查看配置时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 导出实例：游戏目录内容整体打包（对应旧版 export_instance）。
    /// 支持 .zip（.NET 自带压缩，流式写入）与 .7z（借随包分发的 runtime\7zip\7za.exe）。
    /// .rar **不支持导出**：RAR 是专有格式，没有可用的写入实现。
    /// </summary>
    public static InstanceResult Export(GameInstance instance, string exportPath,
        CompressionLevel level = CompressionLevel.Optimal, IProgress<ProgressSample>? progress = null,
        CancellationToken token = default)
    {
        if (instance is null) return new InstanceResult(false, Loc.T("实例不存在"));

        if (string.IsNullOrWhiteSpace(exportPath)) return new InstanceResult(false, Loc.T("导出路径不能为空"));

        if (!Directory.Exists(instance.GameDir))
            return new InstanceResult(false, Loc.F("游戏路径不存在：{0}", instance.GameDir));

        var target = Path.GetFullPath(exportPath);
        var targetDirectory = Path.GetDirectoryName(target);

        if (string.IsNullOrEmpty(targetDirectory) || !Directory.Exists(targetDirectory))
            return new InstanceResult(false, Loc.F("导出目录不存在：{0}", targetDirectory));

        if (File.Exists(target))
            return new InstanceResult(false, Loc.F("目标文件已存在，请先删除或更换名称：{0}", target));

        if (!ArchiveFormats.IsExportable(target))
        {
            return new InstanceResult(false, string.Equals(Path.GetExtension(target), ".rar", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("无法导出为 rar：RAR 是专有格式，没有可用的写入实现（WinRAR 的商业组件不能随包分发）。\n") +
                  Loc.F("请改用 {0} 导出。", ArchiveFormats.ExportDisplay)
                : Loc.F("不支持的导出格式，请使用 {0}。", ArchiveFormats.ExportDisplay));
        }

        var partial = target + ".partial";

        try
        {
            var files = CollectFiles(instance.GameDir);
            if (files.Count == 0) return new InstanceResult(false, Loc.T("实例中没有文件可导出"));

            var totalSize = files.Sum(file => file.Size);

            var info = new InstanceArchiveInfo
            {
                Name = instance.Name,
                OriginalPath = instance.GameDir,
                CreatedAt = instance.CreatedAt,
                InstanceId = instance.Id,
                GameFilesCount = files.Count,
                TotalSizeBytes = totalSize,
                InstalledPackages = instance.InstalledPackages.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
                Kind = instance.Kind,
                Executable = instance.Executable
            };

            if (File.Exists(partial)) File.Delete(partial);

            if (IsSevenZip(target))
            {
                var sevenZipError = WriteSevenZip(instance, partial, info, level, progress, token);

                if (sevenZipError is not null) return new InstanceResult(false, sevenZipError);
            }
            else
            {
                using var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                var infoEntry = archive.CreateEntry(InfoFileName, CompressionLevel.Optimal);
                using (var entryStream = infoEntry.Open())
                using (var writer = new StreamWriter(entryStream, System.Text.Encoding.UTF8))
                {
                    writer.Write(JsonSerializer.Serialize(info, Options));
                }

                var finished = 0;
                var doneBytes = 0L;
                var lastReported = -1.0;
                var buffer = new byte[CopyBufferSize];

                // 按字节报进度：几个 GB 的实例里单个大文件很多，按文件报会让进度条长时间不动
                void Report()
                {
                    if (progress is null) return;

                    var value = totalSize > 0
                        ? Math.Min(1, (double)doneBytes / totalSize)
                        : (double)finished / files.Count;

                    if (value - lastReported < 0.005) return;

                    lastReported = value;
                    progress.Report(new ProgressSample(value, doneBytes, totalSize));
                }

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();

                    var entry = archive.CreateEntry($"{GameFilesDirectoryName}/{file.Relative}", level);

                    try
                    {
                        using var source = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read,
                            CopyBufferSize, FileOptions.SequentialScan);
                        using var destination = entry.Open();

                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();

                            destination.Write(buffer, 0, read);
                            doneBytes += read;
                            Report();
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个文件读不了就跳过，尽量把能导的都导出去（与旧版一致）
                        Log.Warn(Loc.F("导出时跳过无法读取的文件：{0}（{1}）", file.Full, ex.Message));
                        continue;
                    }

                    finished++;
                    Report();
                }
            }

            File.Move(partial, target, overwrite: false);
            progress?.Report(new ProgressSample(1));

            var sizeText = BackupService.FormatSize(new FileInfo(target).Length);

            return new InstanceResult(true,
                Loc.F("实例「{0}」已导出到：\n{1}\n\n", instance.Name, target) +
                Loc.F("源文件：{0} 个（{1}）\n压缩包：{2}", files.Count, BackupService.FormatSize(totalSize), sizeText));
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partial);
            return new InstanceResult(false, Loc.T("导出已取消"));
        }
        catch (Exception ex)
        {
            TryDeleteFile(partial);
            Log.Error(Loc.F("导出实例失败：{0}", instance.Name), ex);
            return new InstanceResult(false, Loc.F("导出失败：{0}", ex.Message));
        }
    }

    /// <summary>目标扩展名是不是 7z（导出只有 .zip 与 .7z 两种）。</summary>
    private static bool IsSevenZip(string target)
        => string.Equals(Path.GetExtension(target), ".7z", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用 7-Zip 写出 7z 包。归档需要保留 <c>game_files</c> 这层结构，而游戏目录可能有好几个 GB，
    /// 不能为了打包先复制一份；因此在暂存目录里建一个指向真实游戏目录的目录联接，让 7-Zip 直接读原目录。
    /// 返回 null 表示成功，否则返回可直接展示给用户的中文原因。
    /// </summary>
    private static string? WriteSevenZip(GameInstance instance, string archivePath, InstanceArchiveInfo info,
        CompressionLevel level, IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var staging = Path.Combine(Paths.Temp, $"export7z_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        var content = Path.Combine(staging, "content");
        var linkPath = Path.Combine(content, GameFilesDirectoryName);

        try
        {
            Directory.CreateDirectory(content);

            File.WriteAllText(Path.Combine(content, InfoFileName), JsonSerializer.Serialize(info, Options));

            if (!Junction.TryCreate(linkPath, instance.GameDir, out var junctionError))
            {
                return Loc.F("无法为游戏目录建立目录联接（{0}）。\n", junctionError) +
                       Loc.T("目录联接需要 NTFS 分区，请改用 .zip 导出。");
            }

            var (ok, error) = SevenZipTool.Create(content, archivePath, ToSevenZipLevel(level),
                progress is null ? null : new PercentProgress(new ProgressSpan(progress, 0.03, 0.98)), token);

            if (ok) return null;

            TryDeleteFile(archivePath);

            return Loc.F("导出失败：{0}", error);
        }
        finally
        {
            // 先摘除联接本身（rmdir 只摘链接，绝不会递归进游戏目录），再清暂存目录
            Junction.Remove(linkPath);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>把 .NET 的压缩级别折算成 7-Zip 的 -mx 等级。</summary>
    private static int ToSevenZipLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => SevenZipTool.LevelStore,
        CompressionLevel.Fastest => SevenZipTool.LevelFastest,
        CompressionLevel.SmallestSize => SevenZipTool.LevelUltra,
        _ => SevenZipTool.LevelNormal
    };

    /// <summary>
    /// 导入实例（对应旧版 import_instance）：解压 → 校验 → 复制游戏文件到实例数据目录 → 建立实例。
    /// 返回新建实例的 Id（失败时为 null）。只接受 <see cref="ArchiveFormats.Extensions"/> 里列出的格式。
    /// </summary>
    public static (InstanceResult Result, string? InstanceId) Import(string archivePath,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return (new InstanceResult(false, Loc.T("文件不存在")), null);

        if (!ArchiveFormats.IsSupported(archivePath))
            return (new InstanceResult(false,
                Loc.F("只支持 {0} 格式的实例包，当前文件是 {1}", ArchiveFormats.Display, Path.GetFileName(archivePath))), null);

        // id 先定下来：暂存目录要落在「实例目录所在分区」上。同盘才有机会直接改名搬过去，
        // 否则 GB 级内容得先写到系统盘再复制过来，既慢又容易把系统盘写满。
        var id = GameInstance.NewId();
        var instanceDirectory = Paths.InstanceDir(id);
        var scratch = Paths.ScratchFor(instanceDirectory);
        var tempDirectory = Path.Combine(scratch, $"import_{DateTime.Now:yyyyMMdd_HHmmss_fff}");

        try
        {
            Directory.CreateDirectory(tempDirectory);

            if (!ArchiveExtractor.TryExtract(archivePath, tempDirectory, out var error,
                    progress is null ? null : new ProgressSpan(progress, 0.05, 0.3), token))
            {
                return (new InstanceResult(false, Loc.F("导入失败：{0}", error)), null);
            }

            var infoPath = Path.Combine(tempDirectory, InfoFileName);
            if (!File.Exists(infoPath))
                return (new InstanceResult(false, Loc.T("无效的实例文件：缺少配置文件")), null);

            var info = JsonSerializer.Deserialize<InstanceArchiveInfo>(File.ReadAllText(infoPath), Options);
            if (info is null) return (new InstanceResult(false, Loc.T("配置文件格式错误")), null);

            var gameFiles = Path.Combine(tempDirectory, GameFilesDirectoryName);
            if (!Directory.Exists(gameFiles))
                return (new InstanceResult(false, Loc.T("无法找到游戏文件目录（game_files）")), null);

            if (!GameLocator.IsGameDirectory(gameFiles, info.Kind, info.Executable))
                return (new InstanceResult(false, Loc.T("解压出来的文件不是有效的游戏目录")), null);

            var name = ResolveName(string.IsNullOrWhiteSpace(info.Name) ? Loc.T("导入的实例") : info.Name);

            var summary = MoveIntoPlace(gameFiles, instanceDirectory, progress, token);

            if (summary.Total == 0 || summary.AllFailed)
            {
                TryDeleteDirectory(instanceDirectory);
                return (new InstanceResult(false, summary.Total == 0
                    ? Loc.T("游戏文件为空，导入失败")
                    : Loc.F("复制游戏文件失败：所有 {0} 个文件均无法复制", summary.Total)), null);
            }

            if (token.IsCancellationRequested)
            {
                TryDeleteDirectory(instanceDirectory);
                return (new InstanceResult(false, Loc.T("导入已取消")), null);
            }

            var instance = new GameInstance
            {
                Id = id,
                Name = name,
                GameDir = instanceDirectory,
                CreatedAt = info.CreatedAt == default ? DateTime.Now : info.CreatedAt,
                Kind = info.Kind,
                // 游戏文件已复制到新实例目录，相对主程序名照旧可用；绝对路径在导入后失效时会退回自动探测
                Executable = info.Executable
            };

            instance.InstalledPackages = info.InstalledPackages;
            instance.MigrateLegacyPackages();

            InstanceManager.EnsureDirectories(instance);
            InstanceStore.Upsert(instance);
            progress?.Report(new ProgressSample(1));

            Log.Info(Loc.F("实例「{0}」导入成功，共 {1} 个文件 → {2}", name, summary.Total, instanceDirectory));

            return (new InstanceResult(true,
                Loc.F("实例「{0}」导入成功", name) +
                (summary.Failed > 0 ? Loc.F("\n（其中 {0} 个文件复制失败）", summary.Failed) : string.Empty)), id);
        }
        catch (OperationCanceledException)
        {
            return (new InstanceResult(false, Loc.T("导入已取消")), null);
        }
        catch (JsonException)
        {
            return (new InstanceResult(false, Loc.T("配置文件格式错误")), null);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导入实例失败：{0}", archivePath), ex);
            return (new InstanceResult(false, Loc.F("导入失败：{0}", ex.Message)), null);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
            Paths.CleanupScratch(scratch);
        }
    }

    /// <summary>
    /// 把解压出来的游戏文件搬进实例目录。暂存目录与实例目录同分区时直接
    /// <see cref="Directory.Move"/>（改名，瞬间完成）；跨分区才退化为带进度的复制。
    /// </summary>
    private static CopySummary MoveIntoPlace(string source, string target, IProgress<ProgressSample>? progress,
        CancellationToken token)
    {
        if (PathGuard.SameVolume(source, target) && !Directory.Exists(target))
        {
            try
            {
                Directory.Move(source, target);
                progress?.Report(new ProgressSample(1));

                return new CopySummary(DirectoryCopier.CountFiles(target), 0, 0,
                    DirectoryCopier.GetSize(target), []);
            }
            catch (IOException ex)
            {
                // 目标卷不支持目录改名等情况：退回逐文件复制
                Log.Warn(Loc.F("同分区搬移失败，退回复制：{0} → {1}（{2}）", source, target, ex.Message));
            }
        }

        Directory.CreateDirectory(target);

        return DirectoryCopier.Copy(source, target, CopyConflictPolicy.Overwrite, null,
            progress is null ? null : new ProgressSpan(progress, 0.3, 0.9), token, consumeSource: true);
    }

    /// <summary>枚举游戏目录下的文件（缓存大小，避免后续多次 stat）。</summary>
    private static List<(string Full, string Relative, long Size)> CollectFiles(string directory)
    {
        var result = new List<(string, string, long)>();
        var root = Path.GetFullPath(directory);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                result.Add((file, Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length));
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("跳过无法读取的文件：{0}（{1}）", file, ex.Message));
            }
        }

        return result;
    }

    /// <summary>同名实例改名为「原名 (n)」（对应旧版 _handle_name_collision）。</summary>
    private static string ResolveName(string originalName)
    {
        var name = originalName;
        var counter = 1;

        while (InstanceStore.All.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{originalName} ({counter++})";

            if (counter <= 100) continue;

            name = $"{originalName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            break;
        }

        return name;
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理文件失败：{0}（{1}）", file, ex.Message));
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
            Log.Warn(Loc.F("清理目录失败：{0}（{1}）", directory, ex.Message));
        }
    }
}
