using System.Text.Json;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Updater;

/// <summary>下载页文件里的一条：名字 + 下载地址。</summary>
public sealed record ManifestEntry(string Name, string Url);

/// <summary>
/// 下载页文件（仓库 <c>survive</c> 分支根目录的 <c>download.json</c>）。
///
/// <para>
/// 结构是一个数组，每项含名字与链接：
/// <code>
/// { "items": [ { "name": "示例资源", "url": "https://example.com/a.zip" } ] }
/// </code>
/// 根节点直接用数组（<c>[ { "name": ..., "url": ... } ]</c>）也支持；
/// 字段名兼容 <c>name / 名字 / 名称 / title</c> 与 <c>url / 下载地址 / address / link / 地址</c>。
/// 目的是让下载页内容不改启动器就能更新：往这个文件里加条目即可。
/// </para>
///
/// <para>
/// 拉取走双线路（GitHub / Gitee），顺序与启动器自更新共用同一份偏好；失败不抛异常。
/// </para>
/// </summary>
public static class DownloadManifest
{
    /// <summary>下载页文件所在分支。</summary>
    public const string BranchName = "survive";

    /// <summary>下载页文件名。</summary>
    public const string FileName = "download.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly string[] NameKeys = ["name", "名字", "名称", "title"];
    private static readonly string[] UrlKeys = ["url", "下载地址", "address", "link", "地址"];
    private static readonly string[] ListKeys = ["items", "downloads", "list", "文件", "下载"];

    /// <summary>GitHub 的 raw 直链。</summary>
    public static string GitHubUrl =>
        AppInfo.GitHubUrl
            .Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
        + $"/{BranchName}/{FileName}";

    /// <summary>Gitee 的 raw 直链。</summary>
    public static string GiteeUrl => $"{AppInfo.GiteeUrl}/raw/{BranchName}/{FileName}";

    /// <summary>拉取并解析下载页文件。任何失败都写进返回的 <c>Error</c>，不抛异常（取消失控除外）。</summary>
    public static async Task<(IReadOnlyList<ManifestEntry> Items, string? Error)> FetchAsync(
        LauncherUpdateSource preferred = LauncherUpdateSource.Auto, CancellationToken token = default)
    {
        var order = preferred == LauncherUpdateSource.Gitee
            ? new[] { ("Gitee", GiteeUrl), ("GitHub", GitHubUrl) }
            : new[] { ("GitHub", GitHubUrl), ("Gitee", GiteeUrl) };

        var errors = new List<string>();

        foreach (var (name, url) in order)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", LauncherUpdater.UserAgent);

                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{name}：HTTP {(int)response.StatusCode}");
                    Log.Warn($"从 {name} 获取下载页文件失败：HTTP {(int)response.StatusCode}");
                    continue;
                }

                var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var items = Parse(text);

                Log.Info($"已获取下载页文件（{name}）：{items.Count} 条");
                return (items, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}：{ex.Message}");
                Log.Warn($"从 {name} 获取下载页文件失败：{ex.Message}");
            }
        }

        return ([], "两个线路都获取失败：" + string.Join("；", errors));
    }

    /// <summary>解析下载页文件 JSON。纯函数，便于自检与离线核对。</summary>
    public static IReadOnlyList<ManifestEntry> Parse(string? text)
    {
        var result = new List<ManifestEntry>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var array = root;

            // 允许根节点是数组，或是一个带 items / downloads 等字段的对象
            if (root.ValueKind == JsonValueKind.Object && !TryGetArray(root, out array))
                return result;

            if (array.ValueKind != JsonValueKind.Array) return result;

            foreach (var element in array.EnumerateArray())
            {
                // 也容忍「纯链接字符串」这种简写
                if (element.ValueKind == JsonValueKind.String)
                {
                    var only = element.GetString()?.Trim() ?? string.Empty;
                    if (only.Length > 0) Add(result, only, only);
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object) continue;

                var url = ReadString(element, UrlKeys);
                if (url.Length == 0) continue;

                var name = ReadString(element, NameKeys);
                Add(result, name.Length > 0 ? name : url, url);
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"下载页文件解析失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>同一个链接只保留第一次出现。</summary>
    private static void Add(List<ManifestEntry> result, string name, string url)
    {
        if (result.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase))) return;

        result.Add(new ManifestEntry(name, url));
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array)
    {
        foreach (var key in ListKeys)
        {
            if (root.TryGetProperty(key, out array) && array.ValueKind == JsonValueKind.Array) return true;
        }

        array = default;
        return false;
    }

    private static string ReadString(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return string.Empty;
    }
}
