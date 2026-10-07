using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HMOL.App.Controls.Svg;
using HMOL.Core.Layout;
using HMOL.Core.Localization;

namespace HMOL.App.Layout;

/// <summary>
/// 主页控件的自由定位预览：拖动移动、8 个手柄缩放、边界裁剪、双击居中、恢复默认位置与大小。
///
/// 摆放方式与 <c>PageHome</c> 应用方案时完全一致（Margin + 左上对齐 + 显式宽高），
/// 于是「预览里看到的位置就是应用后的位置」；坐标一律按百分比写回方案记录。
/// 这里只改内存里的 <see cref="LayoutItem"/>，落盘与生效由编辑器点「保存并应用」决定。
/// </summary>
public sealed class HomeLayoutPreview : Grid
{
    /// <summary>手柄边长。</summary>
    private const double HandleSize = 11;

    /// <summary>拖动/缩放的像素下限，太小就抓不住了。</summary>
    private const double MinPixelSize = 40;

    /// <summary>对齐辅助线的判定距离（像素）：只画线提示，不做吸附。</summary>
    private const double GuideThreshold = 6;

    private static readonly string[] HandlePositions = ["tl", "t", "tr", "l", "r", "bl", "b", "br"];

    private readonly Dictionary<string, Box> _boxes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>对齐辅助线画在最上层，不参与命中测试。</summary>
    private Canvas? _guideLayer;

    private IReadOnlyList<LayoutItem> _items = [];
    private string? _selectedId;
    private DragState? _drag;

    public HomeLayoutPreview()
    {
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "Common.Transparent");

        SizeChanged += (_, _) => Relayout();

