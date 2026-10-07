using System.IO;
using System.Text;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// CnCNet 客户端的玩家名（Handle）。客户端把设置写在游戏目录里、由
/// <c>Resources\ClientDefinitions.ini</c> 的 <c>SettingsFile</c> 指定的文件中
/// （Mental Omega 是 <c>RA2MO.ini</c>），段落固定 <c>[MultiPlayer]</c>，键名 <c>Handle</c>，
/// 长度上限取自同文件的 <c>MaxNameLength</c>（MO 为 14）。
///
/// <para>
/// HMOL 的「联机昵称」与它保持同一个值：读的时候以游戏里的为准，改的时候两边一起写，
/// 这样在启动器里改昵称就等于改 CnCNet 玩家名，不用再进客户端改一次。
/// </para>
/// </summary>
public static class CnCNetProfile
{
    private const string Section = "MultiPlayer";
    private const string HandleKey = "Handle";

    /// <summary>读不到 ClientDefinitions.ini 时的兜底长度上限。</summary>
    private const int FallbackMaxNameLength = 14;

    /// <summary>候选设置文件名：拿不到 ClientDefinitions.ini 时按这个顺序找。</summary>
    private static readonly string[] FallbackFiles = ["RA2MO.ini", "RA2MD.ini", "RA2.ini"];

    /// <summary>客户端允许的玩家名长度；MO 为 14。</summary>
    public static int MaxNameLength(string? gameDir)
    {
        var value = ReadIniValue(ClientDefinitionsPath(gameDir), "Settings", "MaxNameLength");

        return int.TryParse(value, out var length) && length > 0 ? length : FallbackMaxNameLength;
    }

    /// <summary>玩家名所在的设置文件（绝对路径）；找不到返回 null。</summary>
    public static string? SettingsFileOf(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;

        // 优先按 ClientDefinitions.ini 的 SettingsFile 找，找不到再按常见文件名兜底
        var configured = ReadIniValue(ClientDefinitionsPath(gameDir), "Settings", "SettingsFile");

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.Combine(gameDir, configured.Trim());
            if (File.Exists(path)) return path;
        }

        foreach (var name in FallbackFiles)
        {
            var path = Path.Combine(gameDir, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>读玩家名；没有设置文件或没写 Handle 时返回 null。</summary>
    public static string? ReadHandle(string? gameDir)
    {
        var path = SettingsFileOf(gameDir);
        if (path is null) return null;

        var value = ReadIniValue(path, Section, HandleKey);

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>把玩家名写回客户端设置文件；成功返回 null，失败返回可直接展示的原因。</summary>
    public static string? WriteHandle(string? gameDir, string handle)
    {
        var path = SettingsFileOf(gameDir);
        if (path is null) return Loc.T("没找到 CnCNet 客户端的设置文件（如 RA2MO.ini）");

        var name = ChatCrypt.SanitizeText(handle, MaxNameLength(gameDir));
        if (name.Length == 0) return Loc.T("玩家名不能为空");

        try
        {
            WriteIniValue(path, Section, HandleKey, name);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("写入 CnCNet 玩家名失败：{0}", ex.Message));
            return ex.Message;
        }
    }

    private static string ClientDefinitionsPath(string? gameDir)
        => string.IsNullOrWhiteSpace(gameDir)
            ? string.Empty
            : Path.Combine(gameDir, "Resources", "ClientDefinitions.ini");

    /// <summary>极简 INI 取值：只看目标段下的 <c>key=value</c>，注释与空行跳过。</summary>
    private static string? ReadIniValue(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var inSection = false;

            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    inSection = string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                if (string.Equals(line[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line[(equals + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取 INI 失败：{0}（{1}）", path, ex.Message));
        }

        return null;
    }

    /// <summary>
    /// 只改一个键、其余行原样保留：目标段存在就改它或补在段末，段不存在就在文件末尾补一个段。
    /// 行尾统一按 CRLF 写回（RA2 系 INI 一直是 CRLF）。
    /// </summary>
    private static void WriteIniValue(string path, string section, string key, string value)
    {
        var lines = new List<string>();

        if (File.Exists(path))
        {
            lines.AddRange(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n'));

            // 末尾换行会切出一个空串，先摘掉、写完再补，避免多出空行
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        }

        var start = -1;
        var end = lines.Count;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] != '[' || line[^1] != ']') continue;

            if (start < 0 && string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                continue;
            }

            if (start >= 0)
            {
                end = i;
                break;
            }
        }

        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            var keyLine = -1;

            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i].TrimStart();
                if (line.Length == 0 || line[0] is ';' or '#') continue;

                var equals = line.IndexOf('=');
                if (equals <= 0) continue;

                if (string.Equals(line[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    keyLine = i;
                    break;
                }
            }

            if (keyLine >= 0) lines[keyLine] = $"{key}={value}";
            else lines.Insert(end, $"{key}={value}");
        }

        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }
}
