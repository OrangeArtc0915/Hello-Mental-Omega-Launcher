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

    /// <summary>个性化素材目录：自定义窗口图标与启动 / 关闭音效都放这里。</summary>
    public static string Custom { get; private set; } = string.Empty;

    /// <summary>自定义布局方案目录。</summary>
    public static string Layouts { get; private set; } = string.Empty;

    /// <summary>备份根目录：<c>backup\MO</c> 放原版备份，<c>backup\game\&lt;名称&gt;</c> 放用户备份。</summary>
    public static string Backup { get; private set; } = string.Empty;

    public static string Log { get; private set; } = string.Empty;

    public static string Temp { get; private set; } = string.Empty;

    public static string Downloads { get; private set; } = string.Empty;

    public static string SettingsFile => Path.Combine(Data, "Settings.json");

    /// <summary>同分区暂存根目录名。放在操作目标所在分区，用完即清。</summary>
    public const string ScratchDirectoryName = ".hmol-scratch";

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
        Custom = Path.Combine(Data, "custom");
        Layouts = Path.Combine(Data, "layouts");
        Backup = Path.Combine(Data, "backup");
        Log = Path.Combine(Data, "Log");
        Temp = Path.Combine(Path.GetTempPath(), "HMOL");
        Downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HMOL", "Downloads");

        foreach (var dir in new[] { Data, Instances, Packages, Cache, Backgrounds, Custom, Layouts, Backup, Log, Temp, Downloads })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* 目录不可用时由上层在使用点报错，这里不阻断启动 */ }
        }

        IsInitialized = true;
    }

    public static string InstanceFile(string id) => Path.Combine(Instances, id + ".json");

    public static string InstanceDir(string id) => Path.Combine(Instances, id);

    /// <summary>
    /// 给一次大操作在 <paramref name="anchorPath"/> 所在分区上找一个暂存根目录。
    /// 默认的 <see cref="Temp"/> 在系统盘：把 GB 级的解压内容先写到 C: 再复制到游戏盘，
    /// 等于白写一遍、还容易把系统盘写满；更糟的是「隔离区」跨盘时 <c>Directory.Move</c> 会失败并
    /// 退化成整目录复制。锚定到目标同分区的同级目录后，这些移动都退化成瞬间的改名。
    /// 取不到可写目录时退回 <see cref="Temp"/>（功能不受影响，只是慢）。
    /// </summary>
    public static string ScratchFor(string anchorPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(anchorPath)) return Temp;

            var full = Path.GetFullPath(anchorPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 从「锚点的上一级」往上找第一个真实存在的目录，避免把暂存目录建到被操作目录内部
            var parent = Path.GetDirectoryName(full);

            while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                parent = Path.GetDirectoryName(parent);

            if (string.IsNullOrEmpty(parent)) return Temp;

            var scratch = Path.Combine(parent, ScratchDirectoryName);
            Directory.CreateDirectory(scratch);

            // 能建目录不代表能写（只读盘、权限受限），真写一个探针文件确认
            var probe = Path.Combine(scratch, $".probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);

            return scratch;
        }
        catch
        {
            return Temp;
        }
    }

    /// <summary>
    /// 操作结束后收拾同分区的暂存根：空的就删掉，别在用户的游戏目录旁边留一个看不懂的空文件夹。
    /// 里面还有东西（或回退到了 <see cref="Temp"/>）时保持不动。
    /// </summary>
    public static void CleanupScratch(string? scratch)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(scratch)) return;
            if (string.Equals(scratch, Temp, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(scratch)) return;
            if (Directory.EnumerateFileSystemEntries(scratch).Any()) return;

            Directory.Delete(scratch);
        }
        catch
        {
            // 收拾不掉也无妨，只是个空目录
        }
    }
}
