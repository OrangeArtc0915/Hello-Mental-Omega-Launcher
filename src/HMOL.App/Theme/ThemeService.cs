using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using HMOL.Core.App;

namespace HMOL.App.Theme;

/// <summary>一套强调色的色阶。Base 用于图标与点睛，Bright 用于主要按钮与胶囊。</summary>
internal sealed record AccentPalette(
    Color Deep,
    Color Base,
    Color Bright,
    Color Hover,
    Color Soft,
    Color Faint);

/// <summary>
/// 主题服务：把配色写入 Application.Resources，界面通过 DynamicResource 自动响应。
/// 资源键采用语义化命名（Surface / Border / Text / Accent / Status / Nav）。
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<AccentTheme, AccentPalette> Accents = new()
    {
        [AccentTheme.Default] = new(
            ColorOf("#2A3A6B"), ColorOf("#3B55A0"), ColorOf("#4F6FD8"),
            ColorOf("#4563C4"), ColorOf("#D5DCF2"), ColorOf("#EFF2FB")),
        [AccentTheme.Blue] = new(
            ColorOf("#1F4A6B"), ColorOf("#2E6E9E"), ColorOf("#3F8FD0"),
            ColorOf("#4489BC"), ColorOf("#C7DEEE"), ColorOf("#EBF4FA")),
        [AccentTheme.Green] = new(
            ColorOf("#23491F"), ColorOf("#356B2E"), ColorOf("#4E9A43"),
            ColorOf("#427F38"), ColorOf("#CFE5C6"), ColorOf("#EDF5E9")),
        [AccentTheme.Purple] = new(
            ColorOf("#46246B"), ColorOf("#6A3A9E"), ColorOf("#8B57D0"),
            ColorOf("#7B4BBC"), ColorOf("#E2D6F2"), ColorOf("#F4EFFB"))
    };

    private static DispatcherTimer? _systemWatcher;
    private static bool _systemWasDark;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static AccentTheme Accent { get; private set; } = AccentTheme.Default;

    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Initialize(ThemeMode mode, AccentTheme accent)
    {
        Mode = mode;
        Accent = accent;
        _systemWasDark = IsSystemInDarkMode();
        Apply();
        EnsureSystemWatcher();
    }

    public static void SetTheme(ThemeMode mode, AccentTheme accent)
    {
        Mode = mode;
        Accent = accent;
        SettingsStore.Current.ThemeMode = mode;
        SettingsStore.Current.Accent = accent;
        Apply();
    }

    /// <summary>切换预设强调色（会清掉自定义色）。</summary>
    public static void SetPresetAccent(AccentTheme accent)
    {
        Accent = accent;
        SettingsStore.Current.Accent = accent;
        SettingsStore.Current.CustomAccentColor = string.Empty;
        Apply();
    }

    /// <summary>设置自定义强调色；颜色无法解析时返回 false 且不改动。</summary>
    public static bool SetCustomAccent(string? hex)
    {
        if (!TryParseColor(hex, out var color)) return false;

        // 存归一化之后的颜色：它才是实际生效的 Bright，免得「存的」和「看到的」对不上
        SettingsStore.Current.CustomAccentColor = ToHex(DerivePalette(color).Bright);
        Apply();
        return true;
    }

    /// <summary>清掉自定义强调色，回到预设色。</summary>
    public static void ClearCustomAccent()
    {
        SettingsStore.Current.CustomAccentColor = string.Empty;
        Apply();
    }

    /// <summary>当前是否正在使用自定义强调色。</summary>
    public static bool HasCustomAccent => TryParseColor(SettingsStore.Current.CustomAccentColor, out _);

    /// <summary>当前生效的强调色（自定义优先，否则取预设的 Bright）。</summary>
    public static Color CurrentAccentColor
    {
        get
        {
            if (TryParseColor(SettingsStore.Current.CustomAccentColor, out var custom)) return custom;
            return Accents.TryGetValue(Accent, out var found) ? found.Bright : Accents[AccentTheme.Default].Bright;
        }
    }

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c> / 不带 # 的写法；失败返回 false。</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // 解析不了按无效处理，由调用方提示
        }

        return false;
    }

    /// <summary>颜色格式化成 <c>#RRGGBB</c>。</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Apply()
    {
        IsDark = Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemInDarkMode()
        };

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        foreach (var (key, color) in BuildBrushes(IsDark, Accent))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        resources["Shadow.Tint"] = IsDark ? ColorOf("#000000") : ColorOf("#2A3040");
        resources["Brush.WindowBackground"] = BuildWindowGradient(IsDark, Accent);
        resources["Brush.CardCover"] = BuildCardCover(IsDark, Accent);

        ThemeChanged?.Invoke();
    }

    public static void Shutdown()
    {
        _systemWatcher?.Stop();
        _systemWatcher = null;
    }

    /// <summary>
    /// 只按当前设置重算「卡片 / 组件」两个半透明承载面的画刷。
    /// 拖动设置页的透明度滑块时用这个，不走整个 <see cref="Apply"/>——
    /// 后者还会重建窗口渐变并触发 <c>ThemeChanged</c>，让主页背景白重铺一遍。
    /// </summary>
    public static void ApplySurfaceOpacity()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        resources["Surface.CardGlass"] = FrozenBrush(ScaleAlpha(CardGlassBase(IsDark), CardGlassAlpha));
        resources["Surface.PanelGlass"] = FrozenBrush(ScaleAlpha(PanelGlassBase(IsDark), PanelGlassAlpha));
        resources["Surface.Sunken"] = FrozenBrush(ScaleAlpha(SunkenBase(IsDark)));
    }

    public static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSystemWatcher()
    {
        if (_systemWatcher is not null) return;

        _systemWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _systemWatcher.Tick += (_, _) =>
        {
            if (Mode != ThemeMode.System) return;
            var dark = IsSystemInDarkMode();
            if (dark == _systemWasDark) return;
            _systemWasDark = dark;
            Apply();
        };
        _systemWatcher.Start();
    }

    /// <summary>取当前生效的色阶：自定义色优先（由任意色推导），否则用预设。</summary>
    private static AccentPalette ResolvePalette(AccentTheme accent)
        => TryParseColor(SettingsStore.Current.CustomAccentColor, out var custom)
            ? DerivePalette(custom)
            : Accents.TryGetValue(accent, out var found) ? found : Accents[AccentTheme.Default];

    /// <summary>
    /// 由任意颜色推导一整套色阶，尽量贴近手工预设的关系：Bright 就是选中的颜色
    /// （先做饱和度 / 明度归一化，保证白字按钮可读），Base / Deep / Hover 依次压暗，Soft / Faint 是浅色底。
    /// </summary>
    private static AccentPalette DerivePalette(Color main)
    {
        var (h, s, l) = ToHsl(main);

        // 归一化：太亮白字看不清，太暗按钮糊成一团；饱和度太低就不像「强调色」
        s = Math.Clamp(s, 0.35, 0.95);
        l = Math.Clamp(l, 0.38, 0.62);

        var bright = FromHsl(h, s, l);

        return new AccentPalette(
            Mix(bright, Colors.Black, 0.52),
            Mix(bright, Colors.Black, 0.26),
            bright,
            Mix(bright, Colors.Black, 0.08),
            Mix(bright, Colors.White, 0.81),
            Mix(bright, Colors.White, 0.93));
    }

    /// <summary>RGB → HSL（H 0-360，S/L 0-1）。取色器用来把当前色同步到滑块。</summary>
    public static (double H, double S, double L) ToHsl(Color color)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var l = (max + min) / 2;

        if (delta < 1e-6) return (0, 0, l);

        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-6) h = (g - b) / delta + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-6) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;

        return (h * 60, s, l);
    }

    /// <summary>HSL → RGB（H 0-360，S/L 0-1）。取色器用来把三个滑块换算成颜色。</summary>
    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static Dictionary<string, Color> BuildBrushes(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var brushes = new Dictionary<string, Color>(40);

        // 强调色：Base 用于图标与点睛，Bright 用于主要按钮与胶囊
        brushes["Accent.Deep"] = dark ? Mix(palette.Deep, Colors.Black, 0.15) : palette.Deep;
        brushes["Accent.Base"] = dark ? Lighten(palette.Base, 0.18) : palette.Base;
        brushes["Accent.Bright"] = palette.Bright;
        brushes["Accent.Hover"] = dark ? Lighten(palette.Bright, 0.08) : palette.Hover;
        brushes["Accent.Soft"] = dark ? Mix(palette.Base, DarkInk, 0.42) : palette.Soft;
        brushes["Accent.Faint"] = dark ? Mix(palette.Base, DarkInk, 0.20) : palette.Faint;

        // 文字
        brushes["Text.Primary"] = dark ? ColorOf("#E9ECF2") : ColorOf("#2B3242");
        brushes["Text.Secondary"] = dark ? ColorOf("#AEB6C6") : ColorOf("#5F6B80");
        brushes["Text.Tertiary"] = dark ? ColorOf("#7C8496") : ColorOf("#8B94A6");
        brushes["Text.Disabled"] = dark ? ColorOf("#5A6274") : ColorOf("#C2C8D4");
        brushes["Text.OnAccent"] = Colors.White;

        // 承载面
        brushes["Surface.Window"] = dark ? ColorOf("#161A22") : ColorOf("#F6F7FA");
        brushes["Surface.Panel"] = dark ? ColorOf("#1D222C") : ColorOf("#EFF1F6");

        // 侧栏与标题栏用半透明版本：后续的个性化背景要能透到整个窗口，不能只铺内容区。
        brushes["Surface.PanelGlass"] = ScaleAlpha(PanelGlassBase(dark), PanelGlassAlpha);

        // 卡片也半透明，但比侧栏稍实一点，保证卡片里文字密集处对比度够。
        // 只给 SurfaceCard 模板用；Surface.Card 保持不透明，否则嵌在卡片里的面板和弹窗
        // 会变成「半透明套半透明」，叠出来的通透度不可控。
        brushes["Surface.CardGlass"] = ScaleAlpha(CardGlassBase(dark), CardGlassAlpha);
        brushes["Surface.Card"] = dark ? ColorOf("#212833") : Colors.White;
        brushes["Surface.CardHover"] = dark ? ColorOf("#283040") : ColorOf("#F7F9FC");

        // 内凹面（列表行、输入框这类压在承载面上的小块）默认不透明，但也要跟着「卡片透明度」走：
        // 否则卡片透了、里面的列表行还是实心的，整块看起来像没变（常用网站的列表就是这种）。
        brushes["Surface.Sunken"] = ScaleAlpha(SunkenBase(dark));
        brushes["Surface.Overlay"] = dark ? ColorOf("#B3000000") : ColorOf("#59000000");

        // 游戏内 HUD 的文字色：固定浅色，不跟主题走（HUD 底色固定在窗口 XAML 里）
        brushes["Text.OnHud"] = ColorOf("#FFFFFF");
        brushes["Text.OnHudDim"] = ColorOf("#D8DEE8");
        brushes["Text.OnHudFaint"] = ColorOf("#AEB6C4");

        // 描边
        brushes["Border.Default"] = dark ? ColorOf("#2E3745") : ColorOf("#E2E6EF");
        brushes["Border.Strong"] = dark ? ColorOf("#414C5E") : ColorOf("#CBD2E0");

        // 状态
        brushes["Status.Warn"] = dark ? ColorOf("#E0A94A") : ColorOf("#C07A12");
        brushes["Status.WarnSoft"] = dark ? ColorOf("#33291A") : ColorOf("#FCF3E3");
        brushes["Status.Danger"] = dark ? ColorOf("#E8735F") : ColorOf("#C0392B");
        brushes["Status.DangerSoft"] = dark ? ColorOf("#33211E") : ColorOf("#FBE9E7");
        brushes["Status.Success"] = dark ? ColorOf("#6FAF63") : ColorOf("#3E8E4E");

        // 侧栏导航
        brushes["Nav.ItemHover"] = dark ? ColorOf("#242B37") : ColorOf("#E8EBF3");
        brushes["Nav.ItemActive"] = dark ? ColorOf("#2C3648") : ColorOf("#DCE3F5");
        brushes["Nav.Indicator"] = palette.Bright;
        brushes["Nav.Text"] = dark ? ColorOf("#9AA4B6") : ColorOf("#5C6880");
        brushes["Nav.TextActive"] = dark ? Lighten(palette.Bright, 0.35) : palette.Base;

        return brushes;
    }

    /// <summary>窗口整体底纹：极淡的冷色斜向渐变，卡片浮起来时不至于贴在一块死板上。</summary>
    private static LinearGradientBrush BuildWindowGradient(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var edge = dark
            ? Mix(ColorOf("#161A22"), palette.Base, 0.16)
            : Mix(palette.Soft, ColorOf("#FCFDFF"), 0.52);
        var middle = dark ? Mix(ColorOf("#161A22"), palette.Base, 0.05) : ColorOf("#F8FAFD");

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.88, 0),
            EndPoint = new Point(0.12, 1)
        };
        brush.GradientStops.Add(new GradientStop(edge, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.42));
        brush.GradientStops.Add(new GradientStop(edge, 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush BuildCardCover(bool dark, AccentTheme accent)
    {
        var palette = ResolvePalette(accent);

        var from = dark ? Mix(palette.Deep, Colors.Black, 0.35) : palette.Deep;
        var to = dark ? Mix(palette.Base, Colors.Black, 0.25) : palette.Bright;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>深色主题下把强调色压向这个墨色，避免浅色底把界面顶得发白。</summary>
    private static Color DarkInk => ColorOf("#20262F");

    /// <summary>卡片承载面底色（不含 alpha）。alpha 由 <see cref="ScaleAlpha"/> 按设置叠上。</summary>
    private static Color CardGlassBase(bool dark) => dark ? ColorOf("#212833") : Colors.White;

    /// <summary>侧栏 / 标题栏等组件承载面底色（不含 alpha）。</summary>
    private static Color PanelGlassBase(bool dark) => dark ? ColorOf("#1D222C") : ColorOf("#EFF1F6");

    /// <summary>内凹面底色（不含 alpha）。它默认就是实心的，所以基准 alpha 为 1。</summary>
    private static Color SunkenBase(bool dark) => dark ? ColorOf("#1A1F28") : ColorOf("#F1F3F8");

    /// <summary>卡片承载面的基准不透明度（0xD1 ≈ 82%），再乘用户设置里的透明度。</summary>
    private const double CardGlassAlpha = 0xD1 / 255d;

    /// <summary>组件承载面的基准不透明度（0xC7 ≈ 78%）。</summary>
    private const double PanelGlassAlpha = 0xC7 / 255d;

    /// <summary>把用户设置里的承载面透明度乘到基准 alpha 上，得到最终带 alpha 的底色。</summary>
    private static Color ScaleAlpha(Color baseColor, double baseAlpha = 1.0)
    {
        var opacity = SettingsStore.Current.SurfaceOpacity;
        var alpha = (byte)Math.Round(Math.Clamp(baseAlpha * opacity, 0, 1) * 255);
        return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Mix(Color from, Color to, double t) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * t),
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));

    private static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    private static Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
