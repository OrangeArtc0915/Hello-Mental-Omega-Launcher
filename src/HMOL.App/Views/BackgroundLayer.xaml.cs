using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HMOL.App.Animation;
using HMOL.Core.Appearance;
using HMOL.Core.Logging;

namespace HMOL.App.Views;

/// <summary>
/// 铺满整个窗口的个性化背景层（含标题栏与侧栏）：支持静态图、动图（GIF 逐帧）、视频（静音循环）。
/// 放在窗口内容栅格的第一个子元素位置，所以它在所有界面之下；没设背景时整层透明，露出主题渐变。
/// 参考 Tool\src\StardewLauncher.App\Views\BackgroundLayer。
///
/// 自己解码 GIF 而不是引第三方库：WPF 自带的 GifBitmapDecoder 已经能拿到每一帧和帧延时，
/// 配一个 DispatcherTimer 就是播放器，不值得为一个背景功能加依赖。
///
/// 与旧版的对应关系：图片 / GIF / 视频的判定与模糊见 HMOL_qt.py 第 6538-6710 行（_draw_image_bg），
/// 视频的循环与暗化见第 6712-6741 行（_draw_video_bg）。
/// </summary>
public partial class BackgroundLayer : UserControl
{
    /// <summary>模糊半径上限，与 BackgroundSettings.BlurRadius 的取值范围一致。</summary>
    private const int MaxBlurRadius = 30;

    private readonly DispatcherTimer _gifTimer = new();

    /// <summary>图片的模糊效果。在代码里挂而不是写进 XAML：效果对象不适合用 x:Name 取。</summary>
    private readonly BlurEffect _blur = new() { Radius = 0, RenderingBias = RenderingBias.Performance };

    private List<(BitmapSource Frame, TimeSpan Delay)> _gifFrames = [];
    private int _gifIndex;
    private int _loadVersion;
    private bool _paused;
    private bool _videoOpened;
    private string? _appliedPath;

    public BackgroundLayer()
    {
        InitializeComponent();

        ImgBackground.Effect = _blur;
        _gifTimer.Tick += OnGifTick;
    }

    /// <summary>
    /// 按设置铺背景。<paramref name="fileName"/> 是素材目录下的文件名；为空、文件不存在或类型不认识时
    /// 本层保持透明，露出主题渐变。
    /// </summary>
    public void Apply(string? fileName, int blurRadius, int dimPercent, int fadeMs)
    {
        var path = BackgroundService.ResolveExistingFile(fileName);

        // 只动了模糊 / 暗化，素材本身没换：别重头解码大图，更别让视频重头播
        if (path is not null && string.Equals(path, _appliedPath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyLook(path, blurRadius, dimPercent);
            return;
        }

        Reset();

        if (path is null)
        {
            Log.Info("背景：未设置素材或素材已丢失，使用主题渐变");
            return;
        }

        var kind = BackgroundService.DetectKind(path);
        if (kind == BackgroundKind.None)
        {
            Log.Warn($"背景：无法识别的素材类型，使用主题渐变（{Path.GetFileName(path)}）");
            return;
        }

        var blur = ApplyLook(path, blurRadius, dimPercent);

        // 先归零：新画面出现之前不能让上一张背景闪一帧，淡入会把它推回 1
        PanRoot.Opacity = 0;

        try
        {
            switch (kind)
            {
                case BackgroundKind.Video:
                    StartVideo(path);
                    break;

                case BackgroundKind.Gif:
                    LoadGif(path);
                    break;

                default:
                    ImgBackground.Source = LoadFrozen(path);
                    ImgBackground.Visibility = Visibility.Visible;

                    Log.Info($"背景：图片 {Path.GetFileName(path)}（模糊 {blur}，暗化 {dimPercent}%）");
                    break;
            }

            _appliedPath = path;
        }
        catch (Exception ex)
        {
            Log.Warn($"背景加载失败，回退主题渐变：{ex.Message}");
            Reset();
            return;
        }

        FadeIn(fadeMs);
    }

    /// <summary>应用模糊与暗化。返回实际生效的模糊半径。</summary>
    private int ApplyLook(string path, int blurRadius, int dimPercent)
    {
        var kind = BackgroundService.DetectKind(path);

        // 压暗层的颜色取主题的窗口底色（深色主题近黑、浅色主题近白），跟着主题自动变，
        // 免得把颜色写死在代码里
        OverlayDim.SetResourceReference(Border.BackgroundProperty, "Surface.Window");
        OverlayDim.Opacity = Math.Clamp(dimPercent, 0, 80) / 100d;

        // 视频不叠模糊：MediaElement 的画面不走常规渲染链，BlurEffect 要么不生效、
        // 要么逼它整帧软件合成；旧版对视频同样只做暗化（HMOL_qt.py 第 6712 行 _draw_video_bg）
        var blur = kind == BackgroundKind.Video ? 0 : Math.Clamp(blurRadius, 0, MaxBlurRadius);

        if (kind == BackgroundKind.Video && blurRadius > 0)
            Log.Info("背景：视频不支持模糊，已只应用暗化（与旧版行为一致）");

        _blur.Radius = blur;

        // 模糊会把图片边缘糊成半透明，把承载容器按半径向外撑开，多出来的部分由外层裁掉
        var bleed = blur > 0 ? Math.Min(48, (int)Math.Ceiling(blur * 1.5) + 4) : 0;
        PanMedia.Margin = bleed == 0 ? new Thickness(0) : new Thickness(-bleed);

        return blur;
    }

    /// <summary>窗口最小化时停掉动图与视频，别白烧 CPU。</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;

        if (VidBackground.Visibility == Visibility.Visible)
        {
            try
            {
                if (paused) VidBackground.Pause();
                else if (_videoOpened) VidBackground.Play();
            }
            catch (Exception ex)
            {
                Log.Warn($"背景视频{(paused ? "暂停" : "恢复")}失败：{ex.Message}");
            }
        }

        if (paused) _gifTimer.Stop();
        else ScheduleNextGifFrame();
    }

