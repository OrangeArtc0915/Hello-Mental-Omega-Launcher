using System.IO;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.Instances;

/// <summary>实例操作的返回：成功与否 + 给用户看的说明（沿用旧版 (ok, message) 的形式）。</summary>
public sealed record InstanceResult(bool Success, string Message);

/// <summary>
/// 实例的增删改查与导入导出。校验规则完全按旧版 <c>InstanceManager</c>：
/// 名称非空且唯一、路径必须是有效的心灵终结目录、同一路径不能被两个实例共用。
/// </summary>
public static class InstanceManager
{
    /// <summary>实例体积缓存：Id → (目录修改时间, 字节数)。</summary>
    private static readonly Dictionary<string, (DateTime Stamp, long Size)> SizeCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<GameInstance> All => InstanceStore.All;

    public static GameInstance? Current => InstanceStore.Current;

    /// <summary>新增实例（对应旧版 add_instance）。</summary>
    public static InstanceResult Add(string name, string? path, string note = "",
        GameKind kind = GameKind.MentalOmega, string? executable = null)
    {
        var instanceName = (name ?? string.Empty).Trim();
        var gameDir = GameLocator.NormalizePath(path);

        if (instanceName.Length == 0 || gameDir.Length == 0)
            return new InstanceResult(false, "实例名称和路径不能为空");

        if (All.Any(item => string.Equals(item.Name, instanceName, StringComparison.OrdinalIgnoreCase)))
            return new InstanceResult(false, "实例名称已存在");

        if (!GameLocator.IsGameDirectory(gameDir, kind, executable))
            return new InstanceResult(false, InvalidDirectoryMessage(kind, executable));

        if (All.Any(item => GameLocator.IsSamePath(item.GameDir, gameDir)))
            return new InstanceResult(false, "该游戏路径已被其他实例使用");

        var instance = new GameInstance
        {
            Name = instanceName,
            GameDir = gameDir,
            Note = (note ?? string.Empty).Trim(),
            Kind = kind,
            Executable = NormalizeExecutable(gameDir, executable)
        };

        EnsureDirectories(instance);
        InstanceStore.Upsert(instance);

        if (InstanceStore.Current is null) InstanceStore.SetCurrent(instance);

        Log.Info($"已创建实例「{instance.Name}」：{instance.GameDir}");
        return new InstanceResult(true, $"实例「{instanceName}」创建成功");
    }

