using System.Windows;
using System.Windows.Media;

namespace HMOL.App.Theme;

/// <summary>从资源字典读取主题色与主题画刷的辅助方法。</summary>
public static class ThemeColors
{
    public static Color Get(FrameworkElement element, string key, Color fallback = default)
        => element.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    public static SolidColorBrush Brush(FrameworkElement element, string key)
        => element.TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(Colors.Transparent);

    public static Color Accent(FrameworkElement element) => Get(element, "Accent.Base", Colors.SteelBlue);

    public static Color AccentBright(FrameworkElement element) => Get(element, "Accent.Bright", Colors.DodgerBlue);

    public static Color AccentHover(FrameworkElement element) => Get(element, "Accent.Hover", Colors.DodgerBlue);

    public static Color Text(FrameworkElement element) => Get(element, "Text.Primary", Colors.Black);

    public static Color TextSecondary(FrameworkElement element) => Get(element, "Text.Secondary", Colors.Gray);

    public static Color Tertiary(FrameworkElement element) => Get(element, "Text.Tertiary", Colors.Gray);

    public static Color Surface(FrameworkElement element) => Get(element, "Surface.Card", Colors.White);

    public static Color SurfaceHover(FrameworkElement element) => Get(element, "Surface.CardHover", Colors.WhiteSmoke);

    public static Color Sunken(FrameworkElement element) => Get(element, "Surface.Sunken", Colors.WhiteSmoke);

    public static Color Border(FrameworkElement element) => Get(element, "Border.Default", Colors.Gainsboro);

    public static Color BorderStrong(FrameworkElement element) => Get(element, "Border.Strong", Colors.Silver);

    public static Color NavHover(FrameworkElement element) => Get(element, "Nav.ItemHover", Colors.WhiteSmoke);

    public static Color NavActive(FrameworkElement element) => Get(element, "Nav.ItemActive", Colors.WhiteSmoke);

    public static Color Danger(FrameworkElement element) => Get(element, "Status.Danger", Colors.IndianRed);

    public static Color OnAccent(FrameworkElement element) => Get(element, "Text.OnAccent", Colors.White);

    public static Color Transparent(FrameworkElement element) => Get(element, "Common.Transparent", Colors.Transparent);

    public static Color DangerHover(FrameworkElement element)
    {
        var danger = Danger(element);
        return Color.FromRgb(
            (byte)Math.Min(255, danger.R + 24),
            (byte)Math.Max(0, danger.G - 14),
            (byte)Math.Max(0, danger.B - 14));
    }
}
