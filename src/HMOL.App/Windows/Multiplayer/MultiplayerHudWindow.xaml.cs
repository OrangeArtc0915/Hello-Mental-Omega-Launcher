using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HMOL.App.Controls.Svg;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>HUD 里的一行：昵称 / 虚拟 IP / 延迟。</summary>
public sealed record HudRow(string Name, string Ip, string Latency);

/// <summary>
/// 游戏内 HUD：置顶、无边框、半透明的小浮窗，实时显示联机状态与对端。
/// 对应旧版 <c>hud.py</c> 的卡片部分（控制球与游戏内注入/截图不在本轮范围）。
/// 窗口位置跨会话保存（<c>Data\multiplayer.json</c>），重新打开时恢复并夹回屏幕可视范围。
/// </summary>
public partial class MultiplayerHudWindow : Window
{
    /// <summary>HUD 透明度的下限：再低就完全看不清内容了。</summary>
    public const double MinOpacity = 0.4;

    /// <summary>默认落点（屏幕左上偏下），与旧版 DEFAULT_POS 一致。</summary>
    private const double DefaultLeft = 16;
    private const double DefaultTop = 32;

    /// <summary>没有实际尺寸时用于范围判定的兜底宽高。</summary>
    private const double FallbackWidth = 186;
    private const double FallbackHeight = 80;

    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");

    private bool _placed;

    public MultiplayerHudWindow()
    {
        InitializeComponent();

        Left = DefaultLeft;
        Top = DefaultTop;

        // 恢复上次保存的位置；有保存值就不再被 ShowAtDefault 重置回默认落点
        var settings = MultiplayerSettingsStore.Current;

        if (settings.HudLeft is { } left && settings.HudTop is { } top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            Left = left;
            Top = top;
            _placed = true;
        }

        // 关窗（真正销毁的那次）也落一次盘，兜住没有走隐藏按钮就退出程序的情况
        Closed += (_, _) => SavePosition();

        // HUD 是固定深色底，关闭按钮的图标也要换成浅色：否则浅色主题下会是一枚深色图标，看不清
        Loaded += (_, _) => ApplyCloseIconBrush();

        ApplyOpacity();
    }

    /// <summary>把关闭按钮的图标换成 HUD 专用的浅色（<see cref="RoundIconButton"/> 按 Tone 取的是主题色）。</summary>
    private void ApplyCloseIconBrush()
    {
        if (BtnHudClose.Template?.FindName("IconElement", BtnHudClose) is SvgIcon icon)
            icon.SetResourceReference(SvgIcon.IconBrushProperty, "Text.OnHudDim");
    }

    /// <summary>
    /// 按联机配置里的「HUD 透明度」刷新整块 HUD。设置页改了值之后由
    /// <see cref="Services.MultiplayerHub.ApplyHudOpacity"/> 调过来，不用重建窗口。
    /// </summary>
    public void ApplyOpacity()
    {
        var raw = MultiplayerSettingsStore.Current.HudOpacity;
        Frame.Opacity = double.IsFinite(raw) ? Math.Clamp(raw, MinOpacity, 1) : 1;
    }

    /// <summary>用户点了 HUD 上的关闭按钮。</summary>
    public event Action? HideRequested;

    /// <summary>更新内容。<paramref name="title"/> 是状态文本，<paramref name="rows"/> 是对端列表。</summary>
    public void SetContent(string title, IReadOnlyList<HudRow> rows)
    {
        LabTitle.Text = string.IsNullOrWhiteSpace(title) ? Loc.T("联机") : title;
        LabCount.Text = rows.Count > 0 ? Loc.F("在线 {0}", rows.Count) : string.Empty;

        PanRows.Children.Clear();

        foreach (var row in rows)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

            line.Children.Add(NewText("●", 11, "Status.Success"));

            line.Children.Add(NewText(
                string.IsNullOrWhiteSpace(row.Name) ? row.Ip : row.Name,
                12.5,
                "Text.OnHud",
                FontWeights.SemiBold,
                maxWidth: 150));

            line.Children.Add(NewText(row.Ip, 11.5, "Text.OnHudDim", monospace: true, margin: new Thickness(9, 0, 0, 0)));

            if (!string.IsNullOrWhiteSpace(row.Latency))
                line.Children.Add(NewText($"{row.Latency} ms", 11.5, "Text.OnHudDim", monospace: true, margin: new Thickness(9, 0, 0, 0)));

            PanRows.Children.Add(line);
        }

        LabEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 行数变化后重新夹回屏幕内，避免新增行跑到屏幕外
        Dispatcher.InvokeAsync(ClampToScreen, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>显示在默认位置（首次显示时）。已保存过位置则保留保存值。</summary>
    public void ShowAtDefault()
    {
        if (!_placed)
        {
            _placed = true;
            Left = DefaultLeft;
            Top = DefaultTop;
        }

        Show();
        ClampToScreen();
    }

    /// <summary>把当前坐标写回联机配置（<c>Data\multiplayer.json</c>），下次打开时恢复。</summary>
    private void SavePosition()
    {
        if (!double.IsFinite(Left) || !double.IsFinite(Top)) return;

        try
        {
            var left = Left;
            var top = Top;

            MultiplayerSettingsStore.Update(settings =>
            {
                settings.HudLeft = left;
                settings.HudTop = top;
            });
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("保存 HUD 位置失败：{0}", ex.Message));
        }
    }

    private static TextBlock NewText(
        string text,
        double fontSize,
        string brushKey,
        FontWeight? weight = null,
        Thickness margin = default,
        double maxWidth = double.PositiveInfinity,
        bool monospace = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = margin
        };

        if (weight is not null) block.FontWeight = weight.Value;
        if (monospace) block.FontFamily = MonoFont;
        if (!double.IsPositiveInfinity(maxWidth)) block.MaxWidth = maxWidth;

        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>
    /// 夹回屏幕。用虚拟桌面范围（含所有显示器）而不是主屏工作区：
    /// 多显示器拔掉 / 分辨率变化后，保存的坐标落在屏幕外时也能被拉回可见区域。
    /// </summary>
    private void ClampToScreen()
    {
        try
        {
            var left = SystemParameters.VirtualScreenLeft;
            var top = SystemParameters.VirtualScreenTop;
            var right = left + SystemParameters.VirtualScreenWidth;
            var bottom = top + SystemParameters.VirtualScreenHeight;

            var width = ActualWidth > 0 ? ActualWidth : FallbackWidth;
            var height = ActualHeight > 0 ? ActualHeight : FallbackHeight;

            if (double.IsNaN(Left) || Left < left) Left = left + DefaultLeft;
            if (double.IsNaN(Top) || Top < top) Top = top + DefaultTop;

            if (Left + width > right) Left = Math.Max(left, right - width);
            if (Top + height > bottom) Top = Math.Max(top, bottom - height);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("调整 HUD 位置失败：{0}", ex.Message));
        }
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && HasButton(source)) return;

        try { DragMove(); }
        catch { /* 鼠标已释放时 DragMove 会抛异常，忽略 */ }

        _placed = true;
        SavePosition();
    }

    private static bool HasButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnHideClick(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Hide();
        HideRequested?.Invoke();
    }
}
