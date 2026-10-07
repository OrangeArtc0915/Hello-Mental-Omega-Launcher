using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 联机配置的读取与保存，落盘到 <c>Data\multiplayer.json</c>（对应旧版 config.py 的
/// <c>hmol-LJ-DLC_config.json</c>，但改放在程序数据目录而不是 exe 旁边）。
/// 解析失败时回退为默认值并保留坏文件备份，风格与 <see cref="SettingsStore"/> 一致。
/// </summary>
public static class MultiplayerSettingsStore
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

    public static MultiplayerSettings Current { get; private set; } = new();

    /// <summary>配置文件路径：<c>Data\multiplayer.json</c>。</summary>
    public static string FilePath => Path.Combine(Paths.Data, "multiplayer.json");

    public static void Load()
    {
        Paths.Init();

        if (!File.Exists(FilePath))
        {
            Current = new MultiplayerSettings();
            Save();
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<MultiplayerSettings>(json, Options) ?? new MultiplayerSettings();
            Current.CustomNodes ??= [];
            Current.Friends ??= [];
            Migrate();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("联机配置解析失败，将重建为默认值：{0}", ex.Message));
            TryBackupBadFile(FilePath);
            Current = new MultiplayerSettings();
            Save();
        }
    }

    public static void Save()
    {
        Paths.Init();

        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("联机配置保存失败"), ex);
        }
    }

    /// <summary>改一项就立刻落盘，避免程序被强杀时丢配置（旧版 Config.set 是同样行为）。</summary>
    public static void Update(Action<MultiplayerSettings> change)
    {
        change(Current);
        Save();
    }

    /// <summary>
    /// 旧配置迁移：旧版默认节点 <c>tcp://39.108.52.138:11010</c> 的 TCP 11010 已被防火墙拦截，
    /// 实测只有同主机 UDP 可用，配置里仍是 TCP 时自动换成 UDP（与旧版 config.py:69-73 一致）。
    /// </summary>
    private static void Migrate()
    {
        if (Current.EasyTierNode == "tcp://39.108.52.138:11010")
        {
            Current.EasyTierNode = "udp://39.108.52.138:11010";
            Log.Info(Loc.T("联机配置迁移：EasyTier 默认节点 TCP 已不可用，自动切换为 udp://39.108.52.138:11010"));
            Save();
        }
    }

    private static void TryBackupBadFile(string file)
    {
        try { File.Move(file, file + ".failed", overwrite: true); }
        catch { }
    }
}
