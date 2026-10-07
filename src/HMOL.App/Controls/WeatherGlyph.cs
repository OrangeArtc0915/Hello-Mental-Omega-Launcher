using System.Windows.Media;
using HMOL.Core.Logging;
using HMOL.Core.Weather;
using HMOL.Core.Localization;

namespace HMOL.App.Controls;

/// <summary>
/// 天气状况 → 描边几何图形（24×24 的 viewBox，与图标包同一套坐标习惯）。
///
/// 项目自带的图标包（lucide）里没有云 / 太阳 / 雨雪这类天气图形，这里直接用 WPF 的几何小语言画出来：
/// 不新增图片素材，颜色仍由调用方按语义资源键设置，主题与强调色变了跟着走。
/// </summary>
internal static class WeatherGlyph
{
    // ————— 基础图形：云 + 雨 / 雪 / 雷 / 雾 —————
    private const string CloudBody =
        "M4 14.899A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 2.5 8.242";

    private static readonly Geometry? Sun = Parse(
        "M12 2v2 M12 20v2 M2 12h2 M20 12h2 " +
        "m4.93 4.93 1.41 1.41 m17.66 17.66 1.41 1.41 " +
        "m19.07 4.93-1.41 1.41 m6.34 17.66-1.41 1.41 " +
        "M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0");

    private static readonly Geometry? CloudSun = Parse(
        "M12 2v2 m4.93 4.93 1.41 1.41 M20 12h2 m19.07 4.93-1.41 1.41 " +
        "M15.947 12.65a4 4 0 0 0-5.925-4.128 " +
        "M13 22H7a5 5 0 1 1 4.9-6H13a3 3 0 0 1 0 6Z");

    private static readonly Geometry? Cloud = Parse(
        "M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z");

    private static readonly Geometry? CloudFog = Parse(
        CloudBody + " M16 17H7 M17 21H9");

    private static readonly Geometry? CloudDrizzle = Parse(
        CloudBody + " M8 19v1 M8 14v1 M16 19v1 M16 14v1 M12 21v1 M12 16v1");

    private static readonly Geometry? CloudRain = Parse(
        CloudBody + " M16 14v6 M8 14v6 M12 16v6");

    private static readonly Geometry? CloudFreezingRain = Parse(
        CloudBody + " M16 14v4 M8 14v4 M16 20h.01 M8 20h.01 M12 18h.01");

    private static readonly Geometry? CloudSnow = Parse(
        CloudBody + " M8 15h.01 M8 19h.01 M12 17h.01 M12 21h.01 M16 15h.01 M16 19h.01");

    private static readonly Geometry? CloudThunder = Parse(
        "M6 16.326A7 7 0 1 1 15.71 8h1.79a4.5 4.5 0 0 1 .5 8.973 m13 12-3 5h4l-3 5");

    /// <summary>取某天气状况对应的几何图形；解析不出来时返回 null（调用方保留空图形）。</summary>
    public static Geometry? For(WeatherKind kind) => kind switch
    {
        WeatherKind.Clear => Sun,
        WeatherKind.PartlyCloudy => CloudSun,
        WeatherKind.Fog => CloudFog,
        WeatherKind.Drizzle => CloudDrizzle,
        WeatherKind.Rain => CloudRain,
        WeatherKind.FreezingRain => CloudFreezingRain,
        WeatherKind.Snow => CloudSnow,
        WeatherKind.Thunderstorm => CloudThunder,
        _ => Cloud
    };

    private static Geometry? Parse(string data)
    {
        try
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("天气图标解析失败：{0}", ex.Message));
            return null;
        }
    }
}
