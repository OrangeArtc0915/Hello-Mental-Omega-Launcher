using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HMOL.App.Controls;
using HMOL.Core.Localization;

namespace HMOL.App.Windows;

/// <summary>选择窗里的一个条目。<paramref name="Key"/> 是返回给调用方的结果标识。</summary>
/// <param name="Label">主文案，一行显示。</param>
/// <param name="Subtitle">可选副文案，跟在主文案下面一行。</param>
public sealed record PickItem(string Key, string Label, string? Subtitle = null);

/// <summary>选择窗的两种模式。</summary>
public enum PickMode
{
    /// <summary>多选：每项一个复选框，另有「全选 / 全不选」。</summary>
    Multi,

    /// <summary>单选：每项一行，点一下选中。</summary>
    Single
}

/// <summary>
/// 通用多选 / 单选窗。卸载包要「按文件删除」（几百个文件里挑一部分）与「回到某个时间点」（单选），
/// ListBox 装不下「全选 / 全不选 + 已选计数」这套交互，因此自绘一个。
/// 列表统一渲染进 <c>PanItems</c>，条目多时各自只占一个轻量元素，交给 ScrollViewer 滚动。
/// </summary>
public partial class PickWindow : Window
{
    private readonly PickMode _mode;
    private readonly IReadOnlyList<PickItem> _items;

    /// <summary>多选模式的复选框，顺序与 <see cref="_items"/> 一致。</summary>
    private readonly List<CheckBox> _checks = [];

    /// <summary>单选模式的行，用来切换选中高亮。</summary>
    private readonly List<Border> _rows = [];

    private string? _selectedKey;

    private PickWindow(string title, string subtitle, IReadOnlyList<PickItem> items,
        PickMode mode, bool selectAllByDefault)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        _mode = mode;
        _items = items;

        LabSubtitle.Text = subtitle;
        LabSubtitle.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (mode == PickMode.Multi)
        {
            BuildMultiRows(selectAllByDefault);
        }
        else
        {
            // 单选没有全选按钮，也没有「已选 N / M 项」的语义
            PanTools.Visibility = Visibility.Collapsed;
            LabCount.Visibility = Visibility.Collapsed;
            BuildSingleRows();
        }
    }

    /// <summary>用户确认后选中的条目标识；取消或关窗时为 null。</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    /// <summary>
    /// 显示选择窗，返回选中的条目标识；取消 / 关窗返回 null。
    /// </summary>
    /// <param name="mode">多选或单选。</param>
    /// <param name="selectAllByDefault">多选模式下是否默认全选。</param>
    public static IReadOnlyList<string>? Pick(Window? owner, string title, string subtitle,
        IReadOnlyList<PickItem> items, PickMode mode = PickMode.Multi, bool selectAllByDefault = true)
    {
        var window = new PickWindow(title, subtitle, items, mode, selectAllByDefault);

        if (owner is not null) window.Owner = owner;

        return window.ShowDialog() == true ? window.Result : null;
    }

    // ————— 多选 —————

    private void BuildMultiRows(bool selectAllByDefault)
    {
        foreach (var item in _items)
        {
            var check = new CheckBox
            {
                Tag = item.Key,
                IsChecked = selectAllByDefault,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = BuildLabel(item)
            };

            check.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
            check.Checked += (_, _) => UpdateCount();
            check.Unchecked += (_, _) => UpdateCount();

            _checks.Add(check);

            var row = NewRow(check);
            PanItems.Children.Add(row);
        }

        UpdateCount();
    }

    private void UpdateCount()
    {
        var selected = _checks.Count(check => check.IsChecked == true);

        LabCount.Text = Loc.F("已选 {0} / {1} 项", selected, _items.Count);

        // 一个都没勾选就没什么可删 / 可回退的，直接禁用确定
        BtnOk.IsEnabled = selected > 0;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnClearAllClick(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var check in _checks) check.IsChecked = value;

        UpdateCount();
    }

    // ————— 单选 —————

    private void BuildSingleRows()
    {
        foreach (var item in _items)
        {
            var row = NewRow(BuildLabel(item));

            row.Tag = item.Key;
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => SelectSingle(row);

            // 未选中的常态：普通卡片底 + 默认描边，选中后由 SelectSingle 换成强调色
            row.SetResourceReference(Border.BackgroundProperty, "Surface.Card");
            row.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

            _rows.Add(row);
            PanItems.Children.Add(row);
        }

        // 默认不选中任何一行，用户点一下才给确定解锁
        BtnOk.IsEnabled = false;
    }

    private void SelectSingle(Border row)
    {
        _selectedKey = row.Tag as string;

        foreach (var other in _rows)
        {
            var isSelected = ReferenceEquals(other, row);

            other.SetResourceReference(Border.BackgroundProperty, isSelected ? "Accent.Faint" : "Surface.Card");
            other.SetResourceReference(Border.BorderBrushProperty, isSelected ? "Accent.Base" : "Border.Default");
        }

        BtnOk.IsEnabled = true;
    }

    // ————— 收尾 —————

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // 单选已在 SelectSingle 里记下结果；多选临到确认才收一遍勾选
        if (_mode == PickMode.Multi)
        {
            Result = _checks
                .Where(check => check.IsChecked == true)
                .Select(check => (string)check.Tag)
                .ToList();
        }
        else if (_selectedKey is not null)
        {
            Result = new[] { _selectedKey };
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    // ————— 渲染 —————

    /// <summary>一行（含内边距与圆角）的通用外壳；配色各模式自行设置。</summary>
    private static Border NewRow(UIElement content)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 1, 0, 1),
            BorderThickness = new Thickness(1),
            Child = content
        };

        row.SetResourceReference(Border.CornerRadiusProperty, "Radius.Small");
        row.SetResourceReference(Border.BackgroundProperty, "Common.Transparent");
        row.SetResourceReference(Border.BorderBrushProperty, "Common.Transparent");

        return row;
    }

    /// <summary>条目文案：主标题 + 可选副标题，颜色全部走资源，深浅主题都看得清。</summary>
    private static StackPanel BuildLabel(PickItem item)
    {
        var panel = new StackPanel();

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap
        };

        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        panel.Children.Add(label);

        if (string.IsNullOrWhiteSpace(item.Subtitle)) return panel;

        var subtitle = new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
        panel.Children.Add(subtitle);

        return panel;
    }
}