    // ————— 动图 —————

    /// <summary>解码放后台线程：几十帧的图在主线程解会卡一下。</summary>
    private async void LoadGif(string file)
    {
        var version = ++_loadVersion;

        try
        {
            var frames = await Task.Run(() => DecodeGif(file));

            // 期间用户可能又换了背景，丢弃过期结果
            if (version != _loadVersion || frames.Count == 0) return;

            _gifFrames = frames;
            _gifIndex = 0;

            ImgBackground.Source = frames[0].Frame;
            ImgBackground.Visibility = Visibility.Visible;

            ScheduleNextGifFrame();

            Log.Info($"背景：动图 {Path.GetFileName(file)}（{frames.Count} 帧）");
        }
        catch (Exception ex)
        {
            Log.Warn($"动图背景加载失败：{ex.Message}");

            if (version == _loadVersion) Reset();
        }
    }

    private static List<(BitmapSource Frame, TimeSpan Delay)> DecodeGif(string file)
    {
        var decoder = new GifBitmapDecoder(
            new Uri(file),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frames = new List<(BitmapSource Frame, TimeSpan Delay)>();

        foreach (var frame in decoder.Frames)
        {
            // 帧要冻结才能跨线程交给界面用
            if (frame.CanFreeze) frame.Freeze();

            frames.Add((frame, ReadFrameDelay(frame)));
        }

        return frames;
    }

    private static TimeSpan ReadFrameDelay(BitmapFrame frame)
    {
        // GIF 的帧延时存在 Graphic Control Extension 里，单位是 1/100 秒
        const int fallback = 100;

        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                var raw = metadata.GetQuery("/grctlext/Delay");

                // 0 与 1（<20ms）在绝大多数浏览器里都被当成「太快了」，按 100ms 处理
                if (raw is ushort hundredths && hundredths >= 2)
                    return TimeSpan.FromMilliseconds(hundredths * 10);
            }
        }
        catch
        {
            // 元数据读不到就用默认值，不值得为此报错
        }

