namespace HMOL.Core.Extensions;

/// <summary>
/// 一张扩展卡片要显示的内容：主数值 + 若干文字行 + 可选的弱化提示。
/// （「点击打开网址」这类动作直接来自清单，不属于这里的数据。）
///
/// 这些内容全部来自清单声明，或来自清单声明的数据源返回的<b>文本</b>；
/// 宿主不执行任何东西，只负责把字符串画到卡片上。
/// </summary>
public sealed record ExtensionCard
{
    /// <summary>主数值（卡片上的大字）。数据源可用时是取回来的值，否则是清单里的静态值。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>文字行（已按「标签：内容」拼好）。</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>弱化提示（如数据源取不到时的降级说明）；空串表示不显示。</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>扩展本身不可用时的原因（正常情况下不会出现，因为坏清单不会走到渲染）；null 表示可用。</summary>
    public string? Error { get; init; }

    public bool HasValue => Value.Length > 0;

    public bool HasLines => Lines.Count > 0;

    public bool HasNote => Note.Length > 0;

    public bool HasError => Error is { Length: > 0 };
}
