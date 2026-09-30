using HMOL.Core.Logging;

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
            Log.Warn($"版本号无法解析，按相等处理：left=\"{left}\"，right=\"{right}\"");
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