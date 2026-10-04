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
        BuildThanks();
    }

    /// <summary>
    /// 把 <see cref="AppInfo.Thanks"/> 渲染成一排「名字 + 头衔」小标签；
    /// 头衔为空的人只显示名字，不占一个空标签。
    /// </summary>
    private void BuildThanks()
    {
        if (PanThanks is null) return;

        foreach (var credit in AppInfo.Thanks)
        {
            var hasTitle = !string.IsNullOrWhiteSpace(credit.Title);

            var chip = new Border
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 4, hasTitle ? 4 : 10, 4),
                CornerRadius = new CornerRadius(999)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");

            var line = new StackPanel { Orientation = Orientation.Horizontal };

            var name = new TextBlock
            {
                Text = credit.Name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            line.Children.Add(name);

            if (hasTitle)
            {
                var titleText = new TextBlock
                {
                    Text = credit.Title,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                titleText.SetResourceReference(TextBlock.ForegroundProperty, "Text.OnAccent");

                var title = new Border
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(7, 2, 7, 2),
                    CornerRadius = new CornerRadius(999),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = titleText
                };
                title.SetResourceReference(Border.BackgroundProperty, "Accent.Base");

                line.Children.Add(title);
            }

            chip.Child = line;
            PanThanks.Children.Add(chip);
        }
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
