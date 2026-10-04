using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HMOL.App.Pages;
using HMOL.App.Windows;
using HMOL.Core.Logging;

namespace HMOL.App;

/// <summary>
/// 调试期自动截图（仅 Debug 构建）。命令行带 <c>--capture-home &lt;png 路径&gt;</c> 时，
/// 等主窗口显示、布局完成、动效收敛之后把窗口内容渲染成 PNG，然后退出程序。
/// 用 <see cref="RenderTargetBitmap"/> 在 UI 线程、<see cref="DispatcherPriority.ContextIdle"/>
/// （布局已完成）时截，不会截到还没排好版的半成品。
///
/// 加 <c>--capture-page &lt;页面&gt;</c>（home / instances / packages / multiplayer / log / settings，
/// 也接受 0-5 的下标）可以截指定页面：先切到那一页再等动效收敛。不给就截主页（与原来一致）。
/// 加 <c>--capture-setup &lt;分类&gt;</c> 则在设置页里再切到某个分类
/// （0-12 下标，或 appearance / background / layout / home 等名字），用来单独截某一类设置。
/// </summary>
internal static class DebugCapture
{
    /// <summary>等动效收敛的时间：页面入场动画最长约 0.5 秒，这里留一倍余量。</summary>
    private const int SettleMs = 1200;

    /// <summary>设置分类切换还要多等一会儿（卡片有淡入）。</summary>
    private const int SettleWithCategoryMs = 1600;

    /// <summary>设置分类名 → 下标。与 MainWindow 里 PanSetupNav 的顺序一致。</summary>
    private static readonly Dictionary<string, int> SetupCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appearance"] = 0,
        ["nickname"] = 1,
        ["weather"] = 2,
        ["sites"] = 3,
        ["background"] = 4,
        ["music"] = 5,
        ["layout"] = 6,
        ["home"] = 7,
        ["extensions"] = 8,
        ["autostart"] = 9,
        ["gamepath"] = 10,
        ["update"] = 11,
        ["about"] = 12
    };

    /// <summary>读命令行里的 <c>--capture-home</c> 参数。没给或路径为空返回 false。</summary>
    public static bool TryReadPath(IReadOnlyList<string> args, out string? path)
    {
        path = null;

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--capture-home", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(args[i + 1])) return false;

            path = args[i + 1];
            return true;
        }

        return false;
    }

    /// <summary>
    /// 读 <c>--capture-page &lt;页面&gt;</c>，返回 <c>NavPages</c> 里的页面号。
    /// 没给、或名字不认识时返回主页（保持原有行为）。
    /// </summary>
    private static int ReadPage(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--capture-page", StringComparison.OrdinalIgnoreCase)) continue;

            var value = args[i + 1].Trim();

            if (int.TryParse(value, out var index))
                return Math.Clamp(index, NavPages.Home, NavPages.Settings);

            return value.ToLowerInvariant() switch
            {
                "instances" or "instance" => NavPages.Instances,
                "packages" or "package" => NavPages.Packages,
                "multiplayer" or "mp" => NavPages.Multiplayer,
                "log" or "logs" => NavPages.Log,
                "settings" or "setting" => NavPages.Settings,
                _ => NavPages.Home
            };
        }

        return NavPages.Home;
    }

    /// <summary>页面号 → 日志里可读的名字。</summary>
    private static string PageName(int page) => page switch
    {
        NavPages.Instances => "游戏实例",
        NavPages.Packages => "包管理",
        NavPages.Multiplayer => "联机",
        NavPages.Log => "运行日志",
        NavPages.Settings => "设置",
        _ => "主页"
    };

    /// <summary>
    /// 读 <c>--capture-setup &lt;分类&gt;</c>：接受 0-11 的下标或 <see cref="SetupCategories"/> 里的名字。
    /// 没给返回 -1（表示不切分类）。
    /// </summary>
    private static int ReadSetupCategory(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--capture-setup", StringComparison.OrdinalIgnoreCase)) continue;

            var value = args[i + 1].Trim();

            if (int.TryParse(value, out var index)) return index;
            if (SetupCategories.TryGetValue(value, out var mapped)) return mapped;

            Log.Warn($"无法识别的设置分类：{value}，本次按默认分类截。");
            return -1;
        }

        return -1;
    }

    /// <summary>读 <c>--capture-size 宽x高</c>（形如 940x580）：先把窗口改成这个尺寸再截，用来检查小窗口下的排版。</summary>
    private static (double Width, double Height)? ReadSize(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], "--capture-size", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = args[i + 1].Split('x', 'X');
            if (parts.Length != 2) return null;
            if (!double.TryParse(parts[0], out var width) || !double.TryParse(parts[1], out var height)) return null;

            return (width, height);
        }

        return null;
    }

    /// <summary>挂上截图：窗口还没 Loaded 就等它 Loaded，已经 Loaded 就直接排期。</summary>
    public static void Attach(MainWindow window, string path, IReadOnlyList<string> args)
    {
        if (window.IsLoaded) Schedule(window, path, args);
        else window.Loaded += (_, _) => Schedule(window, path, args);
    }

    private static void Schedule(MainWindow window, string path, IReadOnlyList<string> args)
    {
        var size = ReadSize(args);

        if (size is not null)
        {
            window.Width = size.Value.Width;
            window.Height = size.Value.Height;
        }

        // 指定了页面就先切过去，等动效收敛再截（不要的话截的就是主页，与原来一致）
        var setupCategory = ReadSetupCategory(args);
        var page = setupCategory >= 0 ? NavPages.Settings : ReadPage(args);
        if (page != NavPages.Home) window.SwitchToPage(page);

        // 设置页里再切到指定分类（例如 --capture-setup background 截「主页背景」那一类）
        if (setupCategory >= 0) window.SelectSetupCategory(setupCategory);

        Log.Info($"自动截图：目标页面={PageName(page)}" +
                 (setupCategory >= 0 ? $"，设置分类={setupCategory}" : string.Empty) +
                 $"，输出={path}");

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(setupCategory >= 0 ? SettleWithCategoryMs : SettleMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Dispatcher.InvokeAsync(() => Capture(window, path), DispatcherPriority.ContextIdle);
        };

        timer.Start();
    }

    private static void Capture(MainWindow window, string path)
    {
        try
        {
            var content = window.Content as FrameworkElement ?? window;
            content.UpdateLayout();

            var width = (int)Math.Ceiling(content.ActualWidth);
            var height = (int)Math.Ceiling(content.ActualHeight);

            if (width <= 0 || height <= 0)
            {
                Log.Warn("自动截图跳过：主窗口还没量出尺寸");
                return;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var stream = File.Create(path)) encoder.Save(stream);

            Log.Info($"自动截图完成：{path}（{width}×{height}）");
        }
        catch (Exception ex)
        {
            Log.Error($"自动截图失败：{path}", ex);
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }
}
