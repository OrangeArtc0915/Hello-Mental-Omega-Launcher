using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace HMOL.App.Controls;

/// <summary>
/// 内容页页头：左侧「图标底色块 + 标题（18 粗）+ 副标题（12 次要色）」，右侧放本页的操作按钮。
/// 尺寸与字号都取自主页横幅那一套语言，内容页共用一份写法，不再各页各写一遍。
///
/// 右侧按钮直接写在本控件的 <c>Content</c> 里，仍然位于页面的命名范围内，
/// 页面代码照旧能按 <c>x:Name</c> 取到（见各页的 <c>BtnXxx</c>）。
/// 标题 / 副标题 / 图标都留空时对应的那一块会收起来，不需要的页面不必给。
/// </summary>
public class PageHeader : ContentControl
{
    private Border? _iconBlock;
    private TextBlock? _titleElement;
    private TextBlock? _subtitleElement;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(PageHeader),
        new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>页面标题，与主页的标题同一层级（18px 粗）。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>一句话说明本页在做什么（12px 次要色）；留空则不占位。</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>页头图标（形如 <c>lucide/layers</c>）；留空则不显示图标块。</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PageHeader)d).Sync();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _iconBlock = GetTemplateChild("IconBlock") as Border;
        _titleElement = GetTemplateChild("TitleElement") as TextBlock;
        _subtitleElement = GetTemplateChild("SubtitleElement") as TextBlock;

        Sync();
    }

    private void Sync()
    {
        var title = Title ?? string.Empty;
        var subtitle = Subtitle ?? string.Empty;

        if (_titleElement is not null) _titleElement.Text = title;

        if (_subtitleElement is not null)
        {
            _subtitleElement.Text = subtitle;
            _subtitleElement.Visibility = subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_iconBlock is not null)
            _iconBlock.Visibility = string.IsNullOrEmpty(Icon) ? Visibility.Collapsed : Visibility.Visible;

        // 标题与副标题在控件模板里，不会出现在 UIA 的控制视图里；
        // 页头本身带上名称与说明，读屏与自动化仍能读到本页标题。
        AutomationProperties.SetName(this, title);
        AutomationProperties.SetHelpText(this, subtitle);
    }

    /// <summary>
    /// 页头在无障碍树里以 Header 出现，名称与说明取标题 / 副标题。
    /// ContentControl 默认没有自动化对等体，不显式给一个的话页头整块不会出现在 UIA 里
    /// （模板里的文字本来也不会进控制视图），读屏就读不到本页标题了。
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new PageHeaderAutomationPeer(this);

    private sealed class PageHeaderAutomationPeer(PageHeader owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PageHeader);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Header;
    }
}
