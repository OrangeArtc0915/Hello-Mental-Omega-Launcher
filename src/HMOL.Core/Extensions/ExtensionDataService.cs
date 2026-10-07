using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Extensions;

/// <summary>
/// 扩展数据源：只读 GET 一个返回 JSON 的公开接口，按清单里的路径取出一个值当卡片主数值。
///
/// 边界（本机制的网络红线）：
/// <list type="bullet">
/// <item>只发 <b>GET</b>，且清单里的网址必须通过 <see cref="SiteLinkCatalog.IsHttpUrl"/>（只放行 http / https）；</item>
/// <item>有超时；失败、超时、返回的不是 JSON、路径取不到值 —— 一律降级为「显示清单里的静态内容 + 一句弱化提示」，不抛异常；</item>
/// <item>结果缓存在<b>内存</b>里（有效期 = 清单里的刷新间隔，最少 60 秒），所以反复进主页不会重复打同一个接口；</item>
/// <item>绝不写文件（扩展缓存不落盘）、不写注册表、不带任何凭据；</item>
/// <item>总开关在设置里（<see cref="Settings.AllowExtensionNetwork"/>），关掉之后连请求都不会发。</item>
/// </list>
/// </summary>
public static class ExtensionDataService
{
    /// <summary>单次请求超时。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient Client = new() { Timeout = Timeout };

    /// <summary>进程内的结果缓存：扩展 ID →（取到的值, 取到的时刻）。</summary>
    private static readonly Dictionary<string, (string Value, DateTimeOffset At)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>清空内存缓存（手工刷新数据源时用）。</summary>
    public static void ClearCache()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>
    /// 只用清单里的静态内容与<b>内存缓存</b>拼一张卡：不联网，随时可调用。
    /// 主页先用它把卡片画出来（缓存命中时还能直接显示上次取到的值），再去联网刷新。
    /// </summary>
    public static ExtensionCard BuildCached(ExtensionInfo info) => Compose(info).Card;

    /// <summary>
    /// 组装一张扩展卡片：先铺清单里的静态内容，有数据源就把取到的值盖到主数值上。
    /// 缓存还是新鲜的就直接用缓存，不发请求；任何失败都只会变成卡片上的一句弱化提示，绝不向上抛异常。
    /// </summary>
    public static async Task<ExtensionCard> BuildAsync(ExtensionInfo info, CancellationToken token = default)
    {
        var (card, fromCache) = Compose(info);

        if (fromCache || card.Error is not null || info.Manifest?.DataSource is not { } source) return card;

        // 总开关关着就一个请求都不发：Compose 已经给出「已关闭联网 + 静态内容」的那张卡
        if (!SettingsStore.Current.AllowExtensionNetwork) return card;

        var fetched = await FetchAsync(source, token).ConfigureAwait(false);

        if (fetched.Ok)
        {
            Store(info.Id, fetched.Value!);
            Log.Info(Loc.F("扩展「{0}」数据源已更新：{1}", info.Id, fetched.Value));
            return card with { Value = fetched.Value! };
        }

        Log.Info(Loc.F("扩展「{0}」数据源不可用：{1}（已降级为清单里的静态内容）", info.Id, fetched.Error));

        return card with { Note = Loc.F("数据源暂时取不到（{0}），当前显示的是清单中的静态内容。", fetched.Error) };
    }

    /// <summary>
    /// 拼卡：清单里的静态内容 + 命中内存缓存的数据源值。
    /// <c>FromCache</c> 为 true 表示主数值已经来自缓存，不必再联网。
    /// </summary>
    private static (ExtensionCard Card, bool FromCache) Compose(ExtensionInfo info)
    {
        if (info.Manifest is not { } manifest)
            return (new ExtensionCard { Error = info.Error ?? Loc.T("扩展清单不可用") }, false);

        var lines = (manifest.Lines ?? [])
            .Select(line => line.Display)
            .Where(text => text.Length > 0)
            .ToList();

        var card = new ExtensionCard { Value = manifest.Value ?? string.Empty, Lines = lines };

        if (manifest.DataSource is not { } source) return (card, false);

        if (!SettingsStore.Current.AllowExtensionNetwork)
            return (card with { Note = Loc.T("扩展联网已在设置里关闭，这里显示的是清单中的静态内容。") }, false);

        return TryGetCached(info.Id, source, out var cached) ? (card with { Value = cached }, true) : (card, false);
    }

