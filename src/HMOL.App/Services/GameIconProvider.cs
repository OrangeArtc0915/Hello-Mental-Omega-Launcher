using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMOL.Core.Games;
using HMOL.Core.Instances;

namespace HMOL.App.Services;

/// <summary>
/// 实例卡片上的游戏图标。心灵终结 / 原版 / 尤复 用随包图标（<c>Assets\GameIcons</c>），
/// 其它红警 Mod 从实例主程序里提取它内嵌的图标；都取不到时返回 null，界面退回矢量图标。
/// 结果按需缓存（位图已冻结，可跨线程复用）。
/// </summary>
internal static class GameIconProvider
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Resolve(GameInstance instance)
        => FixedIcon(instance.Kind) ?? ExtractFromExecutable(instance.ExecutablePath);

    /// <summary>已知类型的固定图标；「其它 Mod」没有固定图标，返回 null。</summary>
    private static ImageSource? FixedIcon(GameKind kind) => kind switch
    {
        GameKind.MentalOmega => LoadPackIcon("mental-omega.ico"),
        GameKind.OriginalRa2 => LoadPackIcon("original.ico"),
        GameKind.YurisRevenge => LoadPackIcon("yuris-revenge.ico"),
        _ => null
    };

    private static ImageSource? LoadPackIcon(string fileName)
    {
        var key = "pack:" + fileName;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(
                $"pack://application:,,,/HMOL;component/Assets/GameIcons/{fileName}", UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch
        {
            // 图标读不出来就交给调用方退回矢量图标，不影响实例卡片
        }

        Cache[key] = image;
        return image;
    }

    private static ImageSource? ExtractFromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return null;

        string key;
        try
        {
            // 主程序被换掉（更新时间变）后缓存自动失效
            key = $"exe:{executablePath}|{File.GetLastWriteTimeUtc(executablePath).Ticks}";
        }
        catch
        {
            key = "exe:" + executablePath;
        }

        if (Cache.TryGetValue(key, out var cached)) return cached;

        var image = ExtractIcon(executablePath);
        Cache[key] = image;
        return image;
    }

    private static ImageSource? ExtractIcon(string path)
    {
        var info = new SHFILEINFO();

        // 和资源管理器一样取「大图标」，即 exe 里的应用图标
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}