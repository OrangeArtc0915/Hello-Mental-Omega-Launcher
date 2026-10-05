using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMOL.Core.Logging;

namespace HMOL.App.Theme;

/// <summary>
/// 从一张图片里取「主色」，用于「从背景图取色」把强调色设成和壁纸搭调的颜色。
///
/// 做法：把图缩到极小再按色相分 12 桶投票，票数 = 饱和度 × 明度，
/// 取票数最高的那一桶里的像素做加权平均。这样纯灰 / 纯黑 / 纯白的大片背景
/// 不会把结果沖淡成一个灰扑扑的颜色。
/// </summary>
internal static class AccentExtractor
{
    /// <summary>解码时的采样宽度：只要够统计颜色分布即可，越小越快。</summary>
    private const int SampleWidth = 64;

    /// <summary>低于这个饱和度 / 明度的像素视为灰或黑，不参与投票。</summary>
    private const double MinSaturation = 0.15;

    private const double MinValue = 0.12;

    /// <summary>取图主色；读不出图片或算不出可用颜色时返回 null。</summary>
    public static Color? FromImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var pixels = Decode(path);
            if (pixels is null) return null;

            return Dominant(pixels);
        }
        catch (Exception ex)
        {
            Log.Warn($"从背景图取色失败：{ex.Message}");
            return null;
        }
    }

    private static byte[]? Decode(string path)
    {
        var image = new BitmapImage();

        image.BeginInit();
        image.UriSource = new Uri(path);
        image.DecodePixelWidth = SampleWidth;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return pixels;
    }

    /// <summary>按色相投票挑主色；全是灰阶时退化为整体加权平均。</summary>
    private static Color? Dominant(byte[] pixels)
    {
        const int buckets = 12;

        var weight = new double[buckets];
        var sumR = new double[buckets];
        var sumG = new double[buckets];
        var sumB = new double[buckets];

        // 灰阶图片的兜底统计（票数按明度给，避免大片纯黑把结果拉到 0）
        double allR = 0, allG = 0, allB = 0, allWeight = 0;

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var value = max / 255d;
            var saturation = max == 0 ? 0 : (max - min) / (double)max;

            var grayWeight = 0.05 + value;
            allR += r * grayWeight;
            allG += g * grayWeight;
            allB += b * grayWeight;
            allWeight += grayWeight;

            if (saturation < MinSaturation || value < MinValue) continue;

            var hue = HueOf(r, g, b);
            var index = Math.Clamp((int)(hue / (360 / buckets)), 0, buckets - 1);
            var vote = saturation * value;

            weight[index] += vote;
            sumR[index] += r * vote;
            sumG[index] += g * vote;
            sumB[index] += b * vote;
        }

        var best = -1;
        for (var i = 0; i < buckets; i++)
        {
            if (weight[i] <= 0) continue;
            if (best < 0 || weight[i] > weight[best]) best = i;
        }

        if (best >= 0)
        {
            return Color.FromRgb(
                (byte)Math.Clamp(sumR[best] / weight[best], 0, 255),
                (byte)Math.Clamp(sumG[best] / weight[best], 0, 255),
                (byte)Math.Clamp(sumB[best] / weight[best], 0, 255));
        }

        if (allWeight <= 0) return null;

        return Color.FromRgb(
            (byte)Math.Clamp(allR / allWeight, 0, 255),
            (byte)Math.Clamp(allG / allWeight, 0, 255),
            (byte)Math.Clamp(allB / allWeight, 0, 255));
    }

    private static double HueOf(byte r, byte g, byte b)
    {
        double rr = r / 255d, gg = g / 255d, bb = b / 255d;

        var max = Math.Max(rr, Math.Max(gg, bb));
        var min = Math.Min(rr, Math.Min(gg, bb));
        var delta = max - min;

        if (delta < 1e-6) return 0;

        double hue;
        if (Math.Abs(max - rr) < 1e-6) hue = (gg - bb) / delta + (gg < bb ? 6 : 0);
        else if (Math.Abs(max - gg) < 1e-6) hue = (bb - rr) / delta + 2;
        else hue = (rr - gg) / delta + 4;

        return hue * 60 % 360;
    }
}
