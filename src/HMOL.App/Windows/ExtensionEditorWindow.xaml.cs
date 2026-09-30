using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Controls;
using HMOL.Core.Extensions;

namespace HMOL.App.Windows;

/// <summary>
/// 扩展清单的表单编辑器：字段与清单一一对应，保存前走与手写清单<b>完全相同</b>的校验
/// （<see cref="ExtensionValidator"/>），校验不过就把可读原因留在窗口里、不写盘 —— 不用让人手写 JSON 写坏。
///
/// 这个窗口唯一会写入的东西，就是扩展目录里的那一份清单文件。
/// </summary>
public partial class ExtensionEditorWindow : Window
{
    private readonly string _id;
    private readonly List<LineEntry> _lines = [];

    private bool _enabled = true;

    public ExtensionEditorWindow(string id, ExtensionManifest manifest)
    {
        InitializeComponent();

        _id = id;

        Title = $"扩展 · {id}";
        LabFilePath.Text = $"保存到：{Path.Combine(ExtensionStore.RootDirectory, id + ExtensionStore.FileExtension)}（只会写这一个文件）";

        TxtTitle.Text = manifest.Title ?? string.Empty;
        TxtVersion.Text = manifest.Version ?? string.Empty;
        TxtAuthor.Text = manifest.Author ?? string.Empty;
        TxtDescription.Text = manifest.Description ?? string.Empty;
        TxtIcon.Text = manifest.Icon ?? string.Empty;
        TxtValue.Text = manifest.Value ?? string.Empty;
        TxtLink.Text = manifest.Link ?? string.Empty;

        var source = manifest.DataSource;
        TxtDataUrl.Text = source?.Url ?? string.Empty;
        TxtDataPath.Text = source?.Path ?? string.Empty;
        TxtRefresh.Text = source is null ? string.Empty : source.RefreshSeconds.ToString(CultureInfo.InvariantCulture);

        _enabled = manifest.Enabled;

        foreach (var line in manifest.Lines ?? []) AddLineRow(line.Label ?? string.Empty, line.Text ?? string.Empty);

        // 至少留一个空行，用户一进来就能直接填
        if (_lines.Count == 0) AddLineRow(string.Empty, string.Empty);

        RefreshTitleHint();
        RefreshIconHint();
        RefreshEnabledButton();
        RefreshAddLineState();

        Loaded += (_, _) => TxtTitle.Focus();
    }

    /// <summary>清单里的一行文字对应的两个输入框。</summary>
    private sealed record LineEntry(TextBox Label, TextBox Text);

    // ————— 表单交互 —————

    private void OnTitleChanged(object sender, TextChangedEventArgs e) => RefreshTitleHint();

    private void OnIconChanged(object sender, TextChangedEventArgs e) => RefreshIconHint();

    private void OnAddLineClick(object sender, RoutedEventArgs e)
    {
        AddLineRow(string.Empty, string.Empty);
        SetError(null);
    }

    private void OnToggleEnabledClick(object sender, RoutedEventArgs e)
    {
        _enabled = !_enabled;
        RefreshEnabledButton();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var manifest = Collect(out var formError);

        if (formError is not null)
        {
            SetError(formError);
            return;
        }

        // 与手写清单完全相同的校验：不过就不写盘，只把原因摆出来
        var error = ExtensionValidator.Validate(manifest);

        if (error is null && !ExtensionStore.TrySave(_id, manifest, out var saveError)) error = saveError;

        if (error is not null)
        {
            SetError(error);
            return;
        }

        DialogResult = true;
    }

