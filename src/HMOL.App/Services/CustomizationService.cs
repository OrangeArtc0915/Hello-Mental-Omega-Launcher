using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMOL.Core.App;
using HMOL.Core.Logging;
using NAudio.MediaFoundation;
using NAudio.Wave;
using HMOL.Core.Localization;

namespace HMOL.App.Services;

/// <summary>
/// 外观细节用到的素材管理：自定义窗口图标与启动 / 关闭音效。
/// 选中的文件会复制进 <see cref="Paths.Custom"/>（原文件删掉也不影响），与主页背景同一套做法。
/// </summary>
internal static class CustomizationService
{
    /// <summary>可当窗口图标的扩展名（文件选择框用）。</summary>
    public const string IconPattern = "*.ico;*.png;*.jpg;*.jpeg;*.bmp";

    /// <summary>可当音效的扩展名（文件选择框用）。</summary>
    public const string SoundPattern = "*.wav;*.mp3;*.wma;*.m4a";

    /// <summary>素材目录下存在这个文件时返回绝对路径，否则返回 null。</summary>
    public static string? Resolve(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrEmpty(name)) return null;

        var path = Path.Combine(Paths.Custom, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>把选中的文件复制进素材目录，返回落盘后的文件名；失败返回 null。</summary>
    public static string? Import(string sourcePath, string prefix)
    {
        try
        {
            Directory.CreateDirectory(Paths.Custom);

            // 已在素材目录里的就直接复用，不复制成两份
            var existing = Path.GetFullPath(sourcePath);
            if (string.Equals(Path.GetDirectoryName(existing), Path.GetFullPath(Paths.Custom), StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(existing);

            var name = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
            var target = Path.Combine(Paths.Custom, name);

            File.Copy(sourcePath, target, overwrite: false);
            Log.Info(Loc.F("个性化素材已导入：{0} → {1}", Path.GetFileName(sourcePath), target));

            return name;
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导入个性化素材失败：{0}", sourcePath), ex);
            return null;
        }
    }

    /// <summary>删掉一张素材（换新的或清除时调用）；删不掉只记日志。</summary>
    public static void Delete(string? fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return;

        try
        {
            File.Delete(path);
            Log.Info(Loc.F("个性化素材已删除：{0}", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("个性化素材删除失败（可能正被占用）：{0}", ex.Message));
        }
    }

    /// <summary>载入自定义窗口图标；读不到返回 null（调用方回退内置图标）。</summary>
    public static ImageSource? LoadIcon(string? fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return null;

        try
        {
            var image = new BitmapImage();

            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 256;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("自定义窗口图标加载失败，回退内置图标：{0}", ex.Message));
            return null;
        }
    }

    /// <summary>播放一次性音效（后台线程播完即弃）。返回是否真的开始播放。</summary>
    public static bool PlaySound(string? fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return false;

        Task.Run(() =>
        {
            try
            {
                // 解码走 Media Foundation（与背景音乐同一套），要先起一次
                try { MediaFoundationApi.Startup(); }
                catch { /* 已经启动过或系统不支持时忽略，下面读文件会报真错 */ }

                using var reader = new MediaFoundationReader(path);
                using var output = new WaveOutEvent { DesiredLatency = 120 };

                output.Init(reader);
                output.Play();

                while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(60);
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("音效播放失败：{0}", ex.Message));
            }
        });

        return true;
    }
}
