using System.Windows;
using System.Windows.Media;

namespace HMOL.App.Controls.Svg;

/// <summary>
/// 描边式 SVG 图标。以 24x24 的 viewBox 渲染，线条粗细按 viewBox 单位给出。
/// </summary>
public sealed class SvgIcon : FrameworkElement
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(SvgIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(SvgIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>线宽，单位与图标自身的 viewBox 一致（默认取 SVG 文件里的 stroke-width）。</summary>
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(SvgIcon),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 16 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var source = SvgIconLoader.Get(Icon);
        if (source is null) return;

        var brush = IconBrush ?? TryFindResource("Text.Primary") as Brush;
        if (brush is null) return;

        var available = Math.Min(ActualWidth, ActualHeight);
        if (available <= 0) return;

        var viewBoxSize = Math.Max(source.Width, source.Height);
        var scale = available / viewBoxSize;
        var offsetX = (ActualWidth - source.Width * scale) / 2;
        var offsetY = (ActualHeight - source.Height * scale) / 2;

        var pen = new Pen(brush, StrokeThickness > 0 ? StrokeThickness : source.StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
            MiterLimit = 1
        };
        pen.Freeze();

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, source.Geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
