using System.Globalization;
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
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        var extractDirectory = string.Empty;
        var scratch = string.Empty;
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

            // 解压暂存与隔离区都放在游戏目录所在分区：
            // 跨盘时 GB 级内容要先写到系统盘再复制过来（白写一遍，还可能写满系统盘），
            // 而隔离区的跨盘移动更会退化成整目录复制。
            scratch = Paths.ScratchFor(instance.GameDir);

            // 分阶段计时：万一还慢，日志里能直接看出是解压还是铺入在耗时
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var extractMs = 0L;

            // 1) 压缩包先解压到临时目录
            var contentRoot = target.SourcePath;

            if (target.IsArchive)
            {
                token.ThrowIfCancellationRequested();

                extractDirectory = Path.Combine(scratch, $"install_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
                Directory.CreateDirectory(extractDirectory);

                Log.Info($"正在解压：{target.SourcePath} → {extractDirectory}");

                if (!ArchiveExtractor.TryExtract(target.SourcePath, extractDirectory, out var error,
                        progress is null ? null : new ProgressSpan(progress, 0, 0.85), token))
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
                extractMs = watch.ElapsedMilliseconds;
                watch.Restart();
            }

            // 2) 先算出本次会落到游戏目录的文件，覆盖前做快照
            var relativeFiles = ResolveTargetFiles(instance.GameDir, contentRoot, target.TargetDirectory, packageName);
            var snapshot = InstallRecordStore.SnapshotExistingFiles(instance.GameDir, relativeFiles);

            journal = new OperationJournal(scratch);

            // 3) 目录级替换：目标目录已存在时整体搬进隔离区，再铺新内容
            if (ShouldReplaceWholeDirectory(target, policy) && journal.MoveToQuarantine(target.TargetDirectory) is null)
                return Fail($"目标目录已存在且无法替换：{target.TargetDirectory}");

            // 4) 铺到游戏目录（覆盖前留 .bak，未覆盖的登记为新建）。
            // 压缩包的内容是刚解压出来的暂存副本，同分区时逐文件改名就位，不再复制一遍字节。
            var summary = DirectoryCopier.Copy(contentRoot, target.TargetDirectory, policy, journal,
                progress is null ? null : new ProgressSpan(progress, target.IsArchive ? 0.85 : 0, 0.97), token,
                consumeSource: target.IsArchive);

            Log.Info($"安装耗时：解压 {extractMs} ms，铺入 {watch.ElapsedMilliseconds} ms" +
                     $"（{summary.Total} 个文件，{BackupService.FormatSize(summary.BytesCopied)}）");

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
            progress?.Report(new ProgressSample(1));

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
            Paths.CleanupScratch(scratch);
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
        bool allowFullRestore = true, IProgress<ProgressSample>? progress = null, CancellationToken token = default)
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

    // ————— 按文件卸载 / 按时间点回退 —————

    /// <summary>游戏目录里一个可回退的时间点：一次操作留下的 <c>.bak-&lt;时间戳&gt;</c> 分组。</summary>
    /// <param name="Stamp">时间戳（yyyyMMddHHmmss），回退时按它匹配。</param>
    /// <param name="Time">本地时间，界面直接显示。</param>
    /// <param name="FileCount">该时间点留下的备份文件数。</param>
    /// <param name="Samples">前几个文件的相对路径，供界面展示。</param>
    public sealed record RevertPoint(string Stamp, DateTime Time, int FileCount, IReadOnlyList<string> Samples);

    /// <summary>覆盖文件时留下的备份文件名里那一段分隔符。</summary>
    private const string BakMarker = ".bak-";

    /// <summary>
    /// 按选定的文件卸载：只处理用户勾选的那几个文件，其余包与文件一律不动。
    /// 安装记录跟着缩减——全勾完就删掉记录，只勾一部分则记录里保留剩下的，方便之后再卸。
    /// 被这些文件覆盖掉的原版文件，从 MO 原版备份还原回去。
    /// </summary>
    public static UninstallOutcome UninstallFiles(GameInstance instance, PackageType type, string packageName,
        IReadOnlyCollection<string> files, IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        try
        {
            if (instance is null) return FailUninstall("未指定实例");
            if (string.IsNullOrWhiteSpace(packageName)) return FailUninstall("包名为空");
            if (files is null || files.Count == 0) return FailUninstall("没有勾选任何文件");

            if (!Directory.Exists(instance.GameDir))
                return FailUninstall($"当前实例的游戏目录已不存在或被移动：{instance.GameDir}");

            var record = FindRecord(instance, type, packageName);

            if (record is not { Files.Count: > 0 })
                return FailUninstall("这个包没有精确安装记录，无法按文件删除。请改用「按包卸载」或「全量恢复原版」。");

            // 只认记录里确实存在的文件：界面传来的路径一律不当依据
            var wanted = files
                .Select(PathGuard.NormalizeRelative)
                .Where(item => item.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var selected = record.Files
                .Where(item => wanted.Contains(PathGuard.NormalizeRelative(item)))
                .ToList();

            if (selected.Count == 0)
                return FailUninstall("勾选的文件都不在该包的安装记录里，已取消。");

            return RunSelective(instance, type, packageName, record, selected, progress, token);
        }
        catch (OperationCanceledException)
        {
            return new UninstallOutcome(false, true, true, 0, 0, 0, 0, 0, "卸载已取消，已回滚本次改动");
        }
        catch (Exception ex)
        {
            Log.Error($"按文件卸载失败：{packageName}", ex);
            return FailUninstall($"按文件卸载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 列出游戏目录里所有可回退的时间点。来源是各次安装 / 还原覆盖文件时留下的
    /// <c>&lt;文件&gt;.bak-&lt;yyyyMMddHHmmss&gt;[-n]</c>：同一时间戳归为一个时间点，按时间倒序返回。
    /// </summary>
    public static IReadOnlyList<RevertPoint> ListRevertPoints(GameInstance instance)
    {
        var result = new List<RevertPoint>();

        if (instance is null || !Directory.Exists(instance.GameDir)) return result;

        var root = PathGuard.NormalizeRoot(instance.GameDir);
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*" + BakMarker + "*", SearchOption.AllDirectories))
            {
                var stamp = StampOf(file);
                if (stamp is null) continue;

                if (!groups.TryGetValue(stamp, out var list))
                {
                    list = [];
                    groups[stamp] = list;
                }

                list.Add(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描可回退时间点失败：{ex.Message}");
            return result;
        }

        foreach (var (stamp, files) in groups)
        {
            if (!TryParseStamp(stamp, out var time)) continue;

            result.Add(new RevertPoint(stamp, time, files.Count,
                files.Take(5).Select(item => Path.GetRelativePath(root, item).Replace('\\', '/')).ToList()));
        }

        return result.OrderByDescending(item => item.Time).ToList();
    }

    /// <summary>
    /// 回退到某个时间点：把该时间点留下的所有 <c>.bak-&lt;时间戳&gt;</c> 复制回各自的原位置。
    /// 目标文件当前内容会被替换掉——这正是「回退」的语义，所以调用前必须让用户确认。
    /// 备份文件本身保留（万一又要回到回退前），整个过程可取消、失败可整体回滚。
    /// </summary>
    public static UninstallOutcome RevertToPoint(GameInstance instance, string stamp,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        try
        {
            if (instance is null) return FailUninstall("未指定实例");
            if (string.IsNullOrWhiteSpace(stamp)) return FailUninstall("未指定要回退的时间点");

            if (!Directory.Exists(instance.GameDir))
                return FailUninstall($"当前实例的游戏目录已不存在或被移动：{instance.GameDir}");

            var root = PathGuard.NormalizeRoot(instance.GameDir);
            var pairs = new List<(string Backup, string Target)>();

            foreach (var file in Directory.EnumerateFiles(root, "*" + BakMarker + "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(StampOf(file), stamp, StringComparison.OrdinalIgnoreCase)) continue;

                var target = file[..file.LastIndexOf(BakMarker, StringComparison.Ordinal)];
                if (target.Length == 0) continue;

                pairs.Add((file, target));
            }

            if (pairs.Count == 0)
                return FailUninstall("这个时间点已经没有可回退的备份文件了（可能已被清理或恢复过）。");

            var journal = new OperationJournal(Paths.ScratchFor(instance.GameDir));
            var restored = 0;
            var failed = 0;

            // 记录这次到底还原了哪些文件（相对游戏目录），回退完据此判断哪些包的安装被整体撤销了
            var restoredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                for (var i = 0; i < pairs.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new ProgressSample((double)i / pairs.Count));

                    var (backup, target) = pairs[i];

                    try
                    {
                        var directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                        // 现有内容先搬进隔离区：提交=真删掉，回滚=原样搬回来
                        if (File.Exists(target))
                        {
                            journal.MoveToQuarantine(target);
                            if (File.Exists(target))
                            {
                                failed++;
                                Log.Warn($"回退时无法移走现有文件，已跳过：{target}");
                                continue;
                            }
                        }

                        journal.TrackCreated(target);
                        DirectoryCopier.CopyFile(backup, target);
                        restored++;
                        restoredTargets.Add(PathGuard.NormalizeRelative(Path.GetRelativePath(root, target)));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Error($"回退文件失败：{target}", ex);
                    }
                }

                if (token.IsCancellationRequested)
                {
                    journal.Rollback();
                    return new UninstallOutcome(false, true, true, 0, 0, restored, failed, 0, "回退已取消，已回滚本次改动");
                }

                journal.Commit();
                progress?.Report(new ProgressSample(1));

                // 回退把某次安装覆盖的原版文件还原之后，那次安装等于被撤销了：
                // 顺手把它新建的文件删掉，并从「已安装」列表与安装记录里移除，避免列表与实际不符。
                var revertedPackages = MarkRevertedPackagesUninstalled(instance, restoredTargets);

                Log.Info($"按时间点回退完成：{stamp}，恢复 {restored} 个文件，失败 {failed}，撤销 {revertedPackages} 个包");

                var warning = instance.IsValid
                    ? string.Empty
                    : "\n⚠️ 回退后未能识别为有效的心灵终结游戏目录，请手动检查游戏文件。";

                return new UninstallOutcome(failed == 0, false, false, 0, 0, restored, failed, 0,
                    $"已回退到 {FormatStamp(stamp)}\n恢复文件：{restored} 个（失败 {failed}）\n" +
                    (revertedPackages > 0
                        ? $"该时间点安装的 {revertedPackages} 个包已被整体撤销：已从「已安装」列表移除并删除安装记录。\n"
                        : string.Empty) +
                    "备份文件仍保留在游戏目录里（.bak- 开头的文件），确认没问题后可以自行删除。" + warning);
            }
            catch
            {
                journal.Rollback();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return new UninstallOutcome(false, true, true, 0, 0, 0, 0, 0, "回退已取消，已回滚本次改动");
        }
        catch (Exception ex)
        {
            Log.Error($"按时间点回退失败：{stamp}", ex);
            return FailUninstall($"按时间点回退失败：{ex.Message}");
        }
    }

    /// <summary>从 <c>xxx.bak-20261002120000</c> / <c>xxx.bak-20261002120000-1</c> 里取出时间戳；不是这个形式返回 null。</summary>
    private static string? StampOf(string file)
    {
        var name = Path.GetFileName(file);
        var index = name.LastIndexOf(BakMarker, StringComparison.Ordinal);
        if (index < 0) return null;

        var tail = name[(index + BakMarker.Length)..];

        // 同一秒内多次备份会加 -1 / -2 这样的去重后缀
        var dash = tail.IndexOf('-');
        if (dash >= 0) tail = tail[..dash];

        return tail.Length == 14 && tail.All(char.IsAsciiDigit) ? tail : null;
    }

    private static bool TryParseStamp(string stamp, out DateTime time)
        => DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);

    /// <summary>时间戳转成人看的时间（解析不了就原样显示）。</summary>
    private static string FormatStamp(string stamp)
        => TryParseStamp(stamp, out var time) ? time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : stamp;

    /// <summary>
    /// 回退后收拾安装记录：某个包「覆盖过的原版文件」若全部在这次回退里被还原，
    /// 说明这次安装被整体撤销了——把它新建的文件删掉，再从「已安装」列表与安装记录里移除。
    /// 返回被撤销的包数量。找不到对应记录时不做任何事（旧记录 / 备份已被清理的情况）。
    /// </summary>
    private static int MarkRevertedPackagesUninstalled(GameInstance instance, IReadOnlySet<string> restoredTargets)
    {
        if (restoredTargets.Count == 0) return 0;

        var removed = 0;
        var root = PathGuard.NormalizeRoot(instance.GameDir);

        foreach (var record in InstallRecordStore.List(instance))
        {
            // 只认「覆盖过原版文件」的包：它的覆盖文件全部被还原，才算这次安装被整体撤销
            if (record.OriginalSnapshot.Count == 0) continue;
            if (!record.OriginalSnapshot.Keys.All(key => restoredTargets.Contains(PathGuard.NormalizeRelative(key)))) continue;

            var type = PackageTypes.ParseOrNull(record.PackageType);
            if (type is null) continue;

            // 删除这次安装新建、回退还原不到的文件（快照里没有的 = 当初新建的）
            foreach (var file in record.Files)
            {
                var relative = PathGuard.NormalizeRelative(file);

                if (record.OriginalSnapshot.ContainsKey(relative)) continue;
                if (!PathGuard.TryResolve(root, relative, out var full)) continue;

                try
                {
                    if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception ex)
                {
                    Log.Warn($"回退时删除新建文件失败：{full}（{ex.Message}）");
                }
            }

            RemoveInstalledNames(instance, type.Value, record.PackageName);
            InstallRecordStore.Delete(instance, type.Value, record.PackageName);
            removed++;

            Log.Info($"回退已撤销该时间点安装的包：{record.PackageName}（{record.PackageType}）");
        }

        return removed;
    }

    // ————— 选择性卸载 —————

    private static UninstallOutcome UninstallSelective(GameInstance instance, PackageType type,
        string packageName, InstallRecord record, IProgress<ProgressSample>? progress, CancellationToken token)
        => RunSelective(instance, type, packageName, record, record.Files, progress, token);

    /// <summary>
    /// 选择性卸载的公共实现。<paramref name="files"/> 是要删掉的那部分文件：
    /// 传整个 <c>record.Files</c> 就是整包卸载，传子集就是「按文件删除」，安装记录跟着缩减。
    /// </summary>
    private static UninstallOutcome RunSelective(GameInstance instance, PackageType type,
        string packageName, InstallRecord record, IReadOnlyList<string> files,
        IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var journal = new OperationJournal(Paths.ScratchFor(instance.GameDir));
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
            foreach (var relative in files)
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

            // 4) 更新已安装列表与安装记录：只删了一部分就缩减记录，全删完才整条清掉
            var remaining = record.Files
                .Where(item => !files.Contains(item, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (remaining.Count == 0)
            {
                RemoveInstalledNames(instance, type, packageName);
                InstallRecordStore.Delete(instance, type, packageName);
            }
            else
            {
                record.Files = remaining;
                record.Normalize();
                InstallRecordStore.Save(record);
            }

            journal.Commit();
            progress?.Report(new ProgressSample(1));

            var warning = instance.IsValid
                ? string.Empty
                : "\n⚠️ 卸载后未能识别为有效的心灵终结游戏目录，请手动检查游戏文件。";

            Log.Info($"精确卸载完成：{packageName}，删除 {deleted}（失败 {deleteFailed}），" +
                     $"还原原版 {restored}（失败 {restoreFailed}），清理空目录 {cleaned}");

            return new UninstallOutcome(deleteFailed == 0 && restoreFailed == 0, false, false,
                deleted, deleteFailed, restored, restoreFailed, cleaned,
                $"精确卸载完成\n包名：{packageName}\n删除文件：{deleted} 个（失败 {deleteFailed}）\n" +
                $"还原原版：{restored} 个（失败 {restoreFailed}）\n清理空目录：{cleaned} 个" +
                (remaining.Count > 0
                    ? $"\n\n该包仍有 {remaining.Count} 个文件留在游戏目录，安装记录已保留，随时可以再卸。"
                    : string.Empty) + warning);
        }
        catch
        {
            journal.Rollback();
            throw;
        }
    }

    // ————— 全量恢复原版 —————

    private static UninstallOutcome UninstallFull(GameInstance instance, IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var targetRoot = PathGuard.NormalizeRoot(instance.GameDir);
        var journal = new OperationJournal(Paths.ScratchFor(instance.GameDir));

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
            progress?.Report(new ProgressSample(1));

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
        IProgress<ProgressSample>? progress, CancellationToken token)
    {
        var gameRoot = PathGuard.NormalizeRoot(instance.GameDir);
        var mapsCustom = Path.Combine(gameRoot, "Maps", "Custom");
        var journal = new OperationJournal(Paths.ScratchFor(instance.GameDir));

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
            progress?.Report(new ProgressSample(1));

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
