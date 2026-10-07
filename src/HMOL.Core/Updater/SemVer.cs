using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Updater;

/// <summary>
/// 轻量版本号比较。只处理常见的「点分段」版本号，不追求完整语义化版本规范：
/// 忽略 v 前缀，逐段比较，段数不同时短的一方补 0，非数字段按字符串比较。
/// </summary>
public static class SemVer
{
    /// <summary>比较两个版本号。left 更旧返回负数，相等返回 0，left 更新返回正数；任一无法解析时返回 0 并记 Warn 日志。</summary>
    public static int Compare(string? left, string? right)
    {
        if (!TryParse(left, out var a) || !TryParse(right, out var b))
        {
            Log.Warn(Loc.F("版本号无法解析，按相等处理：left=\"{0}\"，right=\"{1}\"", left, right));
            return 0;
        }

        var count = Math.Max(a.Length, b.Length);
        for (var i = 0; i < count; i++)
        {
            // 段数不同时短的一方补 0：4.5 与 4.5.0 视为相等
            var segmentA = i < a.Length ? a[i] : "0";
            var segmentB = i < b.Length ? b[i] : "0";

            int result;
            if (int.TryParse(segmentA, out var numberA) && int.TryParse(segmentB, out var numberB))
                result = numberA.CompareTo(numberB);
            else
                // 含预发布后缀等非纯数字段时按字符串比较
                result = string.Compare(segmentA, segmentB, StringComparison.OrdinalIgnoreCase);

            if (result != 0) return result;
        }

        return 0;
    }

    /// <summary>candidate 是否比 baseline 更新。</summary>
    public static bool IsNewer(string? candidate, string? baseline)
        => Compare(candidate, baseline) > 0;

    /// <summary>
    /// 从 baseline 升到 candidate 是否属于「强制更新」：只看主版本与次版本，
    /// 任一变化就要求必须更新（1.3.0→1.4.0、1.3.0→2.3.0 都算强制），
    /// 只有修订号变化（1.3.0→1.3.1）才交给用户自己选。
    ///
    /// <para>
    /// 段数不同按补 0 处理（4.5 与 4.5.0 视为一致）；版本号无法解析时返回 false —— 宁可让用户自己判断，
    /// 也不要因为一个看不懂的号就把人挡在门外。
    /// </para>
    /// </summary>
    public static bool IsForcedUpdate(string? candidate, string? baseline)
    {
        if (!TryParse(candidate, out var next) || !TryParse(baseline, out var current)) return false;

        return !string.Equals(Segment(next, 0), Segment(current, 0), StringComparison.OrdinalIgnoreCase)
               || !string.Equals(Segment(next, 1), Segment(current, 1), StringComparison.OrdinalIgnoreCase);
    }

    private static string Segment(string[] segments, int index)
        => index < segments.Length ? segments[index] : "0";

    /// <summary>去掉 tag 上的 v / V 前缀，供显示用（比较时不必先去前缀，<see cref="Compare"/> 自己会处理）。</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..].Trim();
        return text;
    }

    private static bool TryParse(string? value, out string[] segments)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..].Trim();
        if (text.Length == 0) return false;

        var parts = text.Split('.');
        // 至少要有一段是数字，否则不认为这是版本号（空串、纯文本都归入无法解析）
        if (!parts.Any(part => int.TryParse(part, out _))) return false;

        segments = parts;
        return true;
    }
}