    /// <summary>编辑实例：名称 / 路径 / 备注 / 游戏类型 / 主程序可以只改其中一部分（对应旧版 update_instance）。</summary>
    public static InstanceResult Update(string instanceId, string? newName = null, string? newPath = null,
        string? newNote = null, GameKind? newKind = null, string? newExecutable = null)
    {
        var instance = InstanceStore.FindById(instanceId);
        if (instance is null) return new InstanceResult(false, "实例不存在");

        if (newName is not null && !string.Equals(newName, instance.Name, StringComparison.Ordinal))
        {
            var name = newName.Trim();
            if (name.Length == 0) return new InstanceResult(false, "实例名称不能为空");

            if (All.Any(item => !ReferenceEquals(item, instance) &&
                                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                return new InstanceResult(false, "实例名称已存在");

            instance.Name = name;
        }

        var targetKind = newKind ?? instance.Kind;
        var targetExecutable = newExecutable is null ? instance.Executable : newExecutable;

        var pathChanged = newPath is not null && !GameLocator.IsSamePath(newPath, instance.GameDir);
        var kindChanged = newKind is not null && newKind.Value != instance.Kind;
        var executableChanged = newExecutable is not null &&
                                !string.Equals(NormalizeExecutable(instance.GameDir, newExecutable),
                                    instance.Executable, StringComparison.OrdinalIgnoreCase);

        var targetDir = pathChanged ? GameLocator.NormalizePath(newPath) : instance.GameDir;

        // 只有目录 / 类型 / 主程序真的变了才重新校验，避免「只改备注」也被坏目录挡住
        if (pathChanged || kindChanged || executableChanged)
        {
            if (!GameLocator.IsGameDirectory(targetDir, targetKind, targetExecutable))
                return new InstanceResult(false, InvalidDirectoryMessage(targetKind, targetExecutable));

            if (pathChanged && All.Any(item => !ReferenceEquals(item, instance) &&
                                               GameLocator.IsSamePath(item.GameDir, targetDir)))
                return new InstanceResult(false, "该游戏路径已被其他实例使用");

            instance.GameDir = targetDir;
            instance.Kind = targetKind;
            instance.Executable = NormalizeExecutable(targetDir, targetExecutable);
        }

        if (newNote is not null) instance.Note = newNote.Trim();

        InstanceStore.Upsert(instance);

        return new InstanceResult(true, "实例信息已更新");
    }

    /// <summary>
    /// 主程序落盘形式：位于游戏目录内时只存相对文件名（换目录、导入导出后仍可用），
    /// 目录外才存绝对路径；文件不存在则存空串（等同自动探测）。
    /// </summary>
    private static string NormalizeExecutable(string gameDir, string? executable)
    {
        var resolved = GameLocator.ResolveExecutable(gameDir, executable);
        if (resolved is null) return string.Empty;

        return PathGuard.IsInside(gameDir, resolved)
            ? Path.GetRelativePath(gameDir, resolved)
            : resolved;
    }

    /// <summary>目录校验失败时给用户的原因，「其它 Mod」要额外提醒去指定主程序。</summary>
    private static string InvalidDirectoryMessage(GameKind kind, string? executable)
        => kind == GameKind.Other && string.IsNullOrWhiteSpace(executable)
            ? "「其它红警 Mod」需要先指定一个可执行文件。"
            : "指定路径不是有效的游戏目录（或是主程序文件不存在）。";

    /// <summary>重命名实例（对应旧版 rename_instance）。</summary>
    public static InstanceResult Rename(string instanceId, string newName)
    {
        var instance = InstanceStore.FindById(instanceId);
        if (instance is null) return new InstanceResult(false, "实例不存在");

        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0) return new InstanceResult(false, "实例名称不能为空");

        if (All.Any(item => !ReferenceEquals(item, instance) &&
                            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            return new InstanceResult(false, "实例名称已存在");

        var oldName = instance.Name;
        instance.Name = name;

        InstanceStore.Upsert(instance);

        return new InstanceResult(true, $"实例重命名成功：「{oldName}」→「{name}」");
    }

    /// <summary>删除实例：配置文件与 <c>instances\&lt;id&gt;\</c> 一并删除（对应旧版 remove_instance）。</summary>
    public static InstanceResult Remove(string instanceId)
    {
        var instance = InstanceStore.FindById(instanceId);
        if (instance is null) return new InstanceResult(false, "实例不存在");

        var name = instance.Name;
        SizeCache.Remove(instanceId);

        if (!InstanceStore.Delete(instance, out var error))
            return new InstanceResult(false, $"删除实例时出错：{error}");

        return new InstanceResult(true, $"实例「{name}」已删除");
    }

    /// <summary>把某个实例设为当前实例（对应旧版 set_current_instance）。</summary>
    public static bool SetCurrent(string? instanceId)
    {
        var instance = InstanceStore.FindById(instanceId);

        if (instance is null)
        {
            InstanceStore.SetCurrent(null);
            return false;
        }

        InstanceStore.SetCurrent(instance);
        return true;
    }

    /// <summary>实例数据目录（<c>instances\&lt;id&gt;\</c>）。对应旧版 open_instance_directory。</summary>
    public static string DirectoryOf(string instanceId) => Paths.InstanceDir(instanceId);

    /// <summary>导出实例到压缩包（对应旧版 export_instance）。</summary>
    public static InstanceResult Export(string instanceId, string exportPath,
        System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Optimal,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
    {
        var instance = InstanceStore.FindById(instanceId);
        if (instance is null) return new InstanceResult(false, "实例不存在");

        return InstanceArchive.Export(instance, exportPath, level, progress, token);
    }

    /// <summary>从压缩包导入实例（对应旧版 import_instance），返回新实例的 Id。</summary>
    public static (InstanceResult Result, string? InstanceId) Import(string archivePath,
        IProgress<ProgressSample>? progress = null, CancellationToken token = default)
        => InstanceArchive.Import(archivePath, progress, token);

    /// <summary>
    /// 统计实例游戏目录的体积。默认用「目录修改时间 + 缓存」避免每次都全量扫描
    /// （对应旧版 get_instance_size 的缓存策略）。
    /// </summary>
    public static long GetSize(string instanceId, bool force = false)
    {
        var instance = InstanceStore.FindById(instanceId);
        if (instance is null || !Directory.Exists(instance.GameDir)) return 0;

        DateTime stamp;
        try
        {
            stamp = Directory.GetLastWriteTime(instance.GameDir);
        }
        catch
        {
            stamp = DateTime.MinValue;
        }

        if (!force && SizeCache.TryGetValue(instanceId, out var cached) && cached.Stamp == stamp)
            return cached.Size;

        var size = IO.DirectoryCopier.GetSize(instance.GameDir);
        SizeCache[instanceId] = (stamp, size);

        return size;
    }

    /// <summary>确保实例的数据目录存在（对应旧版 _ensure_instance_dirs）。</summary>
    public static void EnsureDirectories(GameInstance instance)
    {
        try
        {
            Directory.CreateDirectory(instance.InstallRecordsDirectory);
        }
        catch (Exception ex)
        {
            Log.Error($"创建实例目录失败：{instance.InstanceDirectory}", ex);
        }
    }
}
