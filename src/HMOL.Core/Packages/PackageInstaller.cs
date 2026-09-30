using System.IO;
using HMOL.Core.App;
using HMOL.Core.Backup;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.Packages;

/// <summary>安装目标描述：源在哪、要不要解压、落到哪。</summary>
/// <param name="SourcePath">包管理器目录里的源文件/目录。</param>
/// <param name="IsArchive">源是不是压缩包（按扩展名判断，与旧版一致）。</param>
/// <param name="TargetDirectory">复制落点目录。</param>
/// <param name="MergeIntoRoot">是否直接合并进游戏根目录（这类目标绝不整体替换目录）。</param>
public sealed record PackageTarget(string SourcePath, bool IsArchive, string TargetDirectory, bool MergeIntoRoot);

/// <summary>安装结果。</summary>
public sealed record InstallOutcome(bool Success, bool Cancelled, bool RolledBack,
    int TotalFiles, int FailedFiles, int SkippedFiles, string Message);

/// <summary>卸载结果。</summary>
public sealed record UninstallOutcome(bool Success, bool Cancelled, bool RolledBack,
    int DeletedFiles, int DeleteFailures, int RestoredFiles, int RestoreFailures,
    int CleanedDirectories, string Message);

/// <summary>
/// 插件包的安装与卸载。规则按旧版 <c>_install_package_impl</c> / <c>_uninstall_package*</c>：
/// <list type="bullet">
/// <item>压缩包先解压到临时目录；解压后只有一个顶层目录时以该目录为内容根（旧版「智能识别」）。</item>
/// <item>单个 .map 文件装到 <c>Maps\Custom</c>；压缩包（含地图压缩包）解压后合并到游戏根目录；目录落到 <c>游戏目录\包名</c>；其它单文件落到 <c>游戏目录\同名文件</c>。</item>
/// <item>覆盖已有文件前留下 <c>.bak-&lt;时间戳&gt;</c> 备份。</item>
/// <item>全程登记痕迹，失败或取消时回滚本次已做的部分（旧版只有「全部失败就放弃」）。</item>
/// </list>
/// </summary>
public static class PackageInstaller
{
    /// <summary>包管理器里某个包的源路径。</summary>
    public static string SourcePathOf(PackageType type, string packageName)
        => Path.Combine(PackageTypes.DirectoryOf(type), packageName);

    /// <summary>描述安装目标（对应旧版 16504-16507 与 16684-16691）。</summary>
    public static PackageTarget Describe(GameInstance instance, PackageType type, string packageName)
    {
        var source = SourcePathOf(type, packageName);
        var isArchive = PackageTypes.IsArchiveFile(source);
        var gameDirectory = GameLocator.NormalizePath(instance.GameDir);

        // 压缩包（含地图压缩包）一律解压合并到游戏根目录
        if (isArchive) return new PackageTarget(source, true, gameDirectory, true);

        // 单个 .map 文件装到 Maps\Custom
        if (type == PackageType.Map)
            return new PackageTarget(source, false, Path.Combine(gameDirectory, "Maps", "Custom"), true);

        // 目录 → 游戏目录\包名
        if (Directory.Exists(source)) return new PackageTarget(source, false, Path.Combine(gameDirectory, packageName), false);

        // 其它单文件 → 游戏目录\同名文件
        return new PackageTarget(source, false, gameDirectory, true);
    }

