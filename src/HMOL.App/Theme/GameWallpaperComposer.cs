using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMOL.Core.Appearance;
using HMOL.Core.Localization;
using HMOL.Core.Logging;

namespace HMOL.App.Theme;

/// <summary>生成好的游戏壁纸：1920×1080 的 PNG 与主题配色（都是不带 # 的 6 位十六进制）。</summary>
public sealed record GameWallpaperArt(byte[] Png, string AccentHex, string TextHex);

/// <summary>生成结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record GameWallpaperComposeResult(bool Ok, string Message, GameWallpaperArt? Art);

/// <summary>
/// 把图片 / 视频做成游戏壁纸：cover 裁切到 1920×1080、取主色、算可读的文字色。
///
/// 为什么放在 App 层：WPF 的图像解码、MediaPlayer 取帧与位图取色都依赖 WPF；
/// Core 只接收 PNG 字节并负责落盘（见 <see cref="GameWallpaperService"/>），保持 Core 不引用 WPF。
/// </summary>
public static class GameWallpaperComposer
{
    /// <summary>补丁期望的壁纸尺寸。</summary>
    public const int TargetWidth = 1920;

    public const int TargetHeight = 1080;

    /// <summary>视频取帧的整体超时（解码 + 等首帧渲染），超过就报错，不让界面一直停在「正在生成」。</summary>
    private static readonly TimeSpan VideoTimeout = TimeSpan.FromSeconds(8);

    /// <summary>取不到主色时的兜底色（偏冷蓝，和补丁默认观感接近）。</summary>
    private static readonly Color FallbackAccent = Color.FromRgb(0x3D, 0x8B, 0xFD);

    /// <summary>
    /// 生成壁纸。视频取帧必须在 UI 线程（MediaPlayer 依赖消息泵），
    /// 调用方在别的线程时会自动切回界面线程。
    /// </summary>
    public static async Task<GameWallpaperComposeResult> ComposeAsync(
        string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Fail(Loc.T("壁纸文件不存在。"));

        var kind = BackgroundService.DetectKind(sourcePath);
        if (kind == BackgroundKind.None)
            return Fail(Loc.T("只支持图片（jpg / png / bmp / webp / gif）或视频（mp4 / wmv / avi）。"));

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return await dispatcher
                .InvokeAsync(() => ComposeAsync(sourcePath, cancellationToken))
                .Task.Unwrap()
                .ConfigureAwait(false);
        }

        try
        {
            BitmapSource frame;

            if (kind == BackgroundKind.Video)
            {
                frame = await GrabVideoFrameAsync(sourcePath, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                // 静态图（含 GIF 首帧）解码与裁切都放到后台做，避免大图卡住界面
                frame = await Task.Run(() => LoadImageCover(sourcePath), cancellationToken).ConfigureAwait(true);
            }

            var accent = AccentExtractor.FromBitmap(frame) ?? FallbackAccent;
            var text = LightenForText(accent);
            var png = EncodePng(frame);

            Log.Info(Loc.F(
                "游戏壁纸已生成：{0}（{1}×{2}，主色 {3}）",
                Path.GetFileName(sourcePath), frame.PixelWidth, frame.PixelHeight, Hex(accent)));

            return new GameWallpaperComposeResult(
                true,
                Loc.T("壁纸已生成。"),
                new GameWallpaperArt(png, Hex(accent), Hex(text)));
        }
        catch (OperationCanceledException)
        {
            return Fail(Loc.T("生成壁纸已取消。"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("生成游戏壁纸失败：{0}", sourcePath), ex);
            return Fail(Loc.F("生成游戏壁纸失败：{0}", ex.Message));
        }

        static GameWallpaperComposeResult Fail(string message) => new(false, message, null);
    }

    // ————— 静态图 —————

    /// <summary>等比缩放到铺满 1920×1080 后居中裁切（cover），返回冻结过的位图。</summary>
    private static BitmapSource LoadImageCover(string path)
    {
        var image = new BitmapImage();

        try
        {
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException)
        {
            throw new InvalidOperationException(
                Loc.T("无法解码这张图片（系统可能缺少对应格式的解码器，WebP 最常见）。请换一张 JPG / PNG 再试。"));
        }

        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new InvalidOperationException(Loc.T("图片尺寸无效，无法生成壁纸。"));

        var scale = Math.Max(TargetWidth / (double)image.PixelWidth, TargetHeight / (double)image.PixelHeight);
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));

        // 缩放取整后可能比目标少 1 像素，补一点比例，保证裁切区域一定在图内
        var adjustX = TargetWidth / (double)scaled.PixelWidth;
        var adjustY = TargetHeight / (double)scaled.PixelHeight;
        if (adjustX > 1 || adjustY > 1)
        {
            scale *= Math.Max(adjustX, adjustY);
            scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }

