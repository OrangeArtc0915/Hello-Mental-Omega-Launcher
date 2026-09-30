namespace HMOL.Core.Extensions;

/// <summary>
/// 扩展能用的图标白名单。
///
/// 图标资源是界面层（HMOL.App）里的内嵌 SVG，逻辑层看不到它，因此由界面层在启动时
/// 把「实际存在的图标名」登记进来（见 <see cref="RegisterAvailable"/>），核心这里只负责查表。
/// 界面层没登记时一律放行 —— 宁可少校验一次，也不能因为枚举失败就把好扩展判成坏的；
/// 这种情况下还有第二道兜底：界面渲染时找不到图标会自动退回 <see cref="Fallback"/>，不会崩。
/// </summary>
public static class ExtensionIcons
{
    /// <summary>图标包名。</summary>
    public const string Pack = "lucide";

    /// <summary>图标名写错 / 留空时用的默认图标。</summary>
    public const string Fallback = "lucide/puzzle";

    private static volatile IReadOnlyList<string> _available = [];

    /// <summary>当前登记在册的图标名（形如 <c>lucide/play</c>）；界面层还没登记时为空。</summary>
    public static IReadOnlyList<string> Available => _available;

    /// <summary>
    /// 由界面层登记「实际存在哪些图标」。名字统一成 <c>lucide/名字</c> 小写形式。
    /// 传空或 null 表示退回「不校验图标」。
    /// </summary>
    public static void RegisterAvailable(IEnumerable<string>? names)
    {
        var list = new List<string>();

        foreach (var name in names ?? [])
        {
            var normalized = Normalize(name);

            if (normalized.Length > 0 && !list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                list.Add(normalized);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        _available = list;
    }

    /// <summary>把清单里的图标写法归一化成 <c>lucide/名字</c>；空输入返回空串。</summary>
    public static string Normalize(string? icon)
    {
        var text = (icon ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

        if (text.Length == 0) return string.Empty;

        return text.Contains('/') ? text : $"{Pack}/{text}";
    }

    /// <summary>这个图标名是否可用。界面层还没登记白名单时一律返回 true（无从校验）。</summary>
    public static bool Exists(string? icon)
    {
        var available = _available;
        if (available.Count == 0) return true;

        var normalized = Normalize(icon);
        if (normalized.Length == 0) return false;

        return available.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
