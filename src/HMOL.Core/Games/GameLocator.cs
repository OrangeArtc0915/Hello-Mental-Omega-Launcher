using System.Diagnostics;
using System.IO;

namespace HMOL.Core.Games;

/// <summary>自动检测到的一个候选游戏目录。只作建议，由界面提示用户是否使用。</summary>
/// <param name="Directory">候选的游戏根目录（已规范化）。</param>
/// <param name="HasExecutable">该目录下能否直接找到游戏主程序。</param>
public sealed record GameDirectoryCandidate(string Directory, bool HasExecutable);

/// <summary>
/// 心灵终结（Mental Omega）游戏目录的探测。
/// 目录判定规则完全按旧版 <c>is_mo_directory</c> 实现，四条满足任一即有效。
///
/// 设置项「启动时自动检测游戏路径」(auto_detect_path) 在旧版只有开关、没有实现，
/// 新版在这里补上真实现：<see cref="FindCandidates"/> 按「固定盘符 + 常见目录名」做受限扫描，
/// 由界面在后台调用（限制扫描深度 / 目录数 / 耗时，跳过分页与系统目录），
/// 只把结果当建议交给用户，绝不静默改写实例配置。
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

    /// <summary>
    /// 常见目录名：这些名字多半是用户放游戏的地方，扫描时优先从盘根下探一层命中，
    /// 避免大量无关目录被翻到（大小写不敏感）。
    /// </summary>
    private static readonly string[] CommonFolderNames =
    [
        "Mental Omega", "MentalOmega", "Mental_Omega", "MO", "Red Alert 2", "RedAlert2",
        "Games", "Game", "游戏", "单机游戏", "Steam", "SteamLibrary", "SteamLibrary\\steamapps\\common",
        "Program Files", "Program Files (x86)", "EA Games", "Westwood"
    ];

    /// <summary>
    /// 扫描时跳过的目录名：系统 / 分页 / 缓存目录，翻它们既慢又没有结果（大小写不敏感）。
    /// </summary>
    private static readonly string[] SkipFolderNames =
    [
        "windows", "windows.old", "programdata", "$recycle.bin", "system volume information",
        "recovery", "perflogs", "msocache", "onedrivetemp", "appdata", "node_modules", ".git",
        ".svn", ".vs", "obj", "bin", "packages", "temp", "tmp", "cache"
    ];

    /// <summary>扫描的默认耗时上限：到点就返回已找到的结果，绝不拖住启动。</summary>
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(8);

    /// <summary>扫描时最多访问的目录数上限：防御异常大的磁盘。</summary>
    private const int MaxVisitedDirectories = 30000;

    /// <summary>单个盘至少要分到的时间片：预算够时保证每个盘都被扫到，不让前面的盘吃光。</summary>
    private static readonly TimeSpan MinRootSlice = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// 自动检测可能的游戏目录。扫描范围：本机所有就绪的固定磁盘 + <paramref name="extraRoots"/>；
    /// 命中 <see cref="IsMoDirectory"/> 的目录即作为候选并停止向下递归。
    ///
    /// 性能约束：限制递归深度（<paramref name="maxDepth"/>）、目录总数与总耗时（<paramref name="budget"/>），
    /// 跳过隐藏 / 系统 / 缓存目录；本方法为同步阻塞实现，调用方应放到后台线程执行。
    /// </summary>
    public static IReadOnlyList<GameDirectoryCandidate> FindCandidates(
        TimeSpan? budget = null,
        int maxDepth = 3,
        IReadOnlyCollection<string>? extraRoots = null)
    {
        var results = new List<GameDirectoryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var limit = budget ?? DefaultBudget;
        var visited = 0;

        var roots = EnumerateRoots(extraRoots).ToList();

        for (var i = 0; i < roots.Count; i++)
        {
            if (watch.Elapsed >= limit || visited >= MaxVisitedDirectories) break;

            // 每个盘分一份时间片：保证靠后的盘（游戏常装在非系统盘）也能被扫到，不让 C 盘吃光预算
            var remaining = limit - watch.Elapsed;
            var slice = TimeSpan.FromTicks(Math.Max(remaining.Ticks / (roots.Count - i), MinRootSlice.Ticks));
            if (slice > remaining) slice = remaining;

            var deadline = watch.Elapsed + slice;
            var root = roots[i];

            // 该盘先探常见目录名（便宜且命中率高）
            foreach (var folder in CommonFolderNames)
            {
                if (watch.Elapsed >= deadline || visited >= MaxVisitedDirectories) break;

                var candidate = CombineSafe(root, folder);
                if (candidate is null) continue;

                if (TryAccept(candidate, results, seen)) continue;

                // 常见目录下再下探一层（如 ...\Games\Mental Omega）
                if (maxDepth >= 2) VisitChildren(candidate, 2, maxDepth, results, seen, ref visited, watch, deadline);
            }

            // 再按受限广度优先扫一遍该盘盘根，兜住不按常见命名摆放的情况
            if (watch.Elapsed < deadline && visited < MaxVisitedDirectories)
                VisitChildren(root, 1, maxDepth, results, seen, ref visited, watch, deadline);
        }

        return results;
    }

    private static IEnumerable<string> EnumerateRoots(IReadOnlyCollection<string>? extraRoots)
    {
        var roots = new List<string>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    // 只扫固定磁盘：网络盘 / 光驱 / 可移动盘要么慢要么未必就绪
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    roots.Add(drive.RootDirectory.FullName);
                }
                catch
                {
                    // 单个盘读不到信息就跳过
                }
            }
        }
        catch
        {
            // 枚举磁盘失败时不阻断，至少还能用 extraRoots
        }

        if (extraRoots is not null)
        {
            foreach (var root in extraRoots)
            {
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) roots.Add(root);
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>广度优先下探到 <paramref name="maxDepth"/> 层；<paramref name="deadline"/> 是本轮的时间片上限（当前已耗时）。</summary>
    private static void VisitChildren(
        string start,
        int startDepth,
        int maxDepth,
        List<GameDirectoryCandidate> results,
        HashSet<string> seen,
        ref int visited,
        Stopwatch watch,
        TimeSpan deadline)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((start, startDepth));

        while (queue.Count > 0)
        {
            if (watch.Elapsed >= deadline || visited >= MaxVisitedDirectories) return;

            var (dir, depth) = queue.Dequeue();
            visited++;

            if (TryAccept(dir, results, seen)) continue;   // 命中就不再往下钻，避免出现嵌套候选

            if (depth >= maxDepth) continue;

            foreach (var child in SafeEnumerateDirectories(dir))
            {
                if (watch.Elapsed >= deadline || visited >= MaxVisitedDirectories) return;
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    /// <summary>命中判定：有效目录则记为候选并返回 true。</summary>
    private static bool TryAccept(string directory, List<GameDirectoryCandidate> results, HashSet<string> seen)
    {
        if (!seen.Add(directory) || !IsMoDirectory(directory)) return false;

        results.Add(new GameDirectoryCandidate(NormalizePath(directory), FindExecutable(directory) is not null));
        return true;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        // 先整体取到列表再返回：迭代器里不能 yield return 于带 catch 的 try 块中
        var children = new List<string>();

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                // 隐藏 / 系统目录（含回收站、卷影信息）直接跳过
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };

            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                var name = Path.GetFileName(child);
                if (SkipFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                children.Add(child);
            }
        }
        catch
        {
            // 单个目录枚举失败就跳过，不影响整体扫描
        }

        return children;
    }

    private static string? CombineSafe(string root, string relative)
    {
        try
        {
            var path = Path.Combine(root, relative);
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
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
