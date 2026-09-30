using System.Windows;
using System.Windows.Controls;

namespace HMOL.App.Windows;

/// <summary>
/// 单行文本输入窗。校验交给调用方传入的委托，校验不过时留在窗口里显示原因，
/// 用于「重命名实例」「输入备份名称」这类需要给出明确失败原因的场景。
/// </summary>
public partial class TextInputWindow : Window
{
    private readonly Func<string, string?>? _validate;

    public TextInputWindow(string title, string message, string initialValue = "", string hint = "",
        Func<string, string?>? validate = null)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        _validate = validate;

        LabMessage.Text = message;
        LabHint.Text = hint;
        LabHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

        TxtValue.Text = initialValue;
        TxtValue.SelectAll();

        Loaded += (_, _) => TxtValue.Focus();
    }

    /// <summary>用户输入的内容，点「确定」后有效。</summary>
    public string Value => TxtValue.Text?.Trim() ?? string.Empty;

    /// <summary>显示输入窗，返回用户输入的内容（取消或关闭为 null）。</summary>
    public static string? Ask(Window? owner, string title, string message, string initialValue = "",
        string hint = "", Func<string, string?>? validate = null)
    {
        var window = new TextInputWindow(title, message, initialValue, hint, validate);

        if (owner is not null) window.Owner = owner;

        return window.ShowDialog() == true ? window.Value : null;
    }

    private void OnValueChanged(object sender, TextChangedEventArgs e)
    {
        if (LabError is null) return;

        LabError.Visibility = Visibility.Collapsed;
        BtnOk.IsEnabled = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _validate?.Invoke(Value);

        if (error is not null)
        {
            LabError.Text = error;
            LabError.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
