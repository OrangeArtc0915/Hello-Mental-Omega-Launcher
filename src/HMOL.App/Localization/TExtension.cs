using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using HMOL.Core.Localization;

namespace HMOL.App.Localization;

/// <summary>
/// XAML 里的文本标记扩展：<c>Text="{loc:T '原文'}"</c>（原文就是 msgid，见 <see cref="Loc"/>）。
///
/// <para>
/// 目标属性是依赖属性（界面文案的绝大多数情况）时返回绑定，换语言时随
/// <see cref="LocText"/> 的属性变更即时刷新；其它场合（样式 Setter、模板里的非依赖属性等）
/// 没有可靠的刷新通道，只能取一次当前值。
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    /// <summary>原文（msgid）。</summary>
    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target &&
            target.TargetObject is DependencyObject &&
            target.TargetProperty is DependencyProperty &&
            target.TargetObject is not SetterBase)
        {
            var binding = new Binding(nameof(LocText.Value))
            {
                Source = LocText.For(Text),
                Mode = BindingMode.OneWay
            };

            return binding.ProvideValue(serviceProvider);
        }

        return Loc.T(Text);
    }
}