using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Appearance;

/// <summary>背景素材导入结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record BackgroundImport(bool Ok, string Message, BackgroundKind Kind, string FileName);

/// <summary>
/// 主页背景素材的管理：把用户选中的文件收进 <see cref="Paths.Backgrounds"/>，并按需清掉不再使用的素材。
/// 只负责文件，界面怎么显示由 App 层的 BackgroundLayer 决定。
///
/// 为什么一定要复制：用户往往从下载目录或 U 盘选文件，过几天原文件被删或被移走，背景就白了。
/// 收进自己的数据目录后路径永不失效，也能随程序一起搬走。
/// 对应旧版 HMOL_qt.py 的 _get_home_background_dir / _choose_home_background（第 18332-18390 行）。
/// </summary>
public static class BackgroundService
{
    /// <summary>静态图。GIF 单独按动图处理，视频另列。</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];

    /// <summary>能当背景的视频。<c>.webm</c> / <c>.mkv</c> 依赖系统解码器，多数机器放不了，故不收。</summary>
    private static readonly string[] VideoExtensions = [".mp4", ".wmv", ".avi"];

    /// <summary>动图扩展名。</summary>
    public const string GifExtension = ".gif";

    /// <summary>素材目录：<see cref="Paths.Backgrounds"/>。</summary>
    public static string StorageDirectory => Paths.Backgrounds;

    /// <summary>可当背景的全部扩展名（小写、含点），给文件选择框构造过滤器用。</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [.. ImageExtensions, GifExtension, .. VideoExtensions];

    /// <summary>文件选择框的过滤器通配串，形如 <c>*.jpg;*.png;…</c>。</summary>
    public static string PickPattern => string.Join(";", SupportedExtensions.Select(ext => "*" + ext));

    // ————— 判定 —————

    /// <summary>按扩展名判定背景类型。不支持的类型返回 <see cref="BackgroundKind.None"/>。</summary>
    public static BackgroundKind DetectKind(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return BackgroundKind.None;

        var extension = Path.GetExtension(pathOrName).ToLowerInvariant();

        if (extension == GifExtension) return BackgroundKind.Gif;
        if (VideoExtensions.Contains(extension)) return BackgroundKind.Video;
        if (ImageExtensions.Contains(extension)) return BackgroundKind.Image;

        return BackgroundKind.None;
    }

    /// <summary>这个文件能不能当背景。</summary>
    public static bool IsSupportedMedia(string? path) => DetectKind(path) != BackgroundKind.None;

    /// <summary>类型的中文名，用于界面提示。</summary>
    public static string DescribeKind(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Image => "静态图片",
        BackgroundKind.Gif => "动图",
        BackgroundKind.Video => "视频",
        _ => "无"
    };

    // ————— 路径 —————

    /// <summary>
    /// 把设置里存的素材名解析成素材目录下的绝对路径。
    /// 只取文件名部分：设置文件是纯文本，手改出一个 <c>..\..</c> 也不该让程序去读目录外的文件。
    /// </summary>
    public static string ResolvePath(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        return string.IsNullOrEmpty(name) ? string.Empty : Path.Combine(StorageDirectory, name);
    }

