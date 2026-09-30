using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HMOL.App.Controls.Svg;

/// <summary>
/// SVG path 的 d 属性解析器。支持 M/L/H/V/C/S/Q/T/A/Z 及其相对形式。
/// 只覆盖 lucide 图标集实际用到的命令，未知命令会被安全跳过。
/// </summary>
internal static class SvgPathParser
{
    public static PathGeometry Parse(string data)
    {
        var geometry = new PathGeometry();
        if (string.IsNullOrWhiteSpace(data)) return geometry;

        var tokens = Tokenize(data);
        if (tokens.Count == 0) return geometry;

        var index = 0;
        var command = '\0';
        var previousCommand = '\0';
        double cx = 0, cy = 0, startX = 0, startY = 0;
        double previousCubicX = 0, previousCubicY = 0;
        double previousQuadX = 0, previousQuadY = 0;
        PathFigure? figure = null;

        while (index < tokens.Count)
        {
            if (tokens[index] is char ch)
            {
                command = ch;
                index++;
            }
            else if (command == '\0')
            {
                break;
            }
            else if (char.ToUpperInvariant(command) == 'M')
            {
                // 一个 M 之后的连续坐标按 LineTo 处理
                command = char.IsLower(command) ? 'l' : 'L';
            }
            else if (char.ToUpperInvariant(command) == 'Z')
            {
                if (figure is not null) figure.IsClosed = true;
                cx = startX;
                cy = startY;
                command = '\0';
                previousCommand = 'Z';
                continue;
            }

            var upper = char.ToUpperInvariant(command);
            var relative = char.IsLower(command);
            var ox = relative ? cx : 0;
            var oy = relative ? cy : 0;

            switch (upper)
            {
                case 'M':
                {
                    if (!TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;
                    cx = ox + x;
                    cy = oy + y;
                    figure = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = false };
                    geometry.Figures.Add(figure);
                    startX = cx;
                    startY = cy;
                    break;
                }

                case 'L':
                {
                    if (!TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;
                    cx = ox + x;
                    cy = oy + y;
                    figure?.Segments.Add(new LineSegment(new Point(cx, cy), true));
                    break;
                }

                case 'H':
                {
                    if (!TryNumber(tokens, ref index, out var x)) return geometry;
                    cx = ox + x;
                    figure?.Segments.Add(new LineSegment(new Point(cx, cy), true));
                    break;
                }

                case 'V':
                {
                    if (!TryNumber(tokens, ref index, out var y)) return geometry;
                    cy = oy + y;
                    figure?.Segments.Add(new LineSegment(new Point(cx, cy), true));
                    break;
                }

                case 'C':
                {
                    if (!TryNumber(tokens, ref index, out var x1) || !TryNumber(tokens, ref index, out var y1) ||
                        !TryNumber(tokens, ref index, out var x2) || !TryNumber(tokens, ref index, out var y2) ||
                        !TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;

                    var control1 = new Point(ox + x1, oy + y1);
                    var control2 = new Point(ox + x2, oy + y2);
                    var end = new Point(ox + x, oy + y);
                    figure?.Segments.Add(new BezierSegment(control1, control2, end, true));
                    previousCubicX = control2.X;
                    previousCubicY = control2.Y;
                    cx = end.X;
                    cy = end.Y;
                    break;
                }

                case 'S':
                {
                    if (!TryNumber(tokens, ref index, out var x2) || !TryNumber(tokens, ref index, out var y2) ||
                        !TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;

                    var smooth = previousCommand is 'C' or 'c' or 'S' or 's';
                    var control1 = smooth
                        ? new Point(2 * cx - previousCubicX, 2 * cy - previousCubicY)
                        : new Point(cx, cy);
                    var control2 = new Point(ox + x2, oy + y2);
                    var end = new Point(ox + x, oy + y);
                    figure?.Segments.Add(new BezierSegment(control1, control2, end, true));
                    previousCubicX = control2.X;
                    previousCubicY = control2.Y;
                    cx = end.X;
                    cy = end.Y;
                    break;
                }

                case 'Q':
                {
                    if (!TryNumber(tokens, ref index, out var qx) || !TryNumber(tokens, ref index, out var qy) ||
                        !TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;

                    var control = new Point(ox + qx, oy + qy);
                    var end = new Point(ox + x, oy + y);
                    figure?.Segments.Add(new QuadraticBezierSegment(control, end, true));
                    previousQuadX = control.X;
                    previousQuadY = control.Y;
                    cx = end.X;
                    cy = end.Y;
                    break;
                }

                case 'T':
                {
                    if (!TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;

                    var smooth = previousCommand is 'Q' or 'q' or 'T' or 't';
                    var control = smooth
                        ? new Point(2 * cx - previousQuadX, 2 * cy - previousQuadY)
                        : new Point(cx, cy);
                    var end = new Point(ox + x, oy + y);
                    figure?.Segments.Add(new QuadraticBezierSegment(control, end, true));
                    previousQuadX = control.X;
                    previousQuadY = control.Y;
                    cx = end.X;
                    cy = end.Y;
                    break;
                }

                case 'A':
                {
                    if (!TryNumber(tokens, ref index, out var rx) || !TryNumber(tokens, ref index, out var ry) ||
                        !TryNumber(tokens, ref index, out var rotation) ||
                        !TryNumber(tokens, ref index, out var largeArc) || !TryNumber(tokens, ref index, out var sweep) ||
                        !TryNumber(tokens, ref index, out var x) || !TryNumber(tokens, ref index, out var y))
                        return geometry;

                    var end = new Point(ox + x, oy + y);
                    if (rx <= 0 || ry <= 0 || (Math.Abs(end.X - cx) < 0.0001 && Math.Abs(end.Y - cy) < 0.0001))
                    {
                        figure?.Segments.Add(new LineSegment(end, true));
                    }
                    else
                    {
                        figure?.Segments.Add(new ArcSegment(end, new Size(rx, ry), rotation,
                            largeArc != 0, sweep != 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                            true));
                    }

                    cx = end.X;
                    cy = end.Y;
                    break;
                }

                default:
                    // 不认识的指令直接跳过，绝不抛异常：一个图标画不出来，不能让整个界面崩
                    index++;
                    break;
            }

            previousCommand = command;
        }

        return geometry;
    }

    private static bool TryNumber(List<object> tokens, ref int index, out double value)
    {
        if (index < tokens.Count && tokens[index] is double number)
        {
            value = number;
            index++;
            return true;
        }

        value = 0;
        return false;
    }

    private static List<object> Tokenize(string data)
    {
        var tokens = new List<object>(data.Length / 2);
        var index = 0;

        while (index < data.Length)
        {
            var current = data[index];

            if (char.IsWhiteSpace(current) || current == ',')
            {
                index++;
                continue;
            }

            if (char.IsLetter(current))
            {
                tokens.Add(current);
                index++;
                continue;
            }

            var start = index;
            if (current is '+' or '-') index++;

            while (index < data.Length && (char.IsDigit(data[index]) || data[index] == '.')) index++;

            if (index < data.Length && data[index] is 'e' or 'E')
            {
                index++;
                if (index < data.Length && data[index] is '+' or '-') index++;
                while (index < data.Length && char.IsDigit(data[index])) index++;
            }

            var text = data[start..index];
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                tokens.Add(value);
            else
                index = start + 1;
        }

        return tokens;
    }
}
