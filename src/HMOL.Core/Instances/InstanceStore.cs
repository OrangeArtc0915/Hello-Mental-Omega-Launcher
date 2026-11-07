using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Instances;

/// <summary>
/// 实例的持久化：一个实例一个 JSON 文件，放在数据目录的 <c>instances\&lt;id&gt;.json</c>。
/// 单个文件坏掉只跳过它，不影响其它实例。
/// </summary>
public static class InstanceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 用户可能手工改过配置，键名大小写不敏感
        PropertyNameCaseInsensitive = true,
        // 不转义中文，保证用户手工编辑配置文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly List<GameInstance> Items = [];

    public static IReadOnlyList<GameInstance> All => Items;

    public static GameInstance? Current { get; private set; }

    /// <summary>列表或当前实例发生变化时触发。</summary>
    public static event Action? Changed;

    /// <summary>读取全部实例。上次使用的实例按设置里的 LastInstanceId 恢复。</summary>
    public static void Load()
    {
        Items.Clear();
        Current = null;

        try
        {
            if (Directory.Exists(Paths.Instances))
            {
                // 同一 Id 只认第一个文件：手工复制过配置文件时，界面上不该出现两条一模一样的实例
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.EnumerateFiles(Paths.Instances, "*.json"))
                {
                    var instance = ReadFrom(file, out var error);

                    if (instance is null)
                    {
                        Log.Warn(Loc.F("实例文件解析失败，已跳过：{0}（{1}）", Path.GetFileName(file), error));
                        continue;
                    }

                    if (!seen.Add(instance.Id))
                    {
                        Log.Warn(Loc.F("实例 Id 重复，已跳过：{0}（{1}）", Path.GetFileName(file), instance.Id));
                        continue;
                    }

                    Items.Add(instance);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("读取实例列表失败"), ex);
        }

        var lastId = SettingsStore.Current.LastInstanceId;
        Current = Items.FirstOrDefault(item => string.Equals(item.Id, lastId, StringComparison.OrdinalIgnoreCase))
                  ?? Items.FirstOrDefault();

        Log.Info(Loc.F("已载入 {0} 个实例，当前实例：{1}", Items.Count, Current?.Name ?? Loc.T("无")));
        Changed?.Invoke();
    }

    /// <summary>从指定文件读取一个实例。</summary>
    public static GameInstance? ReadFrom(string filePath, out string? error)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = Loc.T("文件不存在");
                return null;
            }

            var instance = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(filePath), Options);
            if (instance is null)
            {
                error = Loc.T("内容为空");
                return null;
            }

            if (string.IsNullOrWhiteSpace(instance.Id))
            {
                error = Loc.T("缺少实例 Id");
                return null;
            }

            instance.MigrateLegacyPackages();
            return instance;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static GameInstance? FindById(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>写入一个实例的配置文件（已存在则覆盖）。</summary>
    public static bool Save(GameInstance instance)
    {
        try
        {
            Directory.CreateDirectory(Paths.Instances);
            File.WriteAllText(Paths.InstanceFile(instance.Id), JsonSerializer.Serialize(instance, Options));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("实例保存失败：{0}", instance.Name), ex);
            return false;
        }
    }

    /// <summary>把实例登记进内存列表并落盘（已存在则更新）。</summary>
    public static void Upsert(GameInstance instance)
    {
        var index = Items.FindIndex(item => string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));

        if (index >= 0) Items[index] = instance;
        else Items.Add(instance);

        Save(instance);
        Changed?.Invoke();
    }

    /// <summary>删除实例：配置文件与实例数据目录 <c>instances\&lt;id&gt;\</c> 一并删除。</summary>
    public static bool Delete(GameInstance instance, out string? error)
    {
        error = null;

        // 先删数据目录：删不掉就整体放弃（配置还在，用户能从界面重试）。
        // 反过来先删配置的话，这一步失败就会留下一个界面上再也点不到、也没法清理的孤儿目录。
        try
        {
            if (Directory.Exists(instance.InstanceDirectory))
                Directory.Delete(instance.InstanceDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            error = Loc.F("实例数据目录删除失败：{0}", ex.Message);
            return false;
        }

        try
        {
            var file = Paths.InstanceFile(instance.Id);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            error = Loc.F("实例配置删除失败：{0}", ex.Message);
            return false;
        }

        Items.RemoveAll(item => string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));

        if (ReferenceEquals(Current, instance)) SetCurrent(Items.FirstOrDefault());
        else Changed?.Invoke();

        Log.Info(Loc.F("已删除实例「{0}」", instance.Name));
        return true;
    }

    public static void SetCurrent(GameInstance? instance)
    {
        if (ReferenceEquals(Current, instance)) return;

        Current = instance;
        SettingsStore.Current.LastInstanceId = instance?.Id ?? string.Empty;
        SettingsStore.Save();

        Changed?.Invoke();
        Log.Info(Loc.F("当前实例切换为：{0}", instance?.Name ?? Loc.T("无")));
    }
}
