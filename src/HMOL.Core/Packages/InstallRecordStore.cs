using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.Packages;

/// <summary>
/// 精确安装记录的读写。
/// 位置：<c>instances\&lt;id&gt;\install_records\&lt;类型&gt;\&lt;包名&gt;.json</c>（对应旧版 get_install_record_path）。
/// </summary>
public static class InstallRecordStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 用户可能手工查看/修改过记录，键名大小写不敏感
        PropertyNameCaseInsensitive = true,
        // 不转义中文，保证用户手工查看记录时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>实例的安装记录根目录。</summary>
    public static string DirectoryOf(GameInstance instance) => instance.InstallRecordsDirectory;

    /// <summary>某个包记录文件的绝对路径。包名里的非法字符会被替换为 "_"。</summary>
    public static string PathOf(GameInstance instance, PackageType type, string packageName)
    {
        var safeName = PathGuard.SanitizeFileName(Path.GetFileNameWithoutExtension(packageName.Trim()));
        return Path.Combine(DirectoryOf(instance), PackageTypes.DirectoryNameOf(type), safeName + ".json");
    }

    /// <summary>写入安装记录。</summary>
    public static bool Save(InstallRecord record)
    {
        if (record is null) return false;

        var instance = InstanceStore.FindById(record.InstanceId);
        if (instance is null)
        {
            Log.Warn($"保存安装记录失败：找不到实例 {record.InstanceId}");
            return false;
        }

        var type = PackageTypes.ParseOrNull(record.PackageType) ?? PackageType.Ini;
        record.Normalize();

        try
        {
            var path = PathOf(instance, type, record.PackageName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DirectoryOf(instance));

            File.WriteAllText(path, JsonSerializer.Serialize(record, Options));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"保存安装记录失败：{record.PackageName}", ex);
            return false;
        }
    }

    /// <summary>读取指定包的安装记录，不存在或损坏返回 null。</summary>
    public static InstallRecord? Load(GameInstance instance, PackageType type, string packageName)
    {
        var path = PathOf(instance, type, packageName);
        if (!File.Exists(path)) return null;

        return ReadFrom(path);
    }

    /// <summary>列出实例的全部安装记录；指定类型时只列该类型。损坏的文件跳过。</summary>
    public static IReadOnlyList<InstallRecord> List(GameInstance instance, PackageType? type = null)
    {
        var result = new List<InstallRecord>();
        var root = DirectoryOf(instance);

        if (!Directory.Exists(root)) return result;

        IReadOnlyList<PackageType> types = type is null ? PackageTypes.All : [type.Value];

        foreach (var item in types)
        {
            var directory = Path.Combine(root, PackageTypes.DirectoryNameOf(item));
            if (!Directory.Exists(directory)) continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    var record = ReadFrom(file);
                    if (record is null)
                    {
                        Log.Warn($"安装记录解析失败，已跳过：{file}");
                        continue;
                    }

                    result.Add(record);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"列举安装记录失败：{directory}", ex);
            }
        }

        return result;
    }

    /// <summary>删除指定包的安装记录。对应旧版会同时尝试「去扩展名」和「原名」两个名字。</summary>
    public static bool Delete(GameInstance instance, PackageType type, string packageName)
    {
        var deleted = false;

        foreach (var name in CandidateNames(packageName))
        {
            var path = PathOf(instance, type, name);

            try
            {
                if (!File.Exists(path)) continue;

                File.Delete(path);
                deleted = true;
            }
            catch (Exception ex)
            {
                Log.Error($"删除安装记录失败：{path}", ex);
            }
        }

        return deleted;
    }

    /// <summary>清空实例的全部安装记录（对应旧版全量恢复原版后删除 install_records 目录）。</summary>
    public static bool DeleteAll(GameInstance instance)
    {
        var root = DirectoryOf(instance);

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"清空安装记录失败：{root}", ex);
            return false;
        }
    }

    /// <summary>对「将被覆盖的已有文件」做快照（对应旧版 snapshot_existing_files）。</summary>
    public static Dictionary<string, FileSnapshot> SnapshotExistingFiles(string gameDirectory,
        IEnumerable<string> relativeFiles)
    {
        var snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in relativeFiles)
        {
            if (!PathGuard.TryResolve(gameDirectory, relative, out var full)) continue;
            if (!File.Exists(full)) continue;

            try
            {
                var info = new FileInfo(full);
                snapshot[PathGuard.NormalizeRelative(relative)] = new FileSnapshot(info.Length, info.LastWriteTime);
            }
            catch (Exception ex)
            {
                Log.Warn($"记录文件快照失败：{full}（{ex.Message}）");
            }
        }

        return snapshot;
    }

    /// <summary>记录名候选：压缩包去扩展名后的名字，以及原名。</summary>
    public static IReadOnlyList<string> CandidateNames(string packageName)
    {
        var name = (packageName ?? string.Empty).Trim();
        if (name.Length == 0) return [];

        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0 || string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)) return [name];

        return [stem, name];
    }

    private static InstallRecord? ReadFrom(string file)
    {
        try
        {
            var record = JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(file), Options);
            record?.Normalize();
            return record;
        }
        catch
        {
            return null;
        }
    }
}