        return TimeSpan.FromMilliseconds(fallback);
    }

    private void ScheduleNextGifFrame()
    {
        if (_paused || _gifFrames.Count <= 1) return;

        _gifTimer.Interval = _gifFrames[_gifIndex].Delay;
        _gifTimer.Start();
    }

    private void OnGifTick(object? sender, EventArgs e)
    {
        _gifTimer.Stop();

        if (_paused || _gifFrames.Count == 0) return;

        _gifIndex = (_gifIndex + 1) % _gifFrames.Count;
        ImgBackground.Source = _gifFrames[_gifIndex].Frame;

        ScheduleNextGifFrame();
    }

    // ————— 视频 —————

    /// <summary>
    /// 起播。这里刻意<b>不</b>调 Play：LoadedBehavior 是 Manual，媒体还没 Open 时调 Play 会被丢掉，
    /// 画面就一直黑着。真正的播放放到 MediaOpened 里做。
    /// </summary>
    private void StartVideo(string file)
    {
        _videoOpened = false;

        VidBackground.Visibility = Visibility.Visible;
        VidBackground.Source = new Uri(file);

        Log.Info($"背景：视频 {Path.GetFileName(file)}，等待解码…");
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (VidBackground.Visibility != Visibility.Visible) return;

        _videoOpened = true;

        Log.Info($"背景视频已解码：{VidBackground.NaturalVideoWidth}×{VidBackground.NaturalVideoHeight}，" +
                 $"时长 {VidBackground.NaturalDuration.TimeSpan.TotalSeconds:0.0} 秒");

        try
        {
            VidBackground.Volume = 0;
            VidBackground.Position = TimeSpan.Zero;

            if (!_paused) VidBackground.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"背景视频起播失败：{ex.Message}");
        }
    }

    /// <summary>解不开就把视频层收掉：宁可露出主题渐变，也不要给用户留一块黑屏。</summary>
    private void OnVideoFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 换背景时旧媒体也会报失败，那一份已经被收掉了，不用再处理
        if (VidBackground.Visibility != Visibility.Visible) return;

        Log.Warn($"背景视频无法解码（{e.ErrorException?.Message ?? "未知原因"}），已回退为无背景。" +
                 "常见原因是系统缺少该视频编码的解码器（HEVC / AV1 / MKV 需另装解码器）");

        _videoOpened = false;

        try { VidBackground.Stop(); }
        catch { /* 未加载过媒体时 Stop 可能抛异常，忽略 */ }

        VidBackground.Source = null;
        VidBackground.Visibility = Visibility.Collapsed;

        // 视频没了就别再压暗，否则会留下一层灰
        OverlayDim.Opacity = 0;
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        if (VidBackground.Visibility != Visibility.Visible || _paused) return;

        // 背景视频要无缝循环，播完回到开头
        try
        {
            VidBackground.Position = TimeSpan.Zero;
            VidBackground.Play();
        }
        catch (Exception ex)
        {
            Log.Warn($"背景视频循环播放失败：{ex.Message}");
        }
    }

    // ————— 内部 —————

    /// <summary>解码成冻结的位图：避免 WPF 一直占着原文件句柄，用户也就删得掉自己的原图。</summary>
    private static BitmapSource LoadFrozen(string file)
    {
        var image = new BitmapImage();

        image.BeginInit();
        image.UriSource = new Uri(file);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        image.Freeze();

        return image;
    }

    /// <summary>切换背景时的淡入：从全透明过渡到不透明。</summary>
    private void FadeIn(int fadeMs)
    {
        // 动画被挂起或时长为 0 时，引擎会直接把终值写进去
        AnimationEngine.Start("backgroundFade", 0, 1, Math.Clamp(fadeMs, 0, 2000), null,
            value => PanRoot.Opacity = value);
    }

    private void Reset()
    {
        _loadVersion++;
        _gifFrames = [];
        _gifIndex = 0;
        _gifTimer.Stop();
        _videoOpened = false;
        _appliedPath = null;

        ImgBackground.Source = null;
        ImgBackground.Visibility = Visibility.Collapsed;

        try { VidBackground.Stop(); }
        catch { /* 没加载过媒体时 Stop 可能抛异常，忽略 */ }

        VidBackground.Source = null;
        VidBackground.Visibility = Visibility.Collapsed;

        OverlayDim.Opacity = 0;
        PanRoot.Opacity = 1;
    }
}
