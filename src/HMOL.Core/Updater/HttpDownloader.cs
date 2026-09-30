using System.Net;
using System.Net.Http;
using HMOL.Core.Logging;

namespace HMOL.Core.Updater;

/// <summary>
/// 统一的 HTTP 文本获取与重定向探测。静态复用 HttpClient，失败自动重试，任何异常都不向外抛。
/// </summary>
public static class HttpDownloader
{
    /// <summary>默认 User-Agent。部分站点（Gitee、GitHub）会按 UA 拒绝空 UA 的请求。</summary>
    private const string DefaultUserAgent = LauncherUpdater.UserAgent;

    /// <summary>重试等待时间：最多重试 2 次，间隔递增。</summary>
    private static readonly int[] RetryDelaysMs = [800, 2000];

    /// <summary>GetStringAsync 的内存缓存有效期，避免同一进程内对同一地址反复请求。</summary>
    private static readonly TimeSpan TextCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 必须声明支持 gzip / deflate / brotli：GitHub 对不带 Accept-Encoding 的请求会直接返回 500，
    /// 表现为「检查更新失败」，很难查。
    /// </summary>
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                                 | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(100)
    };

    /// <summary>
    /// 专用于探测重定向的客户端。必须关掉自动跳转，否则请求会一路跟到最终响应，
    /// 拿到的是 200 而 Headers.Location 为空，就取不到跳转目标了。
    /// </summary>
    private static readonly HttpClient RedirectProbeClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                                 | DecompressionMethods.Brotli
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Dictionary<string, (string Text, DateTimeOffset FetchedAt)> TextCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object TextCacheLock = new();

    /// <summary>GET 一个文本资源（带缓存与重试）。失败返回 null，不抛出。</summary>
    public static async Task<string?> GetStringAsync(string url, string? userAgent = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var now = DateTimeOffset.Now;
        lock (TextCacheLock)
        {
            if (TextCache.TryGetValue(url, out var cached) && now - cached.FetchedAt < TextCacheTtl)
                return cached.Text;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = CreateRequest(url, userAgent);
                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
                response.EnsureSuccessStatusCode();

                var text = await response.Content.ReadAsStringAsync(token);

                lock (TextCacheLock)
                {
                    TextCache[url] = (text, DateTimeOffset.Now);
                }

                return text;
            }
            catch (OperationCanceledException)
            {
                Log.Warn($"请求已取消：{url}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn($"请求失败（第 {attempt + 1} 次）：{url}（{ex.Message}）");

                if (attempt >= RetryDelaysMs.Length) return null;

                try
                {
                    await Task.Delay(RetryDelaysMs[attempt], token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// 发一次不跟随重定向的 GET，返回 Location 头。
    /// 用于从 GitHub 网页版的 releases/latest 跳转地址里取版本号：它不经过 API，因此不受 60 次/小时的限流。
    /// </summary>
    public static async Task<string?> GetRedirectLocationAsync(string url, string? userAgent = null,
        CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);

            using var response = await RedirectProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var location = response.Headers.Location?.ToString();
            return string.IsNullOrWhiteSpace(location) ? null : location;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"探测重定向失败：{url}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>丢弃指定地址的内存缓存，供需要强制走网络的调用方使用。</summary>
    public static void InvalidateCache(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (TextCacheLock) TextCache.Remove(url);
    }

    private static HttpRequestMessage CreateRequest(string url, string? userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        return request;
    }
}