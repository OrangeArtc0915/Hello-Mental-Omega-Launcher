using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HMOL.App.Animation;
using HMOL.App.Theme;

namespace HMOL.App.Controls;

/// <summary>
/// 卡片容器。可选标题头与柔和投影；悬停时投影加深、标题转为强调色。
/// 投影直接用 WPF 自带的 DropShadowEffect，列表类内容请把 UseShadow 设为 False 以免影响滚动性能。
/// </summary>
public class SurfaceCard : ContentControl
{
    private const double IdleShadowOpacity = 0.10;
    private const double HoverShadowOpacity = 0.24;

    private TextBlock? _titleElement;
    private Border? _cardBorder;
    private Effect? _templateShadow;
    private DropShadowEffect? _shadow;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SurfaceCard),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty UseShadowProperty = DependencyProperty.Register(
        nameof(UseShadow), typeof(bool), typeof(SurfaceCard),
        new PropertyMetadata(true, OnUseShadowChanged));

    public static readonly DependencyProperty HasHoverEffectProperty = DependencyProperty.Register(
        nameof(HasHoverEffect), typeof(bool), typeof(SurfaceCard), new PropertyMetadata(true));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>是否绘制投影。大量重复出现的条目建议关闭。</summary>
    public bool UseShadow
    {
        get => (bool)GetValue(UseShadowProperty);
        set => SetValue(UseShadowProperty, value);
    }

    /// <summary>悬停时是否加深投影并让标题变色。</summary>
    public bool HasHoverEffect
    {
        get => (bool)GetValue(HasHoverEffectProperty);
        set => SetValue(HasHoverEffectProperty, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SurfaceCard)d).SyncTitle();

    private static void OnUseShadowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SurfaceCard)d).SyncShadow();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _titleElement = GetTemplateChild("TitleElement") as TextBlock;

        // 投影是 Border 的 Effect，不是可视元素，不能靠 GetTemplateChild 取
        _cardBorder = GetTemplateChild("CardBorder") as Border;
        _templateShadow = _cardBorder?.Effect;

        SyncShadow();
        SyncTitle();
    }

    /// <summary>
    /// 按 UseShadow 决定是否挂投影。
    /// 模板里默认带一个投影，这里在关闭时把它摘掉——列表里成百上千个条目各带一个
    /// DropShadowEffect 会明显拖慢滚动，所以重复条目必须能真正关掉。
    /// </summary>
    private void SyncShadow()
    {
        if (_cardBorder is null) return;

        if (!UseShadow)
        {
            _shadow = null;
            _cardBorder.Effect = null;
            return;
        }

        if (_cardBorder.Effect is null) _cardBorder.Effect = _templateShadow;
        _shadow = _cardBorder.Effect as DropShadowEffect;
        if (_shadow is not null) _shadow.Opacity = IdleShadowOpacity;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!HasHoverEffect) return;

        AnimateShadow(HoverShadowOpacity);
        _titleElement?.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Base");
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!HasHoverEffect) return;

        AnimateShadow(IdleShadowOpacity);
        _titleElement?.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
    }

    private void AnimateShadow(double target)
    {
        if (_shadow is null) return;
        AnimationEngine.Start($"cardShadow:{GetHashCode()}", _shadow.Opacity, target, 120, Ease.OutFluent,
            v => _shadow.Opacity = v);
    }

    private void SyncTitle()
    {
        if (_titleElement is null) return;

        _titleElement.Text = Title;
        _titleElement.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
    }
}
