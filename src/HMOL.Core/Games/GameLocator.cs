using System.IO;

namespace HMOL.Core.Games;

/// <summary>
/// 心灵终结（Mental Omega）游戏目录的判定与主程序查找。
/// 目录判定规则完全按旧版 <c>is_mo_directory</c> 实现，四条满足任一即有效。
/// </summary>
public static class GameLocator
{
    /// <summary>识别目录用的特征可执行文件名（旧版 is_mo_directory 用的就是这两个）。</summary>
    private static readonly string[] MarkerExecutables =
    [
        "MentalOmegaClient.exe",
        "Mental Omega.exe"
    ];

    /// <summary>游戏根目录下 MO 本体的固定子目录名。</summary>
    public const string MentalOmegaFolderName = "Mental_Omega";

    public static IReadOnlyList<string> LaunchExecutableNames => LaunchExecutablesByKind[GameKind.MentalOmega];

    /// <summary>
    /// 各类型游戏按顺序查找的主程序文件名。
    /// 心灵终结沿用旧版 <c>_launch_game</c> 的顺序（注意与识别用的文件名不同）；
    /// 原版 / 尤复按各自的引擎主程序名；其它 Mod 不给默认值，必须由用户指定。
    /// </summary>
    private static readonly Dictionary<GameKind, string[]> LaunchExecutablesByKind = new()
    {
        [GameKind.MentalOmega] =
        [
            "Mental_Omega_client.exe",
            "MentalOmegaClient.exe",
            "Mental_Omega.exe"
        ],
        [GameKind.OriginalRa2] =
        [
            "ra2.exe",
            "gamemd.exe"
        ],
        [GameKind.YurisRevenge] =
        [
            "ra2md.exe",
            "gamemd.exe"
        ],
        [GameKind.Other] = []
    };

    /// <summary>某类型游戏默认的主程序文件名（按优先级），没有默认为空。</summary>
    public static IReadOnlyList<string> DefaultExecutables(GameKind kind)
        => LaunchExecutablesByKind.TryGetValue(kind, out var names) ? names : [];

    /// <summary>
    /// 是否有效的心灵终结游戏目录。判定规则（旧版 9761-9800）：
    /// 1) 目录下存在 Mental_Omega 子目录；
    /// 2) 路径本身就是 Mental_Omega 目录；
    /// 3) 目录下存在 MentalOmegaClient.exe 或 "Mental Omega.exe"；
    /// 4) 父目录下存在上述特征可执行文件（允许用户选中安装根的子目录）。
    /// </summary>
    public static bool IsMoDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        // 1) 存在 Mental_Omega 子目录
        if (Directory.Exists(Path.Combine(full, MentalOmegaFolderName))) return true;

        // 2) 路径本身即为 Mental_Omega
        if (string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                MentalOmegaFolderName, StringComparison.OrdinalIgnoreCase))
            return true;

        // 3) 目录下存在特征可执行文件
        if (HasMarkerExecutable(full)) return true;

        // 4) 父目录中存在特征可执行文件
        var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrEmpty(parent) && HasMarkerExecutable(parent);
    }

    /// <summary>按旧版顺序返回心灵终结主程序路径，找不到返回 null。</summary>
    public static string? FindExecutable(string? directory) => FindExecutable(directory, GameKind.MentalOmega);

    /// <summary>按指定类型的主程序名顺序在目录里查找，找不到返回 null。</summary>
    public static string? FindExecutable(string? directory, GameKind kind)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        foreach (var name in DefaultExecutables(kind))
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>
    /// 把用户指定的主程序解析成绝对路径。允许写绝对路径，或写相对游戏目录的文件名；
    /// 找不到文件返回 null。用于「其它红警 Mod」这类需要手工指定主程序的情况。
    /// </summary>
    public static string? ResolveExecutable(string? directory, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        var value = executable.Trim().Trim('"');
        if (value.Length == 0) return null;

        try
        {
            var path = Path.IsPathRooted(value)
                ? value
                : Path.Combine(directory ?? string.Empty, value);

            path = Path.GetFullPath(path);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 按类型判断目录是否是有效的游戏目录。用户手工指定了主程序（且文件存在）时一律算有效，
    /// 这样其它红警 Mod 的目录不必符合心灵终结的识别规则。
    /// </summary>
    public static bool IsGameDirectory(string? path, GameKind kind, string? executable = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

        if (ResolveExecutable(path, executable) is not null) return true;

        return kind switch
        {
            GameKind.MentalOmega => IsMoDirectory(path),
            // 其它 Mod 没有可用的默认主程序名，只能靠用户指定
            GameKind.Other => false,
            _ => FindExecutable(path, kind) is not null
        };
    }

    /// <summary>统一为不带尾部分隔符的完整路径（旧版路径比较前会做 abspath）。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var normalized = path.Replace('/', Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        try
        {
            return Path.GetFullPath(normalized);
        }
        catch
        {
            return normalized;
        }
    }

    /// <summary>两个路径是否指向同一个目录（忽略大小写与尾部斜杠）。</summary>
    public static bool IsSamePath(string? left, string? right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool HasMarkerExecutable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;

        return MarkerExecutables.Any(name => File.Exists(Path.Combine(directory, name)));
    }
}
