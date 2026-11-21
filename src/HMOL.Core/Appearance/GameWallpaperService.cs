using System.IO;
using System.Text;
using HMOL.Core.Localization;
using HMOL.Core.Logging;

namespace HMOL.Core.Appearance;

/// <summary>应用游戏壁纸的结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record GameWallpaperApplyResult(bool Ok, string Message, string ThemeName, string ThemeDirectory);

/// <summary>
/// 把壁纸做成 MO 局域网补丁（MoLanRelay）识别的游戏主题。
///
/// 主题目录是 <c>&lt;游戏根&gt;\Resources\MoThemes\&lt;主题名&gt;\</c>，里面放 bg.png 与 theme.ini；
/// 同时在 <c>&lt;游戏根&gt;\Resources\MoLanPatch.ini</c> 里把 Theme 指向该主题。
/// 补丁下次启动时会自己生成 13 张背景、玻璃按钮与控件配色，启动器这边只负责素材与两种颜色。
///
/// 图像解码 / 视频取帧 / 取色都依赖 WPF，放在 App 层做（GameWallpaperComposer），
/// 这里只接收已经裁切好的 PNG 字节，保持 Core 不引用 WPF。
/// </summary>
public static class GameWallpaperService
{
    /// <summary>主题名前缀，便于用户和补丁把自动生成的主题与手写主题区分开。</summary>
    public const string ThemePrefix = "HMOL-";

    /// <summary>主题名长度上限（含前缀），留足 Windows 路径长度余量。</summary>
    private const int MaxThemeNameLength = 40;

    private const string PatchDllName = "MoLanRelay.dll";
    private const string PatchIniName = "MoLanPatch.ini";
    private const string ThemesFolderName = "MoThemes";

    /// <summary>主题目录里的「待应用」标记文件名，补丁启动时认领一次后删除。</summary>
    private const string PendingMarkerName = ".pending";

    /// <summary>游戏资源目录：<c>&lt;游戏根&gt;\Resources</c>。</summary>
    public static string ResourcesDirectory(string gameDir) => Path.Combine(gameDir, "Resources");

    /// <summary>主题根目录：<c>&lt;游戏根&gt;\Resources\MoThemes</c>。</summary>
    public static string ThemesDirectory(string gameDir)
        => Path.Combine(ResourcesDirectory(gameDir), ThemesFolderName);

    /// <summary>补丁设置文件：<c>&lt;游戏根&gt;\Resources\MoLanPatch.ini</c>。</summary>
    public static string PatchIniPath(string gameDir)
        => Path.Combine(ResourcesDirectory(gameDir), PatchIniName);

