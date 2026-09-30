using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Logging;

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
                foreach (var file in Directory.EnumerateFiles(Paths.Instances, "*.json"))
                {
                    var instance = ReadFrom(file, out var error);

                    if (instance is null)
                    {
                        Log.Warn($"实例文件解析失败，已跳过：{Path.GetFileName(file)}（{error}）");
                        continue;
                    }

                    Items.Add(instance);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取实例列表失败", ex);
        }

        var lastId = SettingsStore.Current.LastInstanceId;
        Current = Items.FirstOrDefault(item => string.Equals(item.Id, lastId, StringComparison.OrdinalIgnoreCase))
                  ?? Items.FirstOrDefault();

        Log.Info($"已载入 {Items.Count} 个实例，当前实例：{Current?.Name ?? "无"}");
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
                error = "文件不存在";
                return null;
            }

            var instance = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(filePath), Options);
            if (instance is null)
            {
                error = "内容为空";
                return null;
            }

            if (string.IsNullOrWhiteSpace(instance.Id))
            {
                error = "缺少实例 Id";
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
            Log.Error($"实例保存失败：{instance.Name}", ex);
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

        Items.RemoveAll(item => string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase));

        try
        {
            var file = Paths.InstanceFile(instance.Id);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            error = $"实例配置删除失败：{ex.Message}";
        }

        try
        {
            if (Directory.Exists(instance.InstanceDirectory))
                Directory.Delete(instance.InstanceDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            error = error is null ? $"实例数据目录删除失败：{ex.Message}" : error;
        }

        if (ReferenceEquals(Current, instance)) SetCurrent(Items.FirstOrDefault());
        else Changed?.Invoke();

        if (error is null) Log.Info($"已删除实例「{instance.Name}」");
        return error is null;
    }

    public static void SetCurrent(GameInstance? instance)
    {
        if (ReferenceEquals(Current, instance)) return;

        Current = instance;
        SettingsStore.Current.LastInstanceId = instance?.Id ?? string.Empty;
        SettingsStore.Save();

        Changed?.Invoke();
        Log.Info($"当前实例切换为：{instance?.Name ?? "无"}");
    }
}
