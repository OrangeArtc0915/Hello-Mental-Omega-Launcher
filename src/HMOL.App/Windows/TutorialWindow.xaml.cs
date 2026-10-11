using System.Windows;

namespace HMOL.App.Windows;

/// <summary>
/// 「新手教程」独立窗口。正文全部为内置说明文字，不参与主窗口的页面切换，
/// 由左下角的「新手教程」入口打开。左侧分类侧栏选中哪一节，右侧就只显示哪一节。
/// </summary>
public partial class TutorialWindow : Window
{
    public TutorialWindow()
    {
        InitializeComponent();

        // 默认选中第一项并显示第一节（设置 IsChecked 会触发 OnSectionChecked）。
        NavHowToUse.IsChecked = true;
    }

    /// <summary>点左侧某个分类时，只显示对应小节，并把内容滚回顶部。</summary>
    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;
        if (FindName(name) is not FrameworkElement target) return;

        foreach (UIElement child in PanSections.Children)
            child.Visibility = child == target ? Visibility.Visible : Visibility.Collapsed;

        ScrollSections.ScrollToTop();
    }
}
