using System.Windows;
using System.Windows.Media;

namespace HMOL.App.Animation;

/// <summary>
/// 页面入场动画的统一实现：分组错峰淡入 + 轻微上移，与主页同构。
/// 动画键一律带本页前缀（<c>inst:</c> / <c>pkg:</c> / <c>mp:</c> / <c>log:</c> / <c>set:</c>），
/// 离开页面时按前缀一次性收干净，不留永久运行的动画。
///
/// 元素自己的平移变换在这里统一登记：入场与悬停位移复用同一份，
/// 避免两个动画抢同一个属性（主页就是这么做的，这里把它抽出来给其余页面复用）。
/// </summary>
public sealed class PageAnimator
{
    /// <summary>延迟上限：内容多的页面分组多，超过它的一律并到同一档，保证整体入场控制在 500ms 内。</summary>
    private const double MaxDelayMs = 200;

    private readonly string _prefix;
    private readonly double _durationMs;
    private readonly double _stepMs;
    private readonly double _offsetY;

    /// <summary>入场计划：元素 + 它自己的延迟。</summary>
    private readonly List<(FrameworkElement Element, double Delay)> _plan = [];

    /// <summary>元素 → 它自己的平移变换。</summary>
    private readonly Dictionary<FrameworkElement, TranslateTransform> _offsets = [];

    /// <param name="prefix">本页的动画键前缀，形如 <c>inst:</c>。</param>
    /// <param name="durationMs">单块淡入时长。</param>
    /// <param name="stepMs">同组内的错峰间隔。</param>
    /// <param name="offsetY">入场时从下方多少像素上移到位。</param>
    public PageAnimator(string prefix, double durationMs = 220, double stepMs = 40, double offsetY = 12)
    {
        _prefix = prefix;
        _durationMs = durationMs;
        _stepMs = stepMs;
        _offsetY = offsetY;
    }

    /// <summary>本页动画键的前缀。</summary>
    public string Prefix => _prefix;

    /// <summary>
    /// 追加一组元素：组内按 <c>stepMs</c> 依次错峰，<paramref name="baseDelayMs"/> 是这一组的起始延迟。
    /// 传 null 的元素直接跳过；延迟超过 <see cref="MaxDelayMs"/> 的一律并到同一档。
    /// </summary>
    public void Group(double baseDelayMs, params FrameworkElement?[] elements)
    {
        var index = 0;

        foreach (var element in elements)
        {
            if (element is null) continue;

            _plan.Add((element, Math.Min(baseDelayMs + index * _stepMs, MaxDelayMs)));
            index++;
        }
    }

    /// <summary>入场：各块依次淡入并轻微上移。动画被挂起时直接把状态落到「已入场」。</summary>
    public void Play()
    {
        foreach (var (element, delay) in _plan)
        {
            var offset = OffsetOf(element);

            if (!AnimationEngine.IsEnabled)
            {
                element.Opacity = 1;
                offset.Y = 0;
                offset.X = 0;
                continue;
            }

            offset.Y = _offsetY;
            element.Opacity = 0;

            // 同一 key 会替换旧动画，重复进入不会叠加
            AnimationEngine.Start($"{_prefix}enter:{element.GetHashCode()}", 0, 1, _durationMs, Ease.OutFluent, v =>
            {
                element.Opacity = v;
                offset.Y = _offsetY * (1 - v);
            }, delayMs: delay);
        }
    }

    /// <summary>
    /// 离开页面：停掉带本页前缀的动画（含入场与悬停位移），并把状态落回终态，
    /// 免得下次进入时元素停在半透明 / 偏移的位置。
    /// </summary>
    public void Stop()
    {
        AnimationEngine.StopWhere(key => key.StartsWith(_prefix, StringComparison.Ordinal));

        foreach (var (element, _) in _plan) element.Opacity = 1;

        foreach (var offset in _offsets.Values)
        {
            offset.X = 0;
            offset.Y = 0;
        }
    }

    /// <summary>悬停位移：列表行轻微右移（离开页面时一并收尾）。</summary>
    public void HoverShift(FrameworkElement? element, double to, double durationMs = 120)
    {
        if (element is null) return;

        var offset = OffsetOf(element);
        AnimationEngine.Start($"{_prefix}shift:{element.GetHashCode()}", offset.X, to, durationMs, Ease.OutFluent,
            v => offset.X = v);
    }

    /// <summary>悬停上浮：卡片轻微抬起。入场动画还没跑完时不抢同一个 Y。</summary>
    public void HoverLift(FrameworkElement? element, bool hover)
    {
        if (element is null) return;
        if (AnimationEngine.RunningKeys.Contains($"{_prefix}enter:{element.GetHashCode()}")) return;

        var offset = OffsetOf(element);
        AnimationEngine.Start($"{_prefix}lift:{element.GetHashCode()}", offset.Y, hover ? -2 : 0, 130,
            Ease.OutFluent, v => offset.Y = v);
    }

    /// <summary>取元素自己的平移变换；没有就挂一个（入场与悬停复用它）。</summary>
    private TranslateTransform OffsetOf(FrameworkElement element)
    {
        if (_offsets.TryGetValue(element, out var found)) return found;

        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        _offsets[element] = transform;

        return transform;
    }
}