    /// <summary>把表单内容收集成一份清单。表单自身的填写问题（如刷新间隔不是整数）走 <paramref name="formError"/>。</summary>
    private ExtensionManifest Collect(out string? formError)
    {
        formError = null;

        var refreshText = TxtRefresh.Text.Trim();
        var refresh = ExtensionLimits.RefreshSecondsDefault;

        if (refreshText.Length > 0 && !int.TryParse(refreshText, NumberStyles.Integer, CultureInfo.InvariantCulture, out refresh))
        {
            formError = "「刷新间隔」要填整数秒，例如 600";
            refresh = ExtensionLimits.RefreshSecondsDefault;
        }

        var url = TxtDataUrl.Text.Trim();

        return new ExtensionManifest
        {
            Title = TxtTitle.Text,
            Version = TxtVersion.Text,
            Author = TxtAuthor.Text,
            Description = TxtDescription.Text,
            Icon = TxtIcon.Text,
            Value = TxtValue.Text,
            // 两边都空的行（表单默认留的那一行）不写进清单，免得文件里留一堆空行
            Lines = _lines
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Label.Text) ||
                                !string.IsNullOrWhiteSpace(entry.Text.Text))
                .Select(entry => new ExtensionLine { Label = entry.Label.Text, Text = entry.Text.Text })
                .ToList(),
            // 网址留空就表示这个扩展没有数据源
            DataSource = url.Length == 0
                ? null
                : new ExtensionDataSource { Url = url, Path = TxtDataPath.Text, RefreshSeconds = refresh },
            Link = TxtLink.Text,
            Enabled = _enabled
        };
    }

    // ————— 界面状态 —————

    private void AddLineRow(string label, string text)
    {
        if (_lines.Count >= ExtensionLimits.LineCountMax) return;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (labelFrame, labelBox) = MakeInput(ExtensionLimits.LineLabelMax);
        labelBox.Text = label;
        labelBox.ToolTip = "标签（可留空）";

        var (textFrame, textBox) = MakeInput(ExtensionLimits.LineTextMax);
        textBox.Text = text;
        textBox.ToolTip = "这一行显示的内容";
        textFrame.Margin = new Thickness(6, 0, 0, 0);

        var remove = new RoundIconButton
        {
            Width = 30,
            Height = 30,
            Icon = "lucide/trash-2",
            Tone = ButtonTone.Danger,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "删掉这一行"
        };

        var entry = new LineEntry(labelBox, textBox);

        remove.Click += (_, _) =>
        {
            _lines.Remove(entry);
            PanLines.Children.Remove(row);
            RefreshAddLineState();
            SetError(null);
        };

        Grid.SetColumn(labelFrame, 0);
        Grid.SetColumn(textFrame, 1);
        Grid.SetColumn(remove, 2);

        row.Children.Add(labelFrame);
        row.Children.Add(textFrame);
        row.Children.Add(remove);

        _lines.Add(entry);
        PanLines.Children.Add(row);

        RefreshAddLineState();
    }

    /// <summary>造一个与表单其它输入框同款的「边框 + 无框输入框」。</summary>
    private (Border Frame, TextBox Box) MakeInput(int maxLength)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = maxLength
        };

        box.SetResourceReference(ForegroundProperty, "Text.Primary");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "Text.Primary");

        var frame = new Border
        {
            Height = 32,
            Padding = new Thickness(9, 0, 9, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = TryFindResource("Radius.Button") is CornerRadius radius ? radius : new CornerRadius(8),
            Child = box
        };

        frame.SetResourceReference(BackgroundProperty, "Surface.Sunken");
        frame.SetResourceReference(BorderBrushProperty, "Border.Default");

        return (frame, box);
    }

    private void RefreshTitleHint()
    {
        if (LabTitleHint is null) return;

        var empty = TxtTitle.Text.Trim().Length == 0;

        LabTitleHint.Text = empty ? "名称不能为空。" : $"扩展 ID：{_id}";
        LabTitleHint.SetResourceReference(TextBlock.ForegroundProperty, empty ? "Status.Warn" : "Text.Tertiary");
    }

    /// <summary>图标：顺手把预览和「这个名字在不在图标包里」的结论显示出来。</summary>
    private void RefreshIconHint()
    {
        if (LabIconHint is null) return;

        var icon = ExtensionIcons.Normalize(TxtIcon.Text);

        if (icon.Length == 0)
        {
            IcoPreview.Icon = ExtensionIcons.Fallback;
            LabIconHint.Text = $"留空则使用默认图标 {ExtensionIcons.Fallback}";
            LabIconHint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            return;
        }

        var exists = ExtensionIcons.Exists(icon);

        IcoPreview.Icon = exists ? icon : ExtensionIcons.Fallback;

        LabIconHint.Text = exists
            ? $"将使用图标 {icon}"
            : $"图标「{icon}」不在内置图标包里，保存会被拒绝（可用图标见 docs/extensions.md）";

        LabIconHint.SetResourceReference(TextBlock.ForegroundProperty, exists ? "Text.Tertiary" : "Status.Warn");
    }

    private void RefreshEnabledButton()
    {
        BtnEnabled.Content = _enabled ? "已启用" : "已停用";
        BtnEnabled.Tone = _enabled ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void RefreshAddLineState()
    {
        BtnAddLine.IsEnabled = _lines.Count < ExtensionLimits.LineCountMax;
        BtnAddLine.Content = $"添加一行（{_lines.Count}/{ExtensionLimits.LineCountMax}）";
    }

    private void SetError(string? message)
    {
        LabError.Text = message ?? string.Empty;
        LabError.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
