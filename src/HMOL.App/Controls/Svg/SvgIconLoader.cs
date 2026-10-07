using System.Collections;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.App.Controls.Svg;

internal sealed class SvgIconSource
{
    public required Geometry Geometry { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required double StrokeWidth { get; init; }
}

/// <summary>
/// 从内嵌的 SVG 图标包中加载并解析图标，按名字缓存解析结果。
/// 图标标识形如 "lucide/play"，省略包名时使用默认包。
/// </summary>
internal static class SvgIconLoader
{
    private const string DefaultPack = "lucide";
    private const string AssemblyName = "HMOL";

    /// <summary>程序集里图标资源所在的内嵌资源名与路径标记。</summary>
    private const string ResourceName = AssemblyName + ".g.resources";
    private const string ResourcePathMarker = "assets/iconpacks/";

    private static readonly Regex ElementRegex = new(
        @"<\s*(path|circle|ellipse|rect|line|polyline|polygon)\b([^>]*?)/?\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AttributeRegex = new(
        @"([A-Za-z_:][\w:.-]*)\s*=\s*""([^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex CommentRegex = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<string, SvgIconSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SvgIconSource? Get(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;

        lock (Cache)
        {
            if (Cache.TryGetValue(icon, out var cached)) return cached;
            var loaded = Load(icon);
            Cache[icon] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// 枚举图标包里实际存在的图标，返回形如 <c>lucide/play</c> 的标识。
    /// 供启动自检与扩展清单的图标白名单使用：图标的唯一事实来源是内嵌资源本身，不另存一份名单。
    /// </summary>
    public static IReadOnlyList<string> AvailableKeys()
    {
        var result = new List<string>();

        using var stream = typeof(SvgIconLoader).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return result;

        using var reader = new ResourceReader(stream);

        foreach (DictionaryEntry entry in reader)
        {
            if (entry.Key is not string key) continue;

            var normalized = key.Replace('\\', '/').ToLowerInvariant();
            var index = normalized.IndexOf(ResourcePathMarker, StringComparison.Ordinal);

            if (index < 0 || !normalized.EndsWith(".svg", StringComparison.Ordinal)) continue;

            result.Add(normalized[(index + ResourcePathMarker.Length)..^4]);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static SvgIconSource? Load(string icon)
    {
        var (pack, name) = SplitIconKey(icon);
        if (string.IsNullOrEmpty(name)) return null;

        var uri = new Uri(
            $"pack://application:,,,/{AssemblyName};component/Assets/IconPacks/{pack}/{name}.svg",
            UriKind.Absolute);

        string content;
        try
        {
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is null)
            {
                Log.Warn(Loc.F("图标资源不存在：{0}", icon));
                return null;
            }

            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("图标读取失败 {0}：{1}", icon, ex.Message));
            return null;
        }

        try
        {
            return Parse(content, icon);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("图标解析失败 {0}：{1}", icon, ex.Message));
            return null;
        }
    }

    private static (string Pack, string Name) SplitIconKey(string icon)
    {
        var normalized = icon.Replace('\\', '/').Trim();
        var slash = normalized.IndexOf('/');
        if (slash < 0) return (DefaultPack, normalized);

        var pack = normalized[..slash];
        var name = normalized[(slash + 1)..];
        return (string.IsNullOrWhiteSpace(pack) ? DefaultPack : pack, name);
    }

    private static SvgIconSource? Parse(string content, string icon)
    {
        var body = CommentRegex.Replace(content, string.Empty);

        var width = 24d;
        var height = 24d;
        var strokeWidth = 2d;

        var svgTag = Regex.Match(body, @"<svg\b([^>]*)>", RegexOptions.IgnoreCase);
        if (svgTag.Success)
        {
            var rootAttributes = ReadAttributes(svgTag.Groups[1].Value);
            if (rootAttributes.TryGetValue("viewbox", out var viewBox))
            {
                var parts = viewBox.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4)
                {
                    width = ToDouble(parts[2], 24);
                    height = ToDouble(parts[3], 24);
                }
            }

            if (rootAttributes.TryGetValue("stroke-width", out var strokeText))
                strokeWidth = ToDouble(strokeText, 2);
        }

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };

