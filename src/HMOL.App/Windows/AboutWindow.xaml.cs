using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.App.Windows;

/// <summary>
/// 「关于」独立窗口。软件名、版本、仓库与 QQ 群信息全部取自 <see cref="AppInfo"/>，
/// 界面上不硬编码任何版本号或网址。不参与主窗口的页面切换，由左侧导航打开。
/// </summary>
public partial class AboutWindow : Window
{
    private const string CopyHint = "复制后可直接在 QQ 里搜索群号。";

    private readonly DispatcherTimer _hintTimer;

    public AboutWindow()
    {
        InitializeComponent();

        // 复制结果只在按钮下面提示几秒，随后恢复成默认说明
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            ResetHint();
        };

        ResetHint();
    }

    private void OnOpenGitHubClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.GitHubUrl);

    private void OnOpenWebsiteClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.WebsiteUrl);

    private void OnOpenGiteeClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.GiteeUrl);

    private void OnOpenIssuesClick(object sender, MouseButtonEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.GitHubIssuesUrl);

    private void OnCopyGroupClick(object sender, RoutedEventArgs e)
    {
        var number = AppInfo.QqGroup;

        try
        {
            Clipboard.SetText(number);
            ShowHint($"已复制群号 {number}", isError: false);
        }
        catch (Exception ex)
        {
            // 剪贴板被其它程序占用时会抛异常，此时提示用户手动记录
            ShowHint($"复制失败，请手动记录群号 {number}", isError: true);
            Log.Warn($"复制 QQ 群号失败：{ex.Message}");
        }
    }

    private void OnJoinGroupClick(object sender, RoutedEventArgs e)
        => ShellHelper.OpenUrl(AppInfo.QqGroupUrl);

    /// <summary>提示一次，几秒后自动恢复成默认文案。</summary>
    private void ShowHint(string text, bool isError)
    {
        if (LabHint is null) return;

        LabHint.Text = text;
        LabHint.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "Status.Danger" : "Text.Secondary");

        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ResetHint()
    {
        if (LabHint is null) return;

        LabHint.Text = CopyHint;
        LabHint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