    /// <summary>
    /// 扫描来源与目标之间的文件级冲突（对应旧版 scan_install_conflicts）。
    /// 返回冲突文件的相对路径，最多 50 条。
    /// </summary>
    public static IReadOnlyList<string> ScanConflicts(string sourceRoot, string targetRoot)
    {
        var conflicts = new List<string>();

        if (!Directory.Exists(sourceRoot) || !Directory.Exists(targetRoot)) return conflicts;

        foreach (var relative in DirectoryCopier.ListFiles(sourceRoot))
        {
            if (conflicts.Count >= 50) break;

            var destination = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination)) conflicts.Add(relative);
        }

        return conflicts;
    }

    /// <summary>安装一个包；成功时写入精确安装记录与实例的已安装列表。</summary>
    public static InstallOutcome Install(GameInstance instance, PackageType type, string packageName,
        CopyConflictPolicy policy = CopyConflictPolicy.Overwrite,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        var extractDirectory = string.Empty;
        OperationJournal? journal = null;

        try
        {
            if (instance is null) return Fail("未指定实例");
            if (string.IsNullOrWhiteSpace(packageName)) return Fail("包名为空");

            if (!Directory.Exists(instance.GameDir))
                return Fail($"当前实例的游戏目录已不存在或被移动：{instance.GameDir}\n" +
                            "请检查游戏是否还在原位置，或更新实例路径。");

            var target = Describe(instance, type, packageName);

            if (!File.Exists(target.SourcePath) && !Directory.Exists(target.SourcePath))
                return Fail($"包文件不存在：{target.SourcePath}");

            // 1) 压缩包先解压到临时目录
            var contentRoot = target.SourcePath;

            if (target.IsArchive)
            {
                token.ThrowIfCancellationRequested();

                extractDirectory = Path.Combine(Paths.Temp, $"install_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
                Directory.CreateDirectory(extractDirectory);

                Log.Info($"正在解压：{target.SourcePath} → {extractDirectory}");

                if (!ArchiveExtractor.TryExtract(target.SourcePath, extractDirectory, out var error,
                        progress is null ? null : new ProgressSpan(progress, 0, 0.3), token))
                {
                    TryDeleteDirectory(extractDirectory);
                    return Fail($"解压失败：{error}");
                }

                if (!Directory.EnumerateFileSystemEntries(extractDirectory).Any())
                {
                    TryDeleteDirectory(extractDirectory);
                    return Fail("压缩包内容为空，未解压到任何文件");
                }

                contentRoot = ArchiveExtractor.ResolveSingleTopDirectory(extractDirectory);
            }

            // 2) 先算出本次会落到游戏目录的文件，覆盖前做快照
            var relativeFiles = ResolveTargetFiles(instance.GameDir, contentRoot, target.TargetDirectory, packageName);
            var snapshot = InstallRecordStore.SnapshotExistingFiles(instance.GameDir, relativeFiles);

            journal = new OperationJournal(Paths.Temp);

            // 3) 目录级替换：目标目录已存在时整体搬进隔离区，再铺新内容
            if (ShouldReplaceWholeDirectory(target, policy) && journal.MoveToQuarantine(target.TargetDirectory) is null)
                return Fail($"目标目录已存在且无法替换：{target.TargetDirectory}");

            // 4) 复制（覆盖前留 .bak，未覆盖的登记为新建）
            var summary = DirectoryCopier.Copy(contentRoot, target.TargetDirectory, policy, journal,
                progress is null ? null : new ProgressSpan(progress, 0.3, 0.95), token);

            if (summary.Total == 0)
            {
                journal.Rollback();
                return Fail("安装源中没有可复制的文件");
            }

            if (summary.AllFailed)
            {
                journal.Rollback();
                return Fail($"安装失败：所有 {summary.Total} 个文件均无法复制");
            }

            if (token.IsCancellationRequested)
            {
                journal.Rollback();
                return new InstallOutcome(false, true, true, summary.Total, summary.Failed, summary.Skipped,
                    "安装已取消，已回滚本次改动");
            }

            // 5) 校验落点非空（对应旧版 _dir_file_count(target) == 0）
            if (DirectoryCopier.CountFiles(target.TargetDirectory) == 0)
            {
                journal.Rollback();
                return Fail("安装后目标目录为空，可能复制失败");
            }

            // 6) 记录安装结果
            var recordName = RecordNameOf(packageName, target.IsArchive);

            instance.AddInstalled(type, recordName);
            InstanceStore.Save(instance);

            var saved = InstallRecordStore.Save(new InstallRecord
            {
                PackageType = PackageTypes.DirectoryNameOf(type),
                PackageName = recordName,
                SourceArchive = target.IsArchive ? packageName : string.Empty,
                InstanceId = instance.Id,
                InstanceName = instance.Name,
                Files = relativeFiles,
                OriginalSnapshot = snapshot
            });

            journal.Commit();
            progress?.Report(1);

            var message = $"已安装：{recordName}\n共处理 {summary.Total} 个文件" +
                          (summary.Skipped > 0 ? $"（跳过已存在 {summary.Skipped} 个）" : string.Empty) +
                          (summary.Failed > 0 ? $"\n其中 {summary.Failed} 个文件复制失败，已跳过" : string.Empty);

            if (!saved)
            {
                message += "\n\n⚠️ 文件已装到游戏目录，但安装记录写入失败：" +
                           "重启后「已安装」列表可能不显示该包，也无法精确卸载。";
            }

            Log.Info($"安装完成：{recordName}（{PackageTypes.DirectoryNameOf(type)}），" +
                     $"{summary.Total} 个文件，失败 {summary.Failed}");

            return new InstallOutcome(true, false, false, summary.Total, summary.Failed, summary.Skipped, message);
        }
        catch (OperationCanceledException)
        {
            journal?.Rollback();
            return new InstallOutcome(false, true, true, 0, 0, 0, "安装已取消，已回滚本次改动");
        }
        catch (Exception ex)
        {
            journal?.Rollback();
            Log.Error($"安装失败：{packageName}", ex);
            return Fail($"安装失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(extractDirectory);
        }
    }

    /// <summary>
    /// 卸载一个包：
    /// <list type="bullet">
    /// <item>单个 .map 文件：直接从 <c>Maps\Custom</c> 删除，不需要原版备份。</item>
    /// <item>有精确安装记录：只删这个包的文件，被它覆盖掉的原版文件从 MO 备份还原。</item>
    /// <item>没有记录：全量恢复（清空游戏目录后从 MO 备份复制），可用 allowFullRestore 禁止。</item>
    /// </list>
    /// </summary>
    public static UninstallOutcome Uninstall(GameInstance instance, PackageType type, string packageName,
        bool allowFullRestore = true, IProgress<double>? progress = null, CancellationToken token = default)
    {
        try
        {
            if (instance is null) return FailUninstall("未指定实例");
            if (string.IsNullOrWhiteSpace(packageName)) return FailUninstall("包名为空");

            if (!Directory.Exists(instance.GameDir))
                return FailUninstall($"当前实例的游戏目录已不存在或被移动：{instance.GameDir}");

            var isArchive = PackageTypes.IsArchiveExtension(Path.GetExtension(packageName));

            if (type == PackageType.Map && !isArchive)
                return UninstallMap(instance, packageName, progress, token);

            if (!Directory.Exists(BackupService.OriginalBackupPath))
                return FailUninstall("未找到原版游戏备份。请先执行「备份原版游戏」，把原版游戏备份到：\n" +
                                     BackupService.OriginalBackupPath +
                                     "\n\n原版备份是按包卸载、恢复原版状态的必要条件。");

            var record = FindRecord(instance, type, packageName);

            if (record is { Files.Count: > 0 })
                return UninstallSelective(instance, type, packageName, record, progress, token);

            if (!allowFullRestore)
                return FailUninstall("未找到该包的精确安装记录，且当前不允许全量恢复原版。");

            return UninstallFull(instance, progress, token);
        }
        catch (OperationCanceledException)
        {
            return new UninstallOutcome(false, true, true, 0, 0, 0, 0, 0, "卸载已取消，已回滚本次改动");
        }
        catch (Exception ex)
        {
            Log.Error($"卸载失败：{packageName}", ex);
            return FailUninstall($"卸载失败：{ex.Message}");
        }
    }

    // ————— 选择性卸载 —————

    private static UninstallOutcome UninstallSelective(GameInstance instance, PackageType type,
        string packageName, InstallRecord record, IProgress<double>? progress, CancellationToken token)
    {
        var journal = new OperationJournal(Paths.Temp);
        var targetRoot = PathGuard.NormalizeRoot(instance.GameDir);

        var deleted = 0;
        var deleteFailed = 0;
        var restored = 0;
        var restoreFailed = 0;
        var handled = new List<string>();
        var toRestore = new List<string>();

        try
        {
            // 1) 该包的文件搬进隔离区：提交=真删除，回滚=原样搬回
            foreach (var relative in record.Files)
            {
                token.ThrowIfCancellationRequested();

                var normalized = PathGuard.NormalizeRelative(relative);
                if (normalized.Length == 0) continue;

                if (!PathGuard.TryResolve(targetRoot, normalized, out var full) ||
                    !PathGuard.IsInside(targetRoot, full))
                {
                    Log.Warn($"安装记录里的路径不合法，已跳过：{relative}");
                    continue;
                }

                handled.Add(normalized);

                if (!File.Exists(full)) continue;

                if (journal.MoveToQuarantine(full) is null)
                {
                    deleteFailed++;
                    continue;
                }

                deleted++;

                var backupFile = Path.Combine(BackupService.OriginalBackupPath,
                    normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(backupFile)) toRestore.Add(normalized);
            }

            // 2) 自下而上清理空目录
            var cleaned = DirectoryCopier.CleanEmptyDirectories(targetRoot, handled);

            // 3) 从 MO 备份还原被覆盖掉的原版文件
            foreach (var relative in toRestore)
            {
                token.ThrowIfCancellationRequested();

                var source = Path.Combine(BackupService.OriginalBackupPath,
                    relative.Replace('/', Path.DirectorySeparatorChar));

                if (!PathGuard.TryResolve(targetRoot, relative, out var destination)) continue;

                try
                {
                    var directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    journal.TrackCreated(destination);
                    DirectoryCopier.CopyFile(source, destination);
                    restored++;
                }
                catch (Exception ex)
                {
                    restoreFailed++;
                    Log.Error($"还原原版文件失败：{relative}", ex);
                }
            }

            if (token.IsCancellationRequested)
            {
                journal.Rollback();
                return new UninstallOutcome(false, true, true, deleted, deleteFailed, restored, restoreFailed,
                    cleaned, "卸载已取消，已回滚本次改动");
            }

            // 4) 更新已安装列表与安装记录
            RemoveInstalledNames(instance, type, packageName);
            InstallRecordStore.Delete(instance, type, packageName);

            journal.Commit();
            progress?.Report(1);

            var warning = instance.IsValid
                ? string.Empty
                : "\n⚠️ 卸载后未能识别为有效的心灵终结游戏目录，请手动检查游戏文件。";

            Log.Info($"精确卸载完成：{packageName}，删除 {deleted}（失败 {deleteFailed}），" +
                     $"还原原版 {restored}（失败 {restoreFailed}），清理空目录 {cleaned}");

            return new UninstallOutcome(deleteFailed == 0 && restoreFailed == 0, false, false,
                deleted, deleteFailed, restored, restoreFailed, cleaned,
                $"精确卸载完成\n包名：{packageName}\n删除文件：{deleted} 个（失败 {deleteFailed}）\n" +
                $"还原原版：{restored} 个（失败 {restoreFailed}）\n清理空目录：{cleaned} 个" + warning);
        }
        catch
        {
            journal.Rollback();
            throw;
        }
    }

    // ————— 全量恢复原版 —————

    private static UninstallOutcome UninstallFull(GameInstance instance, IProgress<double>? progress, CancellationToken token)
    {
        var targetRoot = PathGuard.NormalizeRoot(instance.GameDir);
        var journal = new OperationJournal(Paths.Temp);

        try
        {
            Directory.CreateDirectory(targetRoot);

            // 1) 现有内容整体搬进隔离区（旧版是直接删除；搬走才能在失败时搬回来）
            foreach (var entry in Directory.GetFileSystemEntries(targetRoot))
            {
                token.ThrowIfCancellationRequested();

                if (journal.MoveToQuarantine(entry) is null)
                    throw new IOException($"无法移走现有内容：{entry}");
            }

            // 2) 从 MO 备份复制
            var summary = DirectoryCopier.Copy(BackupService.OriginalBackupPath, targetRoot,
                CopyConflictPolicy.Overwrite, journal,
                progress is null ? null : new ProgressSpan(progress, 0.05, 0.95), token);

            if (summary.Total == 0 || summary.AllFailed)
            {
                journal.Rollback();

                return FailUninstall(summary.Total == 0
                    ? "原版备份为空，未恢复任何文件"
                    : $"卸载失败：所有 {summary.Total} 个文件均无法复制");
            }

            if (token.IsCancellationRequested)
            {
                journal.Rollback();
                return new UninstallOutcome(false, true, true, 0, 0, 0, 0, 0, "卸载已取消，已回滚本次改动");
            }

            // 3) 清空已安装列表与安装记录
            instance.ClearInstalled();
            InstanceStore.Save(instance);
            InstallRecordStore.DeleteAll(instance);

            journal.Commit();
            progress?.Report(1);

            var warning = instance.IsValid
                ? string.Empty
                : "\n⚠️ 恢复后未能识别为有效的心灵终结游戏目录，请手动检查游戏文件是否完整。";

            Log.Info($"全量恢复完成：{instance.Name}，恢复 {summary.Total} 个文件，失败 {summary.Failed}");

            return new UninstallOutcome(true, false, false, 0, 0, summary.Total, summary.Failed, 0,
                $"卸载完成，实例已恢复为原版游戏状态。\n目标实例：{instance.Name}\n恢复文件数：{summary.Total}" +
                (summary.Failed > 0 ? $"\n（其中 {summary.Failed} 个文件复制失败）" : string.Empty) + warning);
        }
        catch
        {
            journal.Rollback();
            throw;
        }
    }

    // ————— 地图包（单 .map 文件）卸载 —————

    private static UninstallOutcome UninstallMap(GameInstance instance, string packageName,
        IProgress<double>? progress, CancellationToken token)
    {
        var gameRoot = PathGuard.NormalizeRoot(instance.GameDir);
        var mapsCustom = Path.Combine(gameRoot, "Maps", "Custom");
        var journal = new OperationJournal(Paths.Temp);

        var candidates = new List<string>();
        var record = FindRecord(instance, PackageType.Map, packageName);

        if (record is { Files.Count: > 0 })
        {
            foreach (var relative in record.Files) AddMapCandidate(candidates, gameRoot, mapsCustom, relative);
        }
        else
        {
            // 没有精确记录：按包名猜 .map / .yrm / .mpr
            var stem = Path.GetFileNameWithoutExtension(packageName);

            foreach (var extension in new[] { ".map", ".yrm", ".mpr" })
            {
                AddMapCandidate(candidates, gameRoot, mapsCustom, Path.Combine("Maps", "Custom", stem + extension));
                AddMapCandidate(candidates, gameRoot, mapsCustom, Path.Combine("Maps", "Custom", packageName + extension));
            }
        }

        var deleted = 0;
        var failed = 0;

        try
        {
            foreach (var file in candidates)
            {
                token.ThrowIfCancellationRequested();

                if (!File.Exists(file)) continue;

                if (journal.MoveToQuarantine(file) is null) failed++;
                else
                {
                    deleted++;
                    Log.Info($"已删除地图文件：{file}");
                }
            }

            RemoveInstalledNames(instance, PackageType.Map, packageName);
            InstallRecordStore.Delete(instance, PackageType.Map, packageName);

            journal.Commit();
            progress?.Report(1);

            var message = deleted == 0
                ? $"在 Maps\\Custom 中未找到与「{packageName}」匹配的文件，可能已被手动删除，仅清除了安装记录。"
                : $"已删除 {deleted} 个地图文件" + (failed > 0 ? $"（{failed} 个失败）" : string.Empty);

            return new UninstallOutcome(failed == 0, false, false, deleted, failed, 0, 0, 0, message);
        }
        catch
        {
            journal.Rollback();
            throw;
        }
    }

    // ————— 工具 —————

    /// <summary>
    /// 把相对路径按「相对游戏目录」解析；旧版地图记录是相对 <c>Maps\Custom</c> 的，两种都认。
    /// </summary>
    private static void AddMapCandidate(List<string> candidates, string gameRoot, string mapsCustom, string relative)
    {
        var normalized = PathGuard.NormalizeRelative(relative);
        if (normalized.Length == 0) return;

        if (PathGuard.TryResolve(gameRoot, normalized, out var fromGameRoot) &&
            File.Exists(fromGameRoot) &&
            !candidates.Contains(fromGameRoot, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(fromGameRoot);
            return;
        }

        var prefix = "Maps/Custom/";
        var nested = normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;

        if (!PathGuard.TryResolve(mapsCustom, nested, out var fromMapsCustom)) return;
        if (!File.Exists(fromMapsCustom)) return;
        if (candidates.Contains(fromMapsCustom, StringComparer.OrdinalIgnoreCase)) return;

        candidates.Add(fromMapsCustom);
    }

    private static InstallRecord? FindRecord(GameInstance instance, PackageType type, string packageName)
    {
        foreach (var name in InstallRecordStore.CandidateNames(packageName))
        {
            var record = InstallRecordStore.Load(instance, type, name);
            if (record is not null) return record;
        }

        return null;
    }

    /// <summary>包名统一用「去掉压缩包扩展名」的形式记录（旧版 16878 行）。</summary>
    private static string RecordNameOf(string packageName, bool isArchive)
        => isArchive ? Path.GetFileNameWithoutExtension(packageName) : packageName;

    private static void RemoveInstalledNames(GameInstance instance, PackageType type, string packageName)
    {
        foreach (var name in InstallRecordStore.CandidateNames(packageName)) instance.RemoveInstalled(type, name);

        InstanceStore.Save(instance);
    }

    /// <summary>是否需要「先把整个目标目录移走再铺新内容」。</summary>
    private static bool ShouldReplaceWholeDirectory(PackageTarget target, CopyConflictPolicy policy)
        => policy == CopyConflictPolicy.Overwrite
           && !target.MergeIntoRoot
           && Directory.Exists(target.SourcePath)
           && Directory.Exists(target.TargetDirectory);

    /// <summary>算出本次会落到游戏目录的文件（相对游戏目录，"/" 分隔）。</summary>
    private static List<string> ResolveTargetFiles(string gameDirectory, string contentRoot,
        string targetDirectory, string packageName)
    {
        var result = new List<string>();
        var gameRoot = PathGuard.NormalizeRoot(gameDirectory);

        if (File.Exists(contentRoot))
        {
            var fileName = Path.GetFileName(contentRoot);
            if (fileName.Length == 0) fileName = packageName;

            result.Add(PathGuard.RelativeOf(gameRoot, Path.Combine(targetDirectory, fileName)));
            return result;
        }

        foreach (var relative in DirectoryCopier.ListFiles(contentRoot))
        {
            var target = Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            result.Add(PathGuard.RelativeOf(gameRoot, target));
        }

        return result;
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

    private static InstallOutcome Fail(string message)
    {
        Log.Warn($"安装失败：{message}");
        return new InstallOutcome(false, false, false, 0, 0, 0, message);
    }

    private static UninstallOutcome FailUninstall(string message)
    {
        Log.Warn($"卸载失败：{message}");
        return new UninstallOutcome(false, false, false, 0, 0, 0, 0, 0, message);
    }
}