    // ————— 网络 —————

    /// <summary>结果：要么取到一个字符串，要么带回一句可读中文原因。</summary>
    private readonly record struct FetchResult(bool Ok, string? Value, string? Error)
    {
        public static FetchResult From(string value) => new(true, value, null);

        public static FetchResult Failed(string reason) => new(false, null, reason);
    }

    private static async Task<FetchResult> FetchAsync(ExtensionDataSource source, CancellationToken token)
    {
        var url = source.Url ?? string.Empty;

        // 双保险：清单在写入时已校验过，这里再拦一次，防止有人手工把网址改坏
        if (!SiteLinkCatalog.IsHttpUrl(url)) return FetchResult.Failed(Loc.T("清单里的数据源网址不是有效的 http/https 地址"));

        try
        {
            using var response = await Client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return FetchResult.Failed(Loc.F("接口返回 HTTP {0}", (int)response.StatusCode));

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            if (!TrySelect(document.RootElement, source.Path, out var value, out var reason))
                return FetchResult.Failed(reason);

            if (value.Length == 0) return FetchResult.Failed(Loc.T("取到的值是空的"));

            return FetchResult.From(Trim(value));
        }
        catch (OperationCanceledException)
        {
            return FetchResult.Failed(Loc.T("请求超时"));
        }
        catch (HttpRequestException ex)
        {
            return FetchResult.Failed(Loc.F("连不上这个网址（{0}）", ex.Message));
        }
        catch (JsonException)
        {
            return FetchResult.Failed(Loc.T("返回的内容不是有效的 JSON"));
        }
        catch (Exception ex)
        {
            return FetchResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 按「.」分段取值：对象按属性名进，数组按数字下标进。
    /// 路径留空表示取根值（要求根是字符串 / 数字 / 布尔）。
    /// </summary>
    private static bool TrySelect(JsonElement root, string? path, out string value, out string reason)
    {
        value = string.Empty;
        reason = string.Empty;

        var current = root;
        var text = (path ?? string.Empty).Trim();

        if (text.Length > 0)
        {
            foreach (var segment in text.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (current.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (!current.TryGetProperty(segment, out var property))
                        {
                            reason = Loc.F("返回内容里找不到路径「{0}」", segment);
                            return false;
                        }

                        current = property;
                        break;

                    case JsonValueKind.Array:
                        if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                            index < 0 || index >= current.GetArrayLength())
                        {
                            reason = Loc.F("路径「{0}」不是有效的数组下标", segment);
                            return false;
                        }

                        current = current[index];
                        break;

                    default:
                        reason = Loc.F("路径「{0}」已经越过了可以继续深入的层级", segment);
                        return false;
                }
            }
        }

        switch (current.ValueKind)
        {
            case JsonValueKind.String:
                value = current.GetString() ?? string.Empty;
                return true;

            case JsonValueKind.Number:
                value = current.GetRawText();
                return true;

            case JsonValueKind.True:
                value = Loc.T("是");
                return true;

            case JsonValueKind.False:
                value = Loc.T("否");
                return true;

            case JsonValueKind.Null:
                reason = Loc.T("取到的值是 null");
                return false;

            default:
                reason = Loc.T("取到的值是一个对象或数组，无法直接显示成一行文字");
                return false;
        }
    }

    /// <summary>按主数值的长度上限截断，免得把卡片撑破。</summary>
    private static string Trim(string value)
    {
        var text = value.Trim();

        return text.Length <= ExtensionLimits.ValueMax
            ? text
            : text[..ExtensionLimits.ValueMax] + "…";
    }

    // ————— 内存缓存 —————

    private static bool TryGetCached(string id, ExtensionDataSource source, out string value)
    {
        value = string.Empty;

        var ttl = TimeSpan.FromSeconds(Math.Clamp(source.RefreshSeconds,
            ExtensionLimits.RefreshSecondsMin, ExtensionLimits.RefreshSecondsMax));

        lock (Cache)
        {
            if (!Cache.TryGetValue(id, out var entry)) return false;
            if (DateTimeOffset.Now - entry.At >= ttl) return false;

            value = entry.Value;
            return true;
        }
    }

    private static void Store(string id, string value)
    {
        lock (Cache) Cache[id] = (value, DateTimeOffset.Now);
    }
}