    /// <summary>
    /// 当前游戏实例有没有安装 MO 局域网补丁：
    /// <c>Resources\MoLanRelay.dll</c> 或 <c>Resources\Binaries\MoLanRelay.dll</c> 存在任意一个即可。
    /// </summary>
    public static bool IsPatchInstalled(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir)) return false;

        try
        {
            var resources = ResourcesDirectory(gameDir);

            return File.Exists(Path.Combine(resources, PatchDllName)) ||
                   File.Exists(Path.Combine(resources, "Binaries", PatchDllName));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("检查 MO 局域网补丁失败：{0}", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// 由壁纸标题生成主题名：<c>HMOL-&lt;壁纸名&gt;</c>，过滤 Windows 非法文件名字符，长度不超过 40。
    /// 反过来用这个名字当目录名，所以这里也是防止路径穿越的唯一入口。
    /// </summary>
    public static string BuildThemeName(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var raw = string.IsNullOrWhiteSpace(title) ? Loc.T("壁纸") : title.Trim();

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);

        var safe = builder.ToString().Trim().Trim(' ', '.');
        if (safe.Length == 0) safe = Loc.T("壁纸");

        var maxStem = MaxThemeNameLength - ThemePrefix.Length;
        if (safe.Length > maxStem) safe = safe[..maxStem].TrimEnd(' ', '.');

        return ThemePrefix + safe;
    }

    /// <summary>
    /// 写入主题：覆盖 bg.png / theme.ini，并把 MoLanPatch.ini 的 Theme 改成该主题名。
    /// 同一标题重复应用会原地覆盖，不会产生一堆带时间戳的目录。
    /// </summary>
    /// <param name="gameDir">游戏根目录。</param>
    /// <param name="title">壁纸标题，用来生成主题名。</param>
    /// <param name="backgroundPng">已经 cover 裁切到 1920×1080 的 PNG 字节。</param>
    /// <param name="accentHex">主色，6 位十六进制（不带 #）。</param>
    /// <param name="textHex">文字色，6 位十六进制（不带 #）。</param>
    /// <param name="animPath">
    /// 动态壁纸的源文件绝对路径（GIF 或 mp4/wmv/avi）；不复制文件，只记进
    /// theme.ini 的 Anim 行，由补丁在客户端主菜单和大厅里逐帧播放。静态图片传 null。
    /// </param>
    public static GameWallpaperApplyResult Apply(
        string gameDir, string? title, byte[] backgroundPng, string accentHex, string textHex,
        string? animPath = null)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return Fail(Loc.T("游戏目录不可用，无法应用游戏壁纸。"));

        if (backgroundPng is null || backgroundPng.Length == 0)
            return Fail(Loc.T("没有可写入的壁纸图片。"));

        if (!IsPatchInstalled(gameDir))
            return Fail(Loc.T("当前游戏实例没有安装 MO 局域网补丁，无法应用游戏壁纸。"));

        var themeName = BuildThemeName(title);
        var themeDirectory = Path.Combine(ThemesDirectory(gameDir), themeName);

        try
        {
            Directory.CreateDirectory(themeDirectory);

            // 主题里只该有一张背景图：清掉上一版手放的 bg.jpg，避免补丁在两份图之间挑错
            var staleJpg = Path.Combine(themeDirectory, "bg.jpg");
            if (File.Exists(staleJpg)) File.Delete(staleJpg);

            File.WriteAllBytes(Path.Combine(themeDirectory, "bg.png"), backgroundPng);

            var accent = NormalizeHex(accentHex, "3D8BFD");
            var text = NormalizeHex(textHex, accent);

            // theme.ini 只有两三个键，补丁按普通 ini 读取，用无 BOM 的 UTF-8 最稳
            var ini = new StringBuilder();
            ini.Append("Accent=").Append(accent).Append("\r\n");
            ini.Append("Text=").Append(text).Append("\r\n");

            // 动图 / 视频：文件不复制，补丁按这个路径播放；换了或删了会自动退回静态图
            var hasAnim = !string.IsNullOrWhiteSpace(animPath) && File.Exists(animPath);
            if (hasAnim)
                ini.Append("Anim=").Append(animPath!.Trim()).Append("\r\n");

            File.WriteAllText(
                Path.Combine(themeDirectory, "theme.ini"),
                ini.ToString(),
                new UTF8Encoding(false));

            SetThemeInPatchIni(PatchIniPath(gameDir), themeName);

            // 补丁只在「设置里点应用」时才重写游戏贴图，外部改 Theme 不会自动生效。
            // 留一个标记，补丁下次启动第一次进主菜单时自己套用一遍然后删掉它。
            File.WriteAllText(Path.Combine(themeDirectory, PendingMarkerName), "1", new UTF8Encoding(false));

            Log.Info(Loc.F("游戏壁纸已应用：{0} → {1}", themeName, themeDirectory));

            return new GameWallpaperApplyResult(
                true,
                hasAnim
                    ? Loc.F("已应用动态壁纸「{0}」，重启客户端后主菜单和大厅会动起来。", themeName)
                    : Loc.F("已应用游戏壁纸主题「{0}」，下次启动客户端即生效。", themeName),
                themeName,
                themeDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("应用游戏壁纸失败：{0}", themeName), ex);
            return Fail(Loc.F("应用游戏壁纸失败：{0}", ex.Message));
        }

        static GameWallpaperApplyResult Fail(string message)
            => new(false, message, string.Empty, string.Empty);
    }

    /// <summary>
    /// 只改 MoLanPatch.ini 里的 Theme 行，其它行与换行风格原样保留；文件不存在就新建。
    /// 文件可能带 UTF-8 BOM，读的时候跳过、写回去再按原样补上，免得补丁把首行认错。
    /// </summary>
    private static void SetThemeInPatchIni(string path, string themeName)
    {
        var hasBom = false;
        var text = string.Empty;

        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);

            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var offset = hasBom ? 3 : 0;

            text = new UTF8Encoding(false, false).GetString(bytes, offset, bytes.Length - offset);
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var separator = lines[i].IndexOf('=');
            if (separator <= 0) continue;

            if (!lines[i][..separator].Trim().Equals("Theme", StringComparison.OrdinalIgnoreCase)) continue;

            // 保留原有的缩进与键名大小写，只换等号后面的值
            lines[i] = lines[i][..(separator + 1)] + themeName;
            replaced = true;
            break;
        }

        if (!replaced)
        {
            var entry = "Theme=" + themeName;

            // 文件末尾本来有换行时，插在最后那个空行前面，避免多出一大段空行
            if (lines.Count > 0 && lines[^1].Length == 0) lines.Insert(lines.Count - 1, entry);
            else lines.Add(entry);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, string.Join(newline, lines), new UTF8Encoding(hasBom));
    }

    /// <summary>把颜色规范成不带 # 的 6 位大写十六进制；不合法时用兜底值。</summary>
    private static string NormalizeHex(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('#');

        return text.Length == 6 && text.All(Uri.IsHexDigit)
            ? text.ToUpperInvariant()
            : fallback;
    }
}