        foreach (Match element in ElementRegex.Matches(body))
        {
            var attributes = ReadAttributes(element.Groups[2].Value);
            var geometry = BuildGeometry(element.Groups[1].Value.ToLowerInvariant(), attributes);
            if (geometry is not null) group.Children.Add(geometry);
        }

        if (group.Children.Count == 0)
        {
            Log.Warn(Loc.F("图标内没有可渲染的图形，可能是解析器不支持的写法：{0}", icon));
            return null;
        }

        group.Freeze();

        return new SvgIconSource
        {
            Geometry = group,
            Width = width <= 0 ? 24 : width,
            Height = height <= 0 ? 24 : height,
            StrokeWidth = strokeWidth <= 0 ? 2 : strokeWidth
        };
    }

    private static Geometry? BuildGeometry(string element, Dictionary<string, string> attributes)
    {
        switch (element)
        {
            case "path":
            {
                if (!attributes.TryGetValue("d", out var data) || string.IsNullOrWhiteSpace(data)) return null;
                try
                {
                    var geometry = SvgPathParser.Parse(data);
                    return geometry.Figures.Count == 0 ? null : geometry;
                }
                catch
                {
                    return null;
                }
            }

            case "circle":
            {
                var cx = Value(attributes, "cx", 0);
                var cy = Value(attributes, "cy", 0);
                var r = Value(attributes, "r", 0);
                return r <= 0 ? null : new EllipseGeometry(new Point(cx, cy), r, r);
            }

            case "ellipse":
            {
                var cx = Value(attributes, "cx", 0);
                var cy = Value(attributes, "cy", 0);
                var rx = Value(attributes, "rx", 0);
                var ry = Value(attributes, "ry", 0);
                return rx <= 0 || ry <= 0 ? null : new EllipseGeometry(new Point(cx, cy), rx, ry);
            }

            case "rect":
            {
                var x = Value(attributes, "x", 0);
                var y = Value(attributes, "y", 0);
                var w = Value(attributes, "width", 0);
                var h = Value(attributes, "height", 0);
                if (w <= 0 || h <= 0) return null;
                var rx = Value(attributes, "rx", Value(attributes, "ry", 0));
                return new RectangleGeometry(new Rect(x, y, w, h), rx, rx);
            }

            case "line":
            {
                return new LineGeometry(
                    new Point(Value(attributes, "x1", 0), Value(attributes, "y1", 0)),
                    new Point(Value(attributes, "x2", 0), Value(attributes, "y2", 0)));
            }

            case "polyline":
            case "polygon":
            {
                if (!attributes.TryGetValue("points", out var points) || string.IsNullOrWhiteSpace(points))
                    return null;

                var numbers = points
                    .Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(text => ToDouble(text, double.NaN))
                    .Where(number => !double.IsNaN(number))
                    .ToArray();

                if (numbers.Length < 4) return null;

                var pairs = new List<Point>(numbers.Length / 2);
                for (var i = 0; i + 1 < numbers.Length; i += 2) pairs.Add(new Point(numbers[i], numbers[i + 1]));

                var figure = new PathFigure
                {
                    StartPoint = pairs[0],
                    IsClosed = element == "polygon",
                    IsFilled = false
                };
                figure.Segments.Add(new PolyLineSegment(pairs.Skip(1), true));

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                return geometry;
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> ReadAttributes(string tagContent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(tagContent))
            result[match.Groups[1].Value.ToLowerInvariant()] = match.Groups[2].Value;
        return result;
    }

    private static double Value(Dictionary<string, string> attributes, string key, double fallback)
        => attributes.TryGetValue(key, out var text) ? ToDouble(text, fallback) : fallback;

    private static double ToDouble(string text, double fallback)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
