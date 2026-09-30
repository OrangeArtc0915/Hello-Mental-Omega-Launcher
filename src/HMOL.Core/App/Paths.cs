using System.IO;

namespace HMOL.Core.App;

/// <summary>
/// 工具自身的数据目录。划分依据：可随程序带走 / 可随时重建 / 可随时删除。
/// 可通过环境变量 HMOL_DATA 重定向整个数据目录，便于便携部署与测试。
/// </summary>
public static class Paths
{
    public const string DataEnvVar = "HMOL_DATA";

    public static string ExecutableDirectory { get; private set; } = AppContext.BaseDirectory;

    public static string Data { get; private set; } = string.Empty;

    public static string Instances { get; private set; } = string.Empty;

    public static string Packages { get; private set; } = string.Empty;

    public static string Cache { get; private set; } = string.Empty;

    /// <summary>主页背景素材目录。导入的图片 / GIF / 视频都复制到这里。</summary>
    public static string Backgrounds { get; private set; } = string.Empty;

    /// <summary>自定义布局方案目录。</summary>
    public static string Layouts { get; private set; } = string.Empty;

    /// <summary>备份根目录：<c>backup\MO</c> 放原版备份，<c>backup\game\&lt;名称&gt;</c> 放用户备份。</summary>
    public static string Backup { get; private set; } = string.Empty;

    public static string Log { get; private set; } = string.Empty;

    public static string Temp { get; private set; } = string.Empty;

    public static string Downloads { get; private set; } = string.Empty;

    public static string SettingsFile => Path.Combine(Data, "Settings.json");

    public static bool IsInitialized { get; private set; }

    public static void Init()
    {
        if (IsInitialized) return;

        var custom = Environment.GetEnvironmentVariable(DataEnvVar);
        Data = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(ExecutableDirectory, "Data")
            : Path.GetFullPath(custom);

        Instances = Path.Combine(Data, "instances");
        Packages = Path.Combine(Data, "packages");
        Cache = Path.Combine(Data, "Cache");
        Backgrounds = Path.Combine(Data, "backgrounds");
        Layouts = Path.Combine(Data, "layouts");
        Backup = Path.Combine(Data, "backup");
        Log = Path.Combine(Data, "Log");
        Temp = Path.Combine(Path.GetTempPath(), "HMOL");
        Downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HMOL", "Downloads");

        foreach (var dir in new[] { Data, Instances, Packages, Cache, Backgrounds, Layouts, Backup, Log, Temp, Downloads })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* 目录不可用时由上层在使用点报错，这里不阻断启动 */ }
        }

        IsInitialized = true;
    }

    public static string InstanceFile(string id) => Path.Combine(Instances, id + ".json");

    public static string InstanceDir(string id) => Path.Combine(Instances, id);
}