        // 点空白处取消选中；方块与手柄的事件都标了 Handled，不会走到这里
        MouseLeftButtonDown += (_, _) => Select(null);
    }

    /// <summary>选中的元素标识；没选中时为 null。</summary>
    public string? SelectedId => _selectedId;

    /// <summary>选中变化（含取消选中）。</summary>
    public event Action<string?>? SelectionChanged;

    /// <summary>用户拖动 / 缩放 / 居中 / 恢复默认之后触发，编辑器据此标记「有未保存改动」。</summary>
    public event Action? LayoutChanged;

    /// <summary>按方案（内存里的记录）重新摆一遍。方块只有这么几个，整份重建最省心。</summary>
    public void Bind(IReadOnlyList<LayoutItem> items)
    {
        _items = items;

        Children.Clear();
        _boxes.Clear();

        foreach (var info in HomeLayoutElements.All)
        {
            var box = BuildBox(info);
            _boxes[info.Id] = box;
            Children.Add(box.Root);
        }

        _guideLayer = new Canvas { IsHitTestVisible = false };
        Panel.SetZIndex(_guideLayer, 10);
        Children.Add(_guideLayer);

        Relayout();
        UpdateVisuals();
    }

    public void Select(string? id)
    {
        var next = FindBox(id)?.Info.Id;

        // 选中没变就不重复通知，免得列表与预览互相触发
        if (LayoutScheme.Same(_selectedId, next)) return;

        _selectedId = next;
        UpdateVisuals();
        SelectionChanged?.Invoke(_selectedId);
    }

    /// <summary>按当前大小在预览区居中（没有坐标时用默认示意大小）。</summary>
    public void Center(string? id)
    {
        if (FindBox(id) is not { Item: { } item } box) return;

        var current = CurrentRect(box);
        var rect = new Rect(
            Math.Max(0, (ActualWidth - current.Width) / 2),
            Math.Max(0, (ActualHeight - current.Height) / 2),
            current.Width,
            current.Height);

        WriteBox(item, ClampRect(rect));
    }

    /// <summary>恢复默认位置与大小：清掉坐标，回到流式布局。</summary>
    public void ResetToDefault(string? id)
    {
        if (FindBox(id)?.Item is not { } item) return;

        item.ClearBounds();
        Relayout();
        UpdateVisuals();
        LayoutChanged?.Invoke();
    }

    // ————— 摆放 —————

    private void Relayout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        foreach (var box in _boxes.Values) SetPixels(box, CurrentRect(box));
    }

    /// <summary>当前方块在预览里的像素位置：有坐标用坐标，没有就用元素清单里的默认示意位置。</summary>
    private Rect CurrentRect(Box box)
    {
        if (box.Item is { HasBounds: true } item && IsUsable())
            return ClampRect(ToPixels(item.XPercent!.Value, item.YPercent!.Value, item.WidthPercent!.Value, item.HeightPercent!.Value));

        var fallback = box.Info.DefaultRect ?? new HomeDefaultRect(0, 0, 100, 100);
        return ClampRect(ToPixels(fallback.XPercent, fallback.YPercent, fallback.WidthPercent, fallback.HeightPercent));

        bool IsUsable() => ActualWidth > 0 && ActualHeight > 0;
    }

    private Rect ToPixels(double x, double y, double width, double height)
        => new(x / 100 * ActualWidth, y / 100 * ActualHeight, width / 100 * ActualWidth, height / 100 * ActualHeight);

    /// <summary>把像素矩形裁剪到预览区内：尺寸不小于下限，且整体不越界（与 Core 的裁剪规则一致）。</summary>
    private Rect ClampRect(Rect rect)
    {
        var maxWidth = Math.Max(1, ActualWidth);
        var maxHeight = Math.Max(1, ActualHeight);
        var minSize = Math.Min(MinPixelSize, Math.Min(maxWidth, maxHeight));

        var width = Math.Clamp(rect.Width, minSize, maxWidth);
        var height = Math.Clamp(rect.Height, minSize, maxHeight);

        return new Rect(
            Math.Clamp(rect.X, 0, maxWidth - width),
            Math.Clamp(rect.Y, 0, maxHeight - height),
            width,
            height);
    }

    private void SetPixels(Box box, Rect rect)
    {
        box.Root.HorizontalAlignment = HorizontalAlignment.Left;
        box.Root.VerticalAlignment = VerticalAlignment.Top;
        box.Root.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        box.Root.Width = rect.Width;
        box.Root.Height = rect.Height;

        PlaceHandles(box, rect);
    }

    /// <summary>把像素矩形写回方案记录（百分比，保留一位小数）。</summary>
    private void WriteBox(LayoutItem item, Rect rect)
    {
        item.XPercent = Percent(rect.X, ActualWidth);
        item.YPercent = Percent(rect.Y, ActualHeight);
        item.WidthPercent = Percent(rect.Width, ActualWidth);
        item.HeightPercent = Percent(rect.Height, ActualHeight);

        if (FindBox(item.ElementId) is { } box)
        {
            SetPixels(box, rect);
            UpdateVisuals();
        }

        LayoutChanged?.Invoke();
    }

    private static double Percent(double pixels, double total)
        => total <= 0 ? 0 : Math.Round(pixels / total * 100, 1);

    // ————— 外观 —————

    private void UpdateVisuals()
    {
        foreach (var box in _boxes.Values)
        {
            var selected = LayoutScheme.Same(box.Info.Id, _selectedId);
            var free = box.Item is { HasBounds: true };
            var hidden = box.Item is { Visible: false };

            box.Frame.StrokeThickness = selected ? 2 : 1;
            box.Frame.StrokeDashArray = free && !hidden ? null : new DoubleCollection { 4, 3 };
            box.Frame.SetResourceReference(Shape.StrokeProperty,
                selected ? "Accent.Bright" : free && !hidden ? "Accent.Soft" : "Border.Strong");
            box.Frame.SetResourceReference(Shape.FillProperty, free && !hidden ? "Surface.Card" : "Surface.Sunken");
            box.Frame.Opacity = hidden ? 0.45 : 1;

            box.Title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent.Base" : "Text.Primary");
            box.Subtitle.Text = hidden ? Loc.T("已隐藏") : free ? Loc.T("自由定位") : Loc.T("流式");

            foreach (var handle in box.Handles.Values)
                handle.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ————— 拖动与缩放 —————

    private void OnFrameMouseDown(Box box, MouseButtonEventArgs e)
    {
        Select(box.Info.Id);
        e.Handled = true;

        // 双击 = 居中（与旧版一致）
        if (e.ClickCount == 2)
        {
            Center(box.Info.Id);
            return;
        }

        if (box.Item is null) return;

        _drag = new DragState
        {
            Box = box,
            Capture = box.Frame,
            StartRect = CurrentRect(box),
            StartPoint = e.GetPosition(this),
        };

        box.Frame.CaptureMouse();
    }

    private void OnFrameMouseMove(Box box, MouseEventArgs e)
    {
        if (_drag is not { Handle: null } drag || !ReferenceEquals(drag.Box, box)) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(this);
        var moved = drag.StartRect;
        moved.Offset(point.X - drag.StartPoint.X, point.Y - drag.StartPoint.Y);

        var rect = ClampRect(moved);
        WriteBox(drag.Box.Item!, rect);
        UpdateGuides(rect);
    }

    private void StartResize(Box box, string position, MouseButtonEventArgs e)
    {
        if (box.Item is null) return;

        Select(box.Info.Id);
        _drag = new DragState
        {
            Box = box,
            Handle = position,
            Capture = (IInputElement)e.OriginalSource,
            StartRect = CurrentRect(box),
            StartPoint = e.GetPosition(this),
        };

        _drag.Capture?.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMouseMove(Box box, MouseEventArgs e)
    {
        if (_drag is not { Handle: { } handle } drag || !ReferenceEquals(drag.Box, box)) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var point = e.GetPosition(this);
        var start = drag.StartRect;

        var deltaX = point.X - drag.StartPoint.X;
        var deltaY = point.Y - drag.StartPoint.Y;

        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;

        if (handle.Contains('l')) left = Math.Min(start.Left + deltaX, right - MinPixelSize);
        if (handle.Contains('r')) right = Math.Max(start.Right + deltaX, left + MinPixelSize);
        if (handle.Contains('t')) top = Math.Min(start.Top + deltaY, bottom - MinPixelSize);
        if (handle.Contains('b')) bottom = Math.Max(start.Bottom + deltaY, top + MinPixelSize);

        var rect = ClampRect(new Rect(left, top, right - left, bottom - top));
        WriteBox(drag.Box.Item!, rect);
        UpdateGuides(rect);
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } drag) return;

        drag.Capture?.ReleaseMouseCapture();
        _drag = null;
        ClearGuides();
        e.Handled = true;
    }

    // ————— 对齐辅助线 —————

    /// <summary>
    /// 拖动 / 缩放时画对齐辅助线：正在移动的三条边（左/中/右、上/中/下）与其它方块或预览边界
    /// 在阈值内对齐就画一条虚线。<b>只提示、不改位置</b>，不引入网格吸附。
    /// </summary>
    private void UpdateGuides(Rect moving)
    {
        ClearGuides();

        if (_guideLayer is null || _drag is not { } drag) return;

        var xs = new List<double> { 0, ActualWidth / 2, ActualWidth };
        var ys = new List<double> { 0, ActualHeight / 2, ActualHeight };

        foreach (var box in _boxes.Values)
        {
            if (ReferenceEquals(box, drag.Box) || box.Item is { Visible: false }) continue;

            var rect = CurrentRect(box);

            xs.Add(rect.Left);
            xs.Add(rect.Left + rect.Width / 2);
            xs.Add(rect.Right);

            ys.Add(rect.Top);
            ys.Add(rect.Top + rect.Height / 2);
            ys.Add(rect.Bottom);
        }

        foreach (var x in new[] { moving.Left, moving.Left + moving.Width / 2, moving.Right })
        {
            if (Nearest(xs, x) is { } target) AddGuideLine(target, 0, target, ActualHeight);
        }

        foreach (var y in new[] { moving.Top, moving.Top + moving.Height / 2, moving.Bottom })
        {
            if (Nearest(ys, y) is { } target) AddGuideLine(0, target, ActualWidth, target);
        }
    }

    /// <summary>在阈值内找最近的对齐位置；找不到返回 null。</summary>
    private static double? Nearest(IEnumerable<double> targets, double value)
    {
        double? best = null;

        foreach (var target in targets)
        {
            if (Math.Abs(target - value) > GuideThreshold) continue;
            if (best is { } current && Math.Abs(current - value) <= Math.Abs(target - value)) continue;

            best = target;
        }

        return best;
    }

    private void AddGuideLine(double x1, double y1, double x2, double y2)
    {
        if (_guideLayer is null) return;

        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        };
        line.SetResourceReference(Shape.StrokeProperty, "Accent.Bright");

        _guideLayer.Children.Add(line);
    }

    private void ClearGuides() => _guideLayer?.Children.Clear();

    // ————— 构造方块 —————

    private Box BuildBox(LayoutElementInfo info)
    {
        var frame = new Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            StrokeThickness = 1,
            Cursor = Cursors.SizeAll,
        };

        var icon = new SvgIcon
        {
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Icon = info.Icon,
        };
        icon.SetResourceReference(SvgIcon.IconBrushProperty, "Text.Secondary");

        var title = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Text = info.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var subtitle = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10.5,
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Children.Add(icon);
        label.Children.Add(title);
        label.Children.Add(subtitle);

        // 标签单独放在会裁剪的容器里：方块本身不裁剪，手柄才能探出边界
        var labelHost = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        labelHost.Children.Add(label);

        var handleLayer = new Canvas { ClipToBounds = false };

        // 先给个零尺寸，等 Relayout 算出真实矩形之前不会撑满整个预览区
        var root = new Grid { Width = 0, Height = 0 };
        root.Children.Add(frame);
        root.Children.Add(labelHost);
        root.Children.Add(handleLayer);

        var box = new Box
        {
            Info = info,
            Item = FindItem(info.Id),
            Root = root,
            Frame = frame,
            Title = title,
            Subtitle = subtitle,
        };

        foreach (var position in HandlePositions)
        {
            var handle = BuildHandle(box, position);
            box.Handles[position] = handle;
            handleLayer.Children.Add(handle);
        }

        frame.MouseLeftButtonDown += (_, e) => OnFrameMouseDown(box, e);
        frame.MouseMove += (_, e) => OnFrameMouseMove(box, e);
        frame.MouseLeftButtonUp += EndDrag;

        return box;
    }

    private Rectangle BuildHandle(Box box, string position)
    {
        var handle = new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            RadiusX = HandleSize / 2,
            RadiusY = HandleSize / 2,
            StrokeThickness = 1.5,
            Cursor = CursorFor(position),
            Visibility = Visibility.Collapsed,
        };
        handle.SetResourceReference(Shape.FillProperty, "Accent.Bright");
        handle.SetResourceReference(Shape.StrokeProperty, "Text.OnAccent");

        handle.MouseLeftButtonDown += (_, e) => StartResize(box, position, e);
        handle.MouseMove += (_, e) => OnHandleMouseMove(box, e);
        handle.MouseLeftButtonUp += EndDrag;

        return handle;
    }

    private static void PlaceHandles(Box box, Rect rect)
    {
        var half = HandleSize / 2;
        var centerX = rect.Width / 2 - half;
        var centerY = rect.Height / 2 - half;

        Place("tl", -half, -half);
        Place("t", centerX, -half);
        Place("tr", rect.Width - half, -half);
        Place("l", -half, centerY);
        Place("r", rect.Width - half, centerY);
        Place("bl", -half, rect.Height - half);
        Place("b", centerX, rect.Height - half);
        Place("br", rect.Width - half, rect.Height - half);

        void Place(string position, double left, double top)
        {
            if (!box.Handles.TryGetValue(position, out var handle)) return;

            Canvas.SetLeft(handle, left);
            Canvas.SetTop(handle, top);
        }
    }

    private static Cursor CursorFor(string position) => position switch
    {
        "tl" or "br" => Cursors.SizeNWSE,
        "tr" or "bl" => Cursors.SizeNESW,
        "t" or "b" => Cursors.SizeNS,
        "l" or "r" => Cursors.SizeWE,
        _ => Cursors.Arrow,
    };

    private LayoutItem? FindItem(string id) => _items.FirstOrDefault(item => LayoutScheme.Same(item.ElementId, id));

    private Box? FindBox(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : _boxes.GetValueOrDefault(id);

    /// <summary>方块在预览里的组装件。</summary>
    private sealed class Box
    {
        public required LayoutElementInfo Info { get; init; }

        /// <summary>方案里对应的记录。规范过之后一定不为 null，为 null 时该方块不可编辑。</summary>
        public LayoutItem? Item { get; set; }

        public required Grid Root { get; init; }

        public required Rectangle Frame { get; init; }

        public required TextBlock Title { get; init; }

        public required TextBlock Subtitle { get; init; }

        public Dictionary<string, Rectangle> Handles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DragState
    {
        public required Box Box { get; init; }

        /// <summary>缩放时按下的是哪个手柄；为 null 表示整体拖动。</summary>
        public string? Handle { get; init; }

        public IInputElement? Capture { get; init; }

        public required Rect StartRect { get; init; }

        public required Point StartPoint { get; init; }
    }
}
