using HMOL.Core.App;

namespace HMOL.Core.Extensions;

/// <summary>
/// 扩展清单的校验。所有问题都以<b>可读中文</b>返回，界面直接显示给用户。
///
/// 校验的立场是「严格但绝不抛异常」：清单再离谱也只能让这一个扩展被判无效并跳过，
/// 不会影响启动、不会影响其它扩展、更不会影响主页照常显示。
/// </summary>
public static class ExtensionValidator
{
    /// <summary>
    /// 校验一份清单。<paramref name="manifest"/> 为 null（文件读不出来）时同样返回原因。
    /// 返回 null 表示这份清单可用；否则返回一句中文原因（已带字段名，便于用户定位）。
    /// </summary>
    public static string? Validate(ExtensionManifest? manifest)
    {
        if (manifest is null) return "清单内容为空";

        var data = manifest.Normalize();

        if (data.Title!.Length == 0) return "缺少必填字段「title」（卡片标题）";

        var titleError = Limit("title（卡片标题）", data.Title, ExtensionLimits.TitleMax);
        if (titleError is not null) return titleError;

        var versionError = Limit("version（版本号）", data.Version, ExtensionLimits.VersionMax);
        if (versionError is not null) return versionError;

        var authorError = Limit("author（作者）", data.Author, ExtensionLimits.AuthorMax);
        if (authorError is not null) return authorError;

        var descriptionError = Limit("description（说明）", data.Description, ExtensionLimits.DescriptionMax);
        if (descriptionError is not null) return descriptionError;

        var iconError = Limit("icon（图标名）", data.Icon, ExtensionLimits.IconMax);
        if (iconError is not null) return iconError;

        if (data.Icon!.Length > 0 && !ExtensionIcons.Exists(data.Icon))
        {
            return $"图标「{data.Icon}」不在内置图标包里（可用图标见 docs/extensions.md 的图标清单；" +
                   $"也可以留空用默认图标 {ExtensionIcons.Fallback}）";
        }

        var valueError = Limit("value（主数值）", data.Value, ExtensionLimits.ValueMax);
        if (valueError is not null) return valueError;

        var linesError = ValidateLines(data);
        if (linesError is not null) return linesError;

        var dataSourceError = ValidateDataSource(data);
        if (dataSourceError is not null) return dataSourceError;

        if (data.Link!.Length > 0)
        {
            var linkError = Limit("link（点击打开的网址）", data.Link, ExtensionLimits.UrlMax);
            if (linkError is not null) return linkError;

            if (!SiteLinkCatalog.IsHttpUrl(data.Link))
                return $"link（点击打开的网址）不是有效的 http/https 地址：{data.Link}";
        }

        // 卡片上什么都没有的话，主页会出现一张只有标题的空卡，不如直接判无效
        if (data.Value!.Length == 0 && data.Lines!.Count == 0 && data.DataSource is null)
            return "清单里至少要有一项内容：value（主数值）、lines（文字行）或 dataSource（数据源）";

        return null;
    }

    private static string? ValidateLines(ExtensionManifest data)
    {
        var lines = data.Lines ?? [];

        if (lines.Count > ExtensionLimits.LineCountMax)
            return $"lines（文字行）最多 {ExtensionLimits.LineCountMax} 行，当前 {lines.Count} 行";

        for (var i = 0; i < lines.Count; i++)
        {
            var labelError = Limit($"lines[{i}].label（标签）", lines[i].Label, ExtensionLimits.LineLabelMax);
            if (labelError is not null) return labelError;

            var textError = Limit($"lines[{i}].text（内容）", lines[i].Text, ExtensionLimits.LineTextMax);
            if (textError is not null) return textError;
        }

        return null;
    }

    private static string? ValidateDataSource(ExtensionManifest data)
    {
        if (data.DataSource is not { } source) return null;

        if ((source.Url ?? string.Empty).Length == 0)
            return "dataSource（数据源）缺了必填的 url";

        var urlError = Limit("dataSource.url（数据源网址）", source.Url, ExtensionLimits.UrlMax);
        if (urlError is not null) return urlError;

        if (!SiteLinkCatalog.IsHttpUrl(source.Url))
            return $"dataSource.url（数据源网址）不是有效的 http/https 地址：{source.Url}";

        var pathError = Limit("dataSource.path（取值路径）", source.Path, ExtensionLimits.DataPathMax);
        if (pathError is not null) return pathError;

        if (source.RefreshSeconds < ExtensionLimits.RefreshSecondsMin)
        {
            return $"dataSource.refreshSeconds（刷新间隔）不能小于 {ExtensionLimits.RefreshSecondsMin} 秒" +
                   $"（现在是 {source.RefreshSeconds}），避免过于频繁地请求别人的接口";
        }

        if (source.RefreshSeconds > ExtensionLimits.RefreshSecondsMax)
        {
            return $"dataSource.refreshSeconds（刷新间隔）不能大于 {ExtensionLimits.RefreshSecondsMax} 秒" +
                   $"（现在是 {source.RefreshSeconds}）";
        }

        return null;
    }

    /// <summary>单字段长度校验；超长时给出「字段名 上限 N 字符（当前 M）」这样的原因。</summary>
    private static string? Limit(string field, string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? null : $"{field}最多 {max} 个字符（当前 {text.Length} 个）";
    }

    /// <summary>
    /// 校验扩展文件名（也就是扩展 ID）。写操作落盘前必须过这一关：
    /// 只允许普通文件名，拒绝路径分隔符、上跳与 Windows 保留名，防目录穿越。
    /// </summary>
    public static string? ValidateId(string? id)
    {
        var name = (id ?? string.Empty).Trim();

        if (name.Length == 0) return "扩展名不能为空";

        if (name.Length > ExtensionLimits.IdMax)
            return $"扩展名最多 {ExtensionLimits.IdMax} 个字符（当前 {name.Length} 个）";

        if (name is "." or "..") return "扩展名不能是「.」或「..」";

        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "扩展名里不能包含 \\ / : * ? \" < > | 这些字符";

        if (name.Any(char.IsControl)) return "扩展名里不能包含控制字符";

        var stem = name.Split('.')[0].ToUpperInvariant();

        if (ReservedNames.Contains(stem))
            return $"扩展名「{name}」是系统保留名，换一个吧";

        return null;
    }

    /// <summary>Windows 下不能作为文件名的保留名（不区分大小写，带扩展名也算）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
}
