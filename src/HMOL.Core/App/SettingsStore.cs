using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.Logging;

namespace HMOL.Core.App;

/// <summary>设置的读取与保存。解析失败时回退为默认值并保留坏文件备份。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 不转义中文，保证用户手工编辑配置文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Current { get; private set; } = new();

    public static void Load()
    {
        var file = Paths.SettingsFile;
        if (!File.Exists(file))
        {
            Current = new Settings();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(file);
            Current = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warn($"设置文件解析失败，将重建为默认值：{ex.Message}");
            TryBackupBadFile(file);
            Current = new Settings();
            Save();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            Log.Error("设置保存失败", ex);
        }
    }

    private static void TryBackupBadFile(string file)
    {
        try { File.Move(file, file + ".failed", overwrite: true); }
        catch { }
    }
}