    /// <summary>素材文件存在时返回绝对路径，否则返回 null。</summary>
    public static string? ResolveExistingFile(string? fileName)
    {
        var path = ResolvePath(fileName);
        return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    // ————— 导入 —————

    /// <summary>
    /// 把指定文件设为背景素材。文件会被复制进素材目录，并清掉上一次的素材（背景只保留一份）。
    /// </summary>
    /// <param name="sourcePath">用户选中的源文件。</param>
    /// <param name="moveSource">
    /// 是否改为移动源文件。从壁纸包解出的文件用 true：解包目录随后会被整目录清掉，
    /// 复制一份大视频纯属浪费磁盘。
    /// </param>
    public static BackgroundImport ImportFile(string sourcePath, bool moveSource = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new BackgroundImport(false, "文件不存在。", BackgroundKind.None, string.Empty);

        var kind = DetectKind(sourcePath);
        if (kind == BackgroundKind.None)
        {
            return new BackgroundImport(false,
                $"不支持的文件类型（{Path.GetExtension(sourcePath)}）。" +
                $"可用的有：{string.Join(" / ", SupportedExtensions)}。",
                BackgroundKind.None, string.Empty);
        }

        try
        {
            Directory.CreateDirectory(StorageDirectory);

            // 不再清旧素材：支持多张壁纸轮播，素材目录相当于一个小素材库。
            // 素材越攒越多时可以在设置里逐张移除，或用「清除背景」一次清空。
            // 用户有可能直接选中素材目录里的文件，那就复用它，别复制成两份。
            var existing = Path.GetFullPath(sourcePath);
            if (string.Equals(Path.GetDirectoryName(existing), Path.GetFullPath(StorageDirectory), StringComparison.OrdinalIgnoreCase))
            {
                var keptName = Path.GetFileName(existing);
                Log.Info($"背景素材已在素材库中，直接复用：{keptName}");
                return new BackgroundImport(true, $"已设为背景：{keptName}", kind, keptName);
            }

            var targetName = BuildTargetName(Path.GetExtension(sourcePath));
            var targetPath = Path.Combine(StorageDirectory, targetName);

            if (moveSource) MoveFile(sourcePath, targetPath);
            else File.Copy(sourcePath, targetPath, overwrite: false);

            Log.Info($"背景素材已导入：{Path.GetFileName(sourcePath)} → {targetPath}（{kind}）");

            return new BackgroundImport(true, $"已设为背景：{Path.GetFileName(sourcePath)}", kind, targetName);
        }
        catch (Exception ex)
        {
            Log.Error($"导入背景素材失败：{sourcePath}", ex);
            return new BackgroundImport(false, $"导入背景失败：{ex.Message}", BackgroundKind.None, string.Empty);
        }
    }

    /// <summary>清掉素材目录里的全部素材（恢复主题渐变）。</summary>
    public static void Clear()
    {
        try
        {
            ClearFiles();
            Log.Info("已清除主页背景素材");
        }
        catch (Exception ex)
        {
            Log.Warn($"清除背景素材失败：{ex.Message}");
        }
    }

    /// <summary>素材库里可当背景的文件名（排序后），给轮播清单与列表用。</summary>
    public static IReadOnlyList<string> ListLibrary()
    {
        try
        {
            if (!Directory.Exists(StorageDirectory)) return [];

            return Directory.EnumerateFiles(StorageDirectory)
                .Where(IsSupportedMedia)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举背景素材失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>删掉一张素材。正在播放的视频可能被占用删不掉，这时返回 false。</summary>
    public static bool Delete(string? fileName)
    {
        var path = ResolvePath(fileName);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            Log.Info($"已移除背景素材：{Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"背景素材删除失败（可能正被占用）：{ex.Message}");
            return false;
        }
    }

    // ————— 内部 —————

    /// <summary>
    /// 素材文件名：<c>bg_年月日时分秒毫秒.扩展名</c>。
    /// 带时间戳而不是固定叫 background.ext，是为了同一秒内连续导入两次也不会撞名。
    /// </summary>
    private static string BuildTargetName(string extension)
    {
        var ext = extension.ToLowerInvariant();
        var name = $"bg_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}";

        var index = 1;
        while (File.Exists(Path.Combine(StorageDirectory, name)))
        {
            name = $"bg_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{index}{ext}";
            index++;
        }

        return name;
    }

    /// <summary>优先移动；跨卷等移动失败时退化为复制后删除源文件。</summary>
    private static void MoveFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Move(sourcePath, targetPath);
        }
        catch (IOException)
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
            try { File.Delete(sourcePath); }
            catch (Exception ex) { Log.Warn($"删除已导入的源文件失败：{ex.Message}"); }
        }
    }

    /// <summary>
    /// 清掉素材目录里的旧素材。
    /// 正在播放的视频文件被 MediaElement 占着，删不掉——不是错误，下次导入时还会再试一次。
    /// </summary>
    /// <param name="exceptPath">要跳过的文件（导入时防止把源文件自己删掉）。</param>
    private static void ClearFiles(string? exceptPath = null)
    {
        if (!Directory.Exists(StorageDirectory)) return;

        var keep = string.IsNullOrEmpty(exceptPath) ? null : Path.GetFullPath(exceptPath);

        foreach (var file in Directory.GetFiles(StorageDirectory))
        {
            if (keep is not null && string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Warn($"旧背景素材删除失败（可能正被占用，下次导入时会再清理）：{ex.Message}");
            }
        }
    }
}
