using System.Windows;
using System.Windows.Controls;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.Core.Localization;

namespace HMOL.App.Windows;

/// <summary>选择窗里的一个选项。<paramref name="Key"/> 是返回给调用方的结果标识。</summary>
public sealed record ChoiceOption(string Text, string Key, ButtonTone Tone = ButtonTone.Outline);

/// <summary>对话框左侧的图标，用来替代 MessageBox 的系统图标。</summary>
public enum DialogIcon
{
    /// <summary>不显示图标。</summary>
    None,

    /// <summary>信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warn,

    /// <summary>错误。</summary>
    Error,

    /// <summary>询问。</summary>
    Question
}

/// <summary>
/// 通用多选项确认窗。WPF 的 MessageBox 只能给「是 / 否 / 取消」，装不下
/// 「覆盖全部 / 跳过已有 / 取消」这类三选项语义，因此自绘一个。
/// 系统 MessageBox 用的也是 Windows 原生外观，这里一并换成启动器风格。
/// </summary>
public partial class ChoiceWindow : Window
{
    public ChoiceWindow(string title, string message, string detail, IReadOnlyList<ChoiceOption> options)
        : this(title, message, detail, options, DialogIcon.None)
    {
    }

    public ChoiceWindow(string title, string message, string detail, IReadOnlyList<ChoiceOption> options, DialogIcon icon)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        LabMessage.Text = message;
        LabDetail.Text = detail;
        LabDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

        ApplyIcon(icon);

        foreach (var option in options)
        {
            var button = new OutlineButton
            {
                Content = option.Text,
                Tone = option.Tone,
                MinWidth = 96,
                Padding = new Thickness(14, 7, 14, 7)
            };

            // WrapPanel 换行时行与行之间靠下边距留出间隙；最左边的按钮不要左间距
            button.Margin = PanButtons.Children.Count > 0
                ? new Thickness(8, 0, 0, 6)
                : new Thickness(0, 0, 0, 6);

            button.Click += (_, _) =>
            {
                Result = option.Key;
                DialogResult = true;
            };

            PanButtons.Children.Add(button);
        }
    }

    /// <summary>用户选中的选项标识；直接关闭窗口时为 null。</summary>
    public string? Result { get; private set; }

    /// <summary>显示选择窗，返回选中的选项标识（关闭窗口为 null）。</summary>
    public static string? Ask(Window? owner, string title, string message, string detail, params ChoiceOption[] options)
        => Ask(owner, title, message, detail, DialogIcon.None, options);

    /// <summary>显示带图标的选择窗，返回选中的选项标识（关闭窗口为 null）。</summary>
    public static string? Ask(Window? owner, string title, string message, string detail, DialogIcon icon, params ChoiceOption[] options)
    {
        var window = new ChoiceWindow(title, message, detail, options, icon);

        if (owner is not null) window.Owner = owner;

        return window.ShowDialog() == true ? window.Result : null;
    }

    // ————— MessageBox 的启动器风格替代 —————

    /// <summary>信息提示：只有「知道了」一个按钮。</summary>
    public static void Info(Window? owner, string title, string message, string detail = "")
        => Alert(owner, title, message, detail, DialogIcon.Info);

    /// <summary>警告提示：只有「知道了」一个按钮。</summary>
    public static void Warn(Window? owner, string title, string message, string detail = "")
        => Alert(owner, title, message, detail, DialogIcon.Warn);

    /// <summary>错误提示：只有「知道了」一个按钮。</summary>
    public static void Error(Window? owner, string title, string message, string detail = "")
        => Alert(owner, title, message, detail, DialogIcon.Error);

    /// <summary>是 / 否确认。返回 true 表示选了「确定」。</summary>
    public static bool Confirm(Window? owner, string title, string message, string detail = "",
        string? confirmText = null, string? cancelText = null,
        DialogIcon icon = DialogIcon.Question, bool danger = false)
        => Ask(owner, title, message, detail, icon,
            new ChoiceOption(confirmText ?? Loc.T("确定"), "yes", danger ? ButtonTone.Danger : ButtonTone.Solid),
            new ChoiceOption(cancelText ?? Loc.T("取消"), "no")) == "yes";

    private static void Alert(Window? owner, string title, string message, string detail, DialogIcon icon)
        => Ask(owner, title, message, detail, icon, new ChoiceOption(Loc.T("知道了"), "ok", ButtonTone.Solid));

    private void ApplyIcon(DialogIcon icon)
    {
        var (glyph, brushKey) = icon switch
        {
            DialogIcon.Info => ("lucide/info", "Accent.Base"),
            DialogIcon.Warn => ("lucide/triangle-alert", "Status.Warn"),
            DialogIcon.Error => ("lucide/triangle-alert", "Status.Danger"),
            DialogIcon.Question => ("lucide/circle-help", "Accent.Base"),
            _ => (string.Empty, "Accent.Base")
        };

        if (string.IsNullOrEmpty(glyph))
        {
            IconDialog.Visibility = Visibility.Collapsed;
            return;
        }

        IconDialog.Icon = glyph;
        IconDialog.SetResourceReference(SvgIcon.IconBrushProperty, brushKey);
        IconDialog.Visibility = Visibility.Visible;
    }
}