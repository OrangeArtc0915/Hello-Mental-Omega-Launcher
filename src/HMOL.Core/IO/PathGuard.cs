using System.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.IO;

/// <summary>
/// 路径安全校验。所有写操作的落点都必须先经过这里：
/// 拒绝绝对路径、盘符、".." 回溯，以及解析后落到预期目录之外的路径（防路径穿越）。
/// 与参考项目 <c>PlanInstaller</c> 的做法一致。
/// </summary>
public static class PathGuard
{
    /// <summary>把相对路径规整成不带首尾斜杠、"\" 统一为 "/" 的形式；"." 与空串都表示当前目录。</summary>
    public static string NormalizeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return string.Empty;

        var text = relative.Trim().Replace('\\', '/').Trim('/');
        return text == "." ? string.Empty : text;
    }

    /// <summary>把目录规整为绝对路径；无法解析时返回空串。</summary>
    public static string NormalizeRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;

        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception ex)
        {
            Log.Warn($"路径无法解析：{directory}（{ex.Message}）");
            return string.Empty;
        }
    }

    /// <summary>path 是否等于 root 或位于 root 之内。</summary>
    public static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;

        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)) return true;

        return trimmedPath.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把相对路径解析成 baseDirectory 下的绝对路径，并确认落点仍在 baseDirectory 内。</summary>
    public static bool TryResolve(string baseDirectory, string? relative, out string full)
    {
        full = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return false;

            var root = Path.GetFullPath(baseDirectory);
            var text = NormalizeRelative(relative);

            if (text.Length == 0)
            {
                full = root;
                return true;
            }

            if (Path.IsPathRooted(relative ?? string.Empty)) return false;

            foreach (var segment in text.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;

                if (segment == "..") return false;
                if (segment.Contains(':')) return false;
            }

            var combined = Path.GetFullPath(Path.Combine(root, text.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root, combined)) return false;

            full = combined;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"解析路径失败：{relative}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>求 file 相对 root 的相对路径，统一用 "/" 分隔。</summary>
    public static string RelativeOf(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(file);
        }
    }

    /// <summary>两个路径是否落在同一个分区。同分区时移动文件/目录只是改名，瞬间完成、不搬字节。</summary>
    public static bool SameVolume(string left, string right)
    {
        try
        {
            var a = Path.GetPathRoot(Path.GetFullPath(left));
            var b = Path.GetPathRoot(Path.GetFullPath(right));
            return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把名称里 Windows 不允许的字符替换为 "_"（用于实例名、备份名、包名做目录/文件名）。</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var chars = name.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0) chars[i] = '_';
        }

        return new string(chars);
    }
}
