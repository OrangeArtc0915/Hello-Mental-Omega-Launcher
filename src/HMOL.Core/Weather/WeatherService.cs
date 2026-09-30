using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Weather;

/// <summary>天气状况大类，用于挑选图标与配色。</summary>
public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    FreezingRain,
    Snow,
    Thunderstorm,
    Unknown
}

/// <summary>一次现实天气查询的结果（不可变）。</summary>
public sealed record WeatherSnapshot(
    string City,
    double Temperature,
    double ApparentTemperature,
    double Humidity,
    double WindSpeed,
    WeatherKind Kind,
    string Description,
    double? TodayHigh,
    double? TodayLow,
    DateTimeOffset FetchedAt,
    int? WeatherCode);

/// <summary>
/// 现实天气（非游戏内天气）：数据源为 Open-Meteo，免费、无需 API Key。
/// 地理编码：geocoding-api.open-meteo.com；天气预报：api.open-meteo.com。
///
/// 缓存策略：查询结果连同抓取时刻落到 <c>Data\Cache\weather.json</c>，30 分钟内进入主页直接复用（不再发请求）；
/// 城市 → 经纬度的解析结果在同一进程内 memo 一次，避免每次刷新都多打一次 geocoding。
/// 任何网络失败都不抛异常：优先退回过期缓存，其次返回 null 并把可读中文原因写进 <see cref="LastError"/>。
/// </summary>
public static class WeatherService
{
    /// <summary>缓存有效期。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = true };

    /// <summary>城市 → 经纬度 的进程内 memo（同一城市不重复请求 geocoding）。</summary>
    private static readonly Dictionary<string, (double Latitude, double Longitude)> GeocodeMemo =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次失败原因（可读中文）；成功时为 null。</summary>
    public static string? LastError { get; private set; }

    /// <summary>天气缓存文件。与门锁缓存同处 Data\Cache，文件名区分开。</summary>
    private static string CacheFile
    {
        get
        {
            Paths.Init();
            return Path.Combine(Paths.Cache, "weather.json");
        }
    }

    /// <summary>把城市名解析成经纬度（失败返回 null，原因见 <see cref="LastError"/>）。</summary>
    public static async Task<(double Latitude, double Longitude)?> GeocodeAsync(string? cityName,
        CancellationToken token = default)
    {
        var city = (cityName ?? string.Empty).Trim();

        if (city.Length == 0)
        {
            LastError = "还没设置城市";
            return null;
        }

        if (GeocodeMemo.TryGetValue(city, out var memoized))
        {
            LastError = null;
            return memoized;
        }

        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                      Uri.EscapeDataString(city) + "&count=1&language=zh&format=json";

            using var document = await GetJsonAsync(url, token).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                LastError = $"找不到城市「{city}」，请确认城市名后再试";
                return null;
            }

            var item = results[0];
            var latitude = DoubleOf(item, "latitude");
            var longitude = DoubleOf(item, "longitude");

            if (latitude is null || longitude is null)
            {
                LastError = $"城市「{city}」没有返回经纬度";
                return null;
            }

            GeocodeMemo[city] = (latitude.Value, longitude.Value);

