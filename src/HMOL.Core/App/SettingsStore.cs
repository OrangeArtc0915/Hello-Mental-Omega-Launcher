using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

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
            Log.Warn(Loc.F("设置文件解析失败，将重建为默认值：{0}", ex.Message));
            TryBackupBadFile(file);
            Current = new Settings();
            Save();
        }
    }

    /// <summary>保存设置：先写同目录临时文件再整体替换，写一半崩溃也不会把原配置弄丢。</summary>
    public static void Save()
    {
        lock (SaveLock)
        {
            var file = Paths.SettingsFile;
            var temp = file + ".tmp";

            try
            {
                var directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));

                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            catch (Exception ex)
            {
                Log.Error(Loc.T("设置保存失败"), ex);

                // 临时文件留着没意义，能删就删；删不掉也不影响下次启动
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>设置可能被界面线程和后台任务同时保存，写文件要串行。</summary>
    private static readonly object SaveLock = new();

    private static void TryBackupBadFile(string file)
    {
        try { File.Move(file, file + ".failed", overwrite: true); }
        catch { }
    }
}