        var offsetX = Math.Max(0, (scaled.PixelWidth - TargetWidth) / 2);
        var offsetY = Math.Max(0, (scaled.PixelHeight - TargetHeight) / 2);

        var cropped = new CroppedBitmap(
            scaled, new Int32Rect(offsetX, offsetY, TargetWidth, TargetHeight));

        cropped.Freeze();
        return cropped;
    }

    // ————— 视频取帧 —————

    /// <summary>
    /// 用 MediaPlayer 打开视频，跳到 1 秒处（太短就取中间），
    /// 通过 VideoDrawing 把当前帧按 cover 画到 1920×1080 的 RenderTargetBitmap 上。
    /// 打开 / 出画面共用 8 秒预算，超时抛中文异常；任何异常都由上层转成可读提示。
    /// </summary>
    private static async Task<BitmapSource> GrabVideoFrameAsync(string path, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, e) =>
            failed.TrySetException(e.ErrorException ?? new InvalidOperationException(e.ToString()));

        try
        {
            player.Open(new Uri(path, UriKind.Absolute));

            var timeout = Task.Delay(RemainingTime(stopwatch), cancellationToken);
            var completed = await Task.WhenAny(opened.Task, failed.Task, timeout).ConfigureAwait(true);

            if (completed == failed.Task)
            {
                var error = await failed.Task.ConfigureAwait(true);
                throw new InvalidOperationException(Loc.F("无法解码这个视频：{0}", error.Message));
            }

            if (completed != opened.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw VideoTimeoutError();
            }

            // 首选 1 秒处；很短的视频退到中间；完全拿不到时长就取开头
            var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            var position = duration > TimeSpan.FromSeconds(2)
                ? TimeSpan.FromSeconds(1)
                : duration > TimeSpan.Zero
                    ? TimeSpan.FromTicks(duration.Ticks / 2)
                    : TimeSpan.Zero;

            player.Position = position;
            player.Play();

            // 等首帧真正解码出来；透明 / 全黑的画面继续等，直到 8 秒预算用完
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var frame = RenderVideoFrame(player);
                if (HasVisibleContent(frame)) return frame;

                if (stopwatch.Elapsed >= VideoTimeout) throw VideoTimeoutError();

                await Task.Delay(120, cancellationToken).ConfigureAwait(true);
            }
        }
        finally
        {
            player.Stop();
            player.Close();
        }
    }

    /// <summary>把 MediaPlayer 当前帧按 cover 画到 1920×1080 上。</summary>
    private static BitmapSource RenderVideoFrame(MediaPlayer player)
    {
        var width = player.NaturalVideoWidth;
        var height = player.NaturalVideoHeight;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException(Loc.T("这个文件没有可用的视频画面。"));

        var scale = Math.Max(TargetWidth / (double)width, TargetHeight / (double)height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawVideo(player, new Rect(
                (TargetWidth - drawWidth) / 2,
                (TargetHeight - drawHeight) / 2,
                drawWidth,
                drawHeight));
        }

        var bitmap = new RenderTargetBitmap(TargetWidth, TargetHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return bitmap;
    }

    /// <summary>抽样检查画面里有没有可见像素（视频刚打开时常常先渲染出全透明帧）。</summary>
    private static bool HasVisibleContent(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(
                source,
                new ScaleTransform(48.0 / source.PixelWidth, 27.0 / source.PixelHeight));

            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            var stride = converted.PixelWidth * 4;
            if (stride <= 0 || converted.PixelHeight <= 0) return false;

            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            // 只看 alpha：全黑但确实解码出来的帧 alpha 是 255，刚打开时的空白帧 alpha 是 0
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) return true;
            }

            return false;
        }
        catch
        {
            // 判定本身失败时按「有画面」处理，避免一直重试到超时
            return true;
        }
    }

    private static TimeSpan RemainingTime(Stopwatch stopwatch)
    {
        var remaining = VideoTimeout - stopwatch.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeoutException VideoTimeoutError()
        => new(Loc.T("视频解码超时（8 秒），取不到可用的画面。请换一个视频，或先把视频转成图片再应用。"));

    // ————— 颜色与编码 —————

    /// <summary>主色太暗时按比例向白色混合，把亮度提到 150 以上，保证游戏里的文字读得清。</summary>
    private static Color LightenForText(Color accent)
    {
        var luminance = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
        const double minLuminance = 150;

        if (luminance >= minLuminance) return accent;

        var ratio = Math.Clamp((minLuminance - luminance) / (255 - luminance), 0, 1);

        return Color.FromRgb(
            (byte)Math.Round(accent.R + (255 - accent.R) * ratio),
            (byte)Math.Round(accent.G + (255 - accent.G) * ratio),
            (byte)Math.Round(accent.B + (255 - accent.B) * ratio));
    }

    private static string Hex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return stream.ToArray();
    }
}
