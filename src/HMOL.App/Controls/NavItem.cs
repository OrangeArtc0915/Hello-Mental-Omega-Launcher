using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Controls.Svg;
using HMOL.App.Theme;

namespace HMOL.App.Controls;

/// <summary>
/// 左侧导航项。选中态由背景、图标文字颜色与左侧竖条共同表达；
/// 静止态一律走资源引用，主题切换后自动跟随。
/// </summary>
public class NavItem : RadioButton
{
    private Border? _back;
    private Border? _indicator;
    private SvgIcon? _icon;
    private TextBlock? _text;
    private ScaleTransform? _indicatorScale;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(NavItem),
        new PropertyMetadata(string.Empty, OnIconChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NavItem),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(NavItem), new PropertyMetadata(18d));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NavItem)d).SyncIcon();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (NavItem)d;
        item.SyncText();

        // 取值用的是自定义 Text 属性，Content 为空，无障碍名称需要显式设置
        System.Windows.Automation.AutomationProperties.SetName(item, item.Text);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _back = GetTemplateChild("Back") as Border;
        _indicator = GetTemplateChild("Indicator") as Border;
        _icon = GetTemplateChild("IconElement") as SvgIcon;
        _text = GetTemplateChild("TextElement") as TextBlock;

        if (_indicator is not null)
        {
            _indicator.RenderTransformOrigin = new Point(0.5, 0.5);
            _indicatorScale = new ScaleTransform(1, IsChecked == true ? 1 : 0);
            _indicator.RenderTransform = _indicatorScale;
        }

        SyncIcon();
        SyncText();
        ApplyState(animate: false);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        ApplyState(animate: true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        ApplyState(animate: true);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_back is null || !IsEnabled || IsChecked == true) return;

        AnimationEngine.Color(_back, Border.BackgroundProperty, ThemeColors.NavHover(this), 150, Ease.OutFluent);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_back is null || IsChecked == true) return;

        AnimationEngine.Color(_back, Border.BackgroundProperty, ThemeColors.Transparent(this), 150, Ease.OutFluent,
            () => _back?.SetResourceReference(Border.BackgroundProperty, "Common.Transparent"));
    }

    private void SyncIcon()
    {
        if (_icon is not null) _icon.Icon = Icon;
    }

    private void SyncText()
    {
        if (_text is not null) _text.Text = Text;
    }

    private void ApplyState(bool animate)
    {
        var selected = IsChecked == true;
        var backgroundKey = selected ? "Nav.ItemActive" : "Common.Transparent";
        var foregroundKey = selected ? "Nav.TextActive" : "Nav.Text";

        if (_back is not null)
        {
            var target = selected ? ThemeColors.NavActive(this) : ThemeColors.Transparent(this);

            if (animate && AnimationEngine.IsEnabled)
                AnimationEngine.Color(_back, Border.BackgroundProperty, target, 150, Ease.OutFluent,
                    () => _back?.SetResourceReference(Border.BackgroundProperty, backgroundKey));
            else
                _back.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        }

        _icon?.SetResourceReference(SvgIcon.IconBrushProperty, foregroundKey);
        _text?.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);

        if (_indicatorScale is null) return;

        var to = selected ? 1d : 0d;
        if (animate && AnimationEngine.IsEnabled)
            AnimationEngine.ScaleY(_indicatorScale, to, selected ? 260 : 150,
                selected ? Ease.OutBack : Ease.OutFluent);
        else
            _indicatorScale.ScaleY = to;
    }
}
