using System.Windows;
using System.Windows.Media;
using HMOL.Core.App;

namespace HMOL.App.Theme;

/// <summary>
/// 界面外观（字体 / 圆角）的应用。这些值以资源（<c>AppFont</c>、<c>Radius.*</c>）形式被 XAML
/// 以 <c>DynamicResource</c> 引用，所以这里改写 <see cref="Application.Resources"/> 就能实时生效，
/// 不必重建窗口。基准值取自 <c>Resources\Colors.xaml</c>。
/// </summary>
internal static class AppearanceService
{
    /// <summary>内置默认字体，与 Colors.xaml 里的 AppFont 保持一致。</summary>
    public const string DefaultFont = "Microsoft YaHei UI, Segoe UI, Arial";

    /// <summary>颜色 / 尺寸基准：Colors.xaml 里的默认圆角。胶囊（Pill）不参与缩放。</summary>
    private static readonly (string Key, double Basis)[] Radii =
    [
        ("Radius.Small", 6),
        ("Radius.Button", 8),
        ("Radius.Item", 10),
        ("Radius.Card", 12)
    ];

    private static string _appliedFont = string.Empty;
    private static double _appliedScale = double.NaN;

    /// <summary>按设置应用字体与圆角。启动时调一次，设置变更时再调。</summary>
    public static void Apply()
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        ApplyFont(resources);
        ApplyRadii(resources);
    }

    private static void ApplyFont(ResourceDictionary resources)
    {
        var name = SettingsStore.Current.AppFontFamily;
        if (string.Equals(name, _appliedFont, StringComparison.Ordinal)) return;

        _appliedFont = name;

        try
        {
            resources["AppFont"] = string.IsNullOrWhiteSpace(name)
                ? new FontFamily(DefaultFont)
                : new FontFamily(name);
        }
        catch
        {
            // 字体名非法时退回默认，不让界面因为一个字体名挂掉
            resources["AppFont"] = new FontFamily(DefaultFont);
        }
    }

    private static void ApplyRadii(ResourceDictionary resources)
    {
        var scale = SettingsStore.Current.CornerRadiusScale;
        if (Math.Abs(scale - _appliedScale) < 0.001) return;

        _appliedScale = scale;

        foreach (var (key, basis) in Radii)
            resources[key] = new CornerRadius(Math.Round(basis * scale));
    }
}
