using System.Net;
using System.Text.Json.Serialization;

namespace HMOL.Core.App;

/// <summary>主页「常用网站」小组件里的一条记录（名称 + 网址），随设置一起落盘。</summary>
public sealed class SiteLinkSetting
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>设置页与小组件显示用的简称；名称为空时退回网址主机名。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>网址的主机名（解析不出时返回原网址）。</summary>
    [JsonIgnore]
    public string Host => Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var uri) ? uri.Host : (Url ?? string.Empty);
}

/// <summary>
/// 常用网站的默认清单与网址校验。
/// 默认清单里的每个地址都实际访问确认过（可达且返回正常页面）；没验证过的地址一律不放进来。
/// 网址只允许 http / https：其它协议（file:、ms-*: 等）不允许作为外链打开。
/// </summary>
public static class SiteLinkCatalog
{
    /// <summary>
    /// 默认站点。仓库地址直接引用 <see cref="AppInfo"/> 里的常量，避免同一个地址写两份。
    /// </summary>
    public static List<SiteLinkSetting> Defaults() =>
    [
        new() { Name = "本项目 GitHub", Url = AppInfo.GitHubUrl },
        new() { Name = "本项目 Gitee", Url = AppInfo.GiteeUrl },
        new() { Name = "心灵终结官网", Url = "https://mentalomega.com/" },
        new() { Name = "官方 QQ 群", Url = AppInfo.QqGroupUrl }
    ];

    /// <summary>
    /// 是否是可用于外链的 http / https 地址。
    /// 除协议之外还要求主机名像样（含点号、localhost 或 IP）：像「https://notaurl」这种
    /// 只有协议头的输入会被挡下，免得用户点了之后只能看到浏览器的报错页。
    /// </summary>
    public static bool IsHttpUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        if (host.Length == 0) return false;

        return host.Contains('.', StringComparison.Ordinal)
               || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(host, out _);
    }

    /// <summary>
    /// 规范化一批站点：去掉名称为空或网址非法的条目，并补齐 http:// 前缀。
    /// 用户在设置页手输「www.xxx.com」这类地址时按 https 处理。
    /// </summary>
    public static List<SiteLinkSetting> Normalize(IEnumerable<SiteLinkSetting>? source)
    {
        var result = new List<SiteLinkSetting>();
        if (source is null) return result;

        foreach (var link in source)
        {
            if (link is null) continue;

            var url = CompleteUrl(link.Url);
            if (!IsHttpUrl(url)) continue;

            var name = (link.Name ?? string.Empty).Trim();

            result.Add(new SiteLinkSetting
            {
                Name = name.Length > 0 ? name : url,
                Url = url
            });
        }

        return result;
    }

    /// <summary>给缺协议的输入补上 https://；已经有协议的保持原样。</summary>
    public static string CompleteUrl(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        if (text.Contains("://", StringComparison.Ordinal)) return text;

        // 手输的主机名（含 localhost 之类）一律按 https 补全
        return "https://" + text;
    }
}
