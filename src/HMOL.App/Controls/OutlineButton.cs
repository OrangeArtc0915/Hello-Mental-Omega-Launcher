using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Theme;

namespace HMOL.App.Controls;

public enum ButtonTone
{
    /// <summary>描边按钮，用于次要操作。</summary>
    Outline = 0,

    /// <summary>实心强调色按钮，用于主要操作。</summary>
    Solid = 1,

    /// <summary>危险操作。</summary>
    Danger = 2,

    /// <summary>无边框文字按钮。</summary>
    Plain = 3
}

/// <summary>
/// 按钮。Tone 决定配色：Outline 为描边、Solid 为强调色实心、Danger 为警示、Plain 为纯文字。
/// 静止态的配色一律用资源引用，主题切换后自动跟随。
/// </summary>
public class OutlineButton : Button
{
    private Border? _face;
    private ScaleTransform? _scale;

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(ButtonTone), typeof(OutlineButton),
        new PropertyMetadata(ButtonTone.Outline, OnToneChanged));

    public ButtonTone Tone
    {
        get => (ButtonTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((OutlineButton)d).ApplyRestingState();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _face = GetTemplateChild("Face") as Border;
        if (_face is not null)
        {
            _face.RenderTransformOrigin = new Point(0.5, 0.5);
            _scale = new ScaleTransform(1, 1);
            _face.RenderTransform = _scale;
        }

        ApplyRestingState();
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (_face is null || !IsEnabled || Tone == ButtonTone.Plain) return;

        var (background, border, foreground) = HoverColors();
        AnimationEngine.Color(_face, Border.BackgroundProperty, background, 150, Ease.OutFluent);
        AnimationEngine.Color(_face, Border.BorderBrushProperty, border, 150, Ease.OutFluent);
        AnimationEngine.Color(this, ForegroundProperty, foreground, 150, Ease.OutFluent);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        RestoreRestingState();
    }

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_scale is null || !IsEnabled) return;

        AnimationEngine.ScaleX(_scale, 0.975, 80, t => Ease.OutFluent(t, 5));
        AnimationEngine.ScaleY(_scale, 0.975, 80, t => Ease.OutFluent(t, 5));
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_scale is null || !IsEnabled) return;

        AnimationEngine.ScaleX(_scale, 1, 300, Ease.OutBack);
        AnimationEngine.ScaleY(_scale, 1, 300, Ease.OutBack);
    }

    private void ApplyRestingState()
    {
        if (_face is null) return;

        var (backgroundKey, borderKey, foregroundKey) = RestingKeys();
        _face.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        _face.SetResourceReference(Border.BorderBrushProperty, borderKey);
        _face.BorderThickness = Tone == ButtonTone.Plain ? new Thickness(0) : new Thickness(1);
        SetResourceReference(ForegroundProperty, foregroundKey);
    }

    private void RestoreRestingState()
    {
        if (_face is null || Tone == ButtonTone.Plain) return;

        var (backgroundKey, borderKey, foregroundKey) = RestingKeys();
        var (background, border, foreground) = RestingColors();

        AnimationEngine.Color(_face, Border.BackgroundProperty, background, 200, Ease.OutFluent,
            () => _face?.SetResourceReference(Border.BackgroundProperty, backgroundKey));
        AnimationEngine.Color(_face, Border.BorderBrushProperty, border, 200, Ease.OutFluent,
            () => _face?.SetResourceReference(Border.BorderBrushProperty, borderKey));
        AnimationEngine.Color(this, ForegroundProperty, foreground, 200, Ease.OutFluent,
            () => SetResourceReference(ForegroundProperty, foregroundKey));
    }

    private (string Background, string Border, string Foreground) RestingKeys() => Tone switch
    {
        ButtonTone.Solid => ("Accent.Bright", "Accent.Bright", "Text.OnAccent"),
        ButtonTone.Danger => ("Status.Danger", "Status.Danger", "Text.OnAccent"),
        ButtonTone.Plain => ("Common.Transparent", "Common.Transparent", "Accent.Base"),
        _ => ("Surface.Card", "Border.Strong", "Text.Primary")
    };

    private (Color Background, Color Border, Color Foreground) RestingColors() => Tone switch
    {
        ButtonTone.Solid => (ThemeColors.AccentBright(this), ThemeColors.AccentBright(this), ThemeColors.OnAccent(this)),
        ButtonTone.Danger => (ThemeColors.Danger(this), ThemeColors.Danger(this), ThemeColors.OnAccent(this)),
        ButtonTone.Plain => (ThemeColors.Transparent(this), ThemeColors.Transparent(this), ThemeColors.Accent(this)),
        _ => (ThemeColors.Surface(this), ThemeColors.BorderStrong(this), ThemeColors.Text(this))
    };

    private (Color Background, Color Border, Color Foreground) HoverColors() => Tone switch
    {
        ButtonTone.Solid => (ThemeColors.AccentHover(this), ThemeColors.AccentHover(this), ThemeColors.OnAccent(this)),
        ButtonTone.Danger => (ThemeColors.DangerHover(this), ThemeColors.DangerHover(this), ThemeColors.OnAccent(this)),
        ButtonTone.Plain => (ThemeColors.Transparent(this), ThemeColors.Transparent(this), ThemeColors.AccentBright(this)),
        _ => (ThemeColors.SurfaceHover(this), ThemeColors.Accent(this), ThemeColors.Accent(this))
    };
}
