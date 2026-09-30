using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Controls.Svg;
using HMOL.App.Theme;

namespace HMOL.App.Controls;

/// <summary>
/// 圆形图标按钮。Tone 决定配色，静止态一律用资源引用，主题切换后自动跟随。
/// </summary>
public class RoundIconButton : Button
{
    private Border? _hit;
    private SvgIcon? _icon;
    private ScaleTransform? _scale;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(RoundIconButton),
        new PropertyMetadata(string.Empty, OnIconChanged));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(ButtonTone), typeof(RoundIconButton),
        new PropertyMetadata(ButtonTone.Outline, OnToneChanged));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(RoundIconButton), new PropertyMetadata(15d));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public ButtonTone Tone
    {
        get => (ButtonTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RoundIconButton)d).SyncIcon();

    private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RoundIconButton)d).ApplyRestingState();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _hit = GetTemplateChild("Hit") as Border;
        _icon = GetTemplateChild("IconElement") as SvgIcon;

        if (_hit is not null)
        {
            _hit.RenderTransformOrigin = new Point(0.5, 0.5);
            _scale = new ScaleTransform(1, 1);
            _hit.RenderTransform = _scale;
        }

        SyncIcon();
        ApplyRestingState();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_hit is null || !IsEnabled) return;

        AnimationEngine.Color(_hit, Border.BackgroundProperty, HoverColor(), 150, Ease.OutFluent);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hit is null) return;

        var key = RestingKeys().Background;
        AnimationEngine.Color(_hit, Border.BackgroundProperty, RestingColor(), 200, Ease.OutFluent,
            () => _hit?.SetResourceReference(Border.BackgroundProperty, key));
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_scale is null || !IsEnabled) return;

        AnimationEngine.ScaleX(_scale, 0.85, 80, t => Ease.OutFluent(t, 5));
        AnimationEngine.ScaleY(_scale, 0.85, 80, t => Ease.OutFluent(t, 5));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_scale is null || !IsEnabled) return;

        AnimationEngine.ScaleX(_scale, 1, 300, Ease.OutBack);
        AnimationEngine.ScaleY(_scale, 1, 300, Ease.OutBack);
    }

    private void SyncIcon()
    {
        if (_icon is not null) _icon.Icon = Icon;
    }

    private void ApplyRestingState()
    {
        if (_hit is null) return;

        var (backgroundKey, iconKey) = RestingKeys();
        _hit.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        _icon?.SetResourceReference(SvgIcon.IconBrushProperty, iconKey);
    }

    private (string Background, string Icon) RestingKeys() => Tone switch
    {
        ButtonTone.Solid => ("Accent.Bright", "Text.OnAccent"),
        ButtonTone.Danger => ("Status.Danger", "Text.OnAccent"),
        ButtonTone.Plain => ("Common.Transparent", "Text.Secondary"),
        _ => ("Common.Transparent", "Text.Primary")
    };

    private Color RestingColor() => Tone switch
    {
        ButtonTone.Solid => ThemeColors.AccentBright(this),
        ButtonTone.Danger => ThemeColors.Danger(this),
        ButtonTone.Plain => ThemeColors.Transparent(this),
        _ => ThemeColors.Transparent(this)
    };

    private Color HoverColor() => Tone switch
    {
        ButtonTone.Solid => ThemeColors.AccentHover(this),
        ButtonTone.Danger => ThemeColors.DangerHover(this),
        _ => ThemeColors.Sunken(this)
    };
}