            LastError = null;
            return (latitude.Value, longitude.Value);
        }
        catch (OperationCanceledException)
        {
            LastError = "解析城市超时，请检查网络";
            return null;
        }
        catch (HttpRequestException ex)
        {
            // 断网 / 代理不可用 / DNS 失败等都走这里，提示语用中文起头，细节跟在后面方便排查
            LastError = $"连不上 Open-Meteo，无法解析城市「{city}」（{ex.Message}）";
            Log.Info($"天气地理编码失败：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"解析城市失败：{ex.Message}";
            Log.Info($"天气地理编码失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>取当前天气。失败返回 null 并把原因写进 <see cref="LastError"/>；有旧缓存时退回过期缓存。</summary>
    public static async Task<WeatherSnapshot?> GetAsync(string? cityName, bool forceRefresh = false,
        CancellationToken token = default)
    {
        var city = (cityName ?? string.Empty).Trim();

        if (city.Length == 0)
        {
            LastError = "还没设置城市";
            return null;
        }

        var cache = TryReadCache(city);

        if (!forceRefresh && cache is not null && DateTimeOffset.Now - cache.FetchedAt < CacheTtl)
        {
            LastError = null;
            return cache.ToSnapshot();
        }

        var coordinates = await GeocodeAsync(city, token).ConfigureAwait(false);

        if (coordinates is null)
        {
            if (cache is not null)
            {
                Log.Info($"天气不可用（{LastError}），退回缓存：{cache.FetchedAt:MM-dd HH:mm}");
                return cache.ToSnapshot();
            }

            Log.Info($"天气不可用：{LastError}");
            return null;
        }

        var fetched = await FetchAsync(city, coordinates.Value, token).ConfigureAwait(false);

        if (fetched is null)
        {
            if (cache is not null)
            {
                Log.Info($"天气不可用（{LastError}），退回缓存：{cache.FetchedAt:MM-dd HH:mm}");
                return cache.ToSnapshot();
            }

            Log.Info($"天气不可用：{LastError}");
            return null;
        }

        WriteCache(fetched);

        LastError = null;
        return fetched;
    }

    /// <summary>
    /// Open-Meteo 的 WMO weather code → 状况大类 + 中文描述。
    /// 覆盖 WMO 4677 里 Open-Meteo 会返回的全部取值（含 0-3、45/48、51-57、61-67、71-77、80-82、85/86、95-99）。
    /// </summary>
    public static (WeatherKind Kind, string Description) Classify(int? code) => code switch
    {
        0 => (WeatherKind.Clear, "晴"),
        1 => (WeatherKind.PartlyCloudy, "晴间多云"),
        2 => (WeatherKind.PartlyCloudy, "多云"),
        3 => (WeatherKind.Cloudy, "阴"),

        45 => (WeatherKind.Fog, "雾"),
        48 => (WeatherKind.Fog, "雾凇"),

        51 => (WeatherKind.Drizzle, "小毛毛雨"),
        53 => (WeatherKind.Drizzle, "毛毛雨"),
        55 => (WeatherKind.Drizzle, "大毛毛雨"),
        56 => (WeatherKind.FreezingRain, "轻度冻毛毛雨"),
        57 => (WeatherKind.FreezingRain, "强冻毛毛雨"),

        61 => (WeatherKind.Rain, "小雨"),
        63 => (WeatherKind.Rain, "中雨"),
        65 => (WeatherKind.Rain, "大雨"),
        66 => (WeatherKind.FreezingRain, "轻度冻雨"),
        67 => (WeatherKind.FreezingRain, "强冻雨"),

        71 => (WeatherKind.Snow, "小雪"),
        73 => (WeatherKind.Snow, "中雪"),
        75 => (WeatherKind.Snow, "大雪"),
        77 => (WeatherKind.Snow, "米雪（霰）"),

        80 => (WeatherKind.Rain, "小阵雨"),
        81 => (WeatherKind.Rain, "阵雨"),
        82 => (WeatherKind.Rain, "强阵雨"),

        85 => (WeatherKind.Snow, "小阵雪"),
        86 => (WeatherKind.Snow, "大阵雪"),

        95 => (WeatherKind.Thunderstorm, "雷阵雨"),
        96 => (WeatherKind.Thunderstorm, "雷阵雨伴小冰雹"),
        99 => (WeatherKind.Thunderstorm, "雷阵雨伴大冰雹"),

        _ => (WeatherKind.Unknown, "未知天气")
    };

    // ————— 网络 —————

    private static async Task<WeatherSnapshot?> FetchAsync(string city,
        (double Latitude, double Longitude) coordinates, CancellationToken token)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={coordinates.Latitude.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={coordinates.Longitude.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                  "&timezone=auto&forecast_days=1";

        try
        {
            using var document = await GetJsonAsync(url, token).ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("current", out var current))
            {
                LastError = "天气接口没有返回当前数据";
                return null;
            }

            var code = IntOf(current, "weather_code");
            var (kind, description) = Classify(code);

            double? high = null;
            double? low = null;

            if (root.TryGetProperty("daily", out var daily))
            {
                high = ElementDouble(daily, "temperature_2m_max", 0);
                low = ElementDouble(daily, "temperature_2m_min", 0);
            }

            return new WeatherSnapshot(
                city,
                DoubleOf(current, "temperature_2m") ?? 0,
                DoubleOf(current, "apparent_temperature") ?? 0,
                DoubleOf(current, "relative_humidity_2m") ?? 0,
                DoubleOf(current, "wind_speed_10m") ?? 0,
                kind,
                description,
                high,
                low,
                DateTimeOffset.Now,
                code);
        }
        catch (OperationCanceledException)
        {
            LastError = "天气请求超时，请检查网络";
            return null;
        }
        catch (HttpRequestException ex)
        {
            LastError = $"连不上 Open-Meteo 天气服务（{ex.Message}）";
            Log.Info($"获取天气失败：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"天气请求失败：{ex.Message}";
            Log.Info($"获取天气失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken token)
    {
        using var response = await Client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
    }

    // ————— 缓存 —————

    private static WeatherCache? TryReadCache(string city)
    {
        try
        {
            var file = CacheFile;
            if (!File.Exists(file)) return null;

            var cache = JsonSerializer.Deserialize<WeatherCache>(File.ReadAllText(file), CacheOptions);

            return cache is { Version: WeatherCache.CurrentVersion } &&
                   string.Equals(cache.City, city, StringComparison.OrdinalIgnoreCase)
                ? cache
                : null;
        }
        catch (Exception ex)
        {
            Log.Info($"读取天气缓存失败（按无缓存处理）：{ex.Message}");
            return null;
        }
    }

    private static void WriteCache(WeatherSnapshot snapshot)
    {
        try
        {
            var cache = new WeatherCache
            {
                City = snapshot.City,
                Temperature = snapshot.Temperature,
                ApparentTemperature = snapshot.ApparentTemperature,
                Humidity = snapshot.Humidity,
                WindSpeed = snapshot.WindSpeed,
                Kind = snapshot.Kind,
                Description = snapshot.Description,
                TodayHigh = snapshot.TodayHigh,
                TodayLow = snapshot.TodayLow,
                WeatherCode = snapshot.WeatherCode,
                FetchedAt = snapshot.FetchedAt
            };

            Directory.CreateDirectory(Paths.Cache);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, CacheOptions));
        }
        catch (Exception ex)
        {
            Log.Info($"写入天气缓存失败（不影响本次显示）：{ex.Message}");
        }
    }

    /// <summary>天气缓存文件结构。改字段时把 <see cref="CurrentVersion"/> 加一，旧缓存自然失效。</summary>
    private sealed class WeatherCache
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public string City { get; set; } = string.Empty;

        public double Temperature { get; set; }

        public double ApparentTemperature { get; set; }

        public double Humidity { get; set; }

        public double WindSpeed { get; set; }

        public WeatherKind Kind { get; set; }

        public string Description { get; set; } = string.Empty;

        public double? TodayHigh { get; set; }

        public double? TodayLow { get; set; }

        public int? WeatherCode { get; set; }

        public DateTimeOffset FetchedAt { get; set; }

        public WeatherSnapshot ToSnapshot() => new(
            City, Temperature, ApparentTemperature, Humidity, WindSpeed,
            Kind, Description, TodayHigh, TodayLow, FetchedAt, WeatherCode);
    }

    // ————— JSON 取值助手 —————

    private static int? IntOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static double? DoubleOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private static double? ElementDouble(JsonElement element, string name, int index)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        if (index >= array.GetArrayLength()) return null;

        var value = array[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : null;
    }
}
