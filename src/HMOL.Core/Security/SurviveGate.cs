using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Security;

/// <summary>
/// 远程门锁开关：读取 <c>survive</c> 分支上的 <c>survive.yml</c>，
/// 其中 <c>HMOL_survive</c> 为 false 时禁止启动（提示「此版本HMOL暂停支持！请联系域管理员！」）。
///
/// 拉不到配置时的取舍（断网 / 被墙 / 仓库挂掉都算拉不到）：
/// 优先沿用**上次成功读到的结果**——这样一旦被暂停，用户就算拔网线也依然拦得住；
/// 从未成功读到过则放行——避免因为网络问题把所有人挡在门外。
/// </summary>
public static class SurviveGate
{
    /// <summary>门锁对应的错误代码。旧版错误代码用到 E1-E11，这里新起 E12 一类。</summary>
    public const string ErrorCode = "E12.01";

    /// <summary>被暂停时给用户看的提示。</summary>
    public const string BlockedMessage = "此版本HMOL暂停支持！请联系域管理员！";

    /// <summary>配置文件里控制开关的键名（大小写不敏感）。</summary>
    private const string KeyName = "HMOL_survive";

    /// <summary>配置来源，按顺序尝试：GitHub 优先，Gitee 作为备用源。</summary>
    private static readonly string[] Sources =
    [
        "https://raw.githubusercontent.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/survive/survive.yml",
        "https://gitee.com/orangearc655743/Hello-Mental-Omega-Launcher/raw/survive/survive.yml"
    ];

    /// <summary>来源的简称，用于日志与提示。</summary>
    public static string SourcesText => string.Join("、", Sources.Select(SourceName));

    /// <summary>
    /// 单个来源的请求超时。严格模式下启动要等这次校验，所以给得比较短。
    /// 若某个来源常年不可达（例如国内访问 GitHub），这个值就是「启动要多等几秒」的上限。
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 一轮校验的等待预算，**必须大于 <see cref="RequestTimeout"/>**。
    /// 各来源是并行请求、用 Task.WhenAll 汇总，若预算等于单源超时，最慢的源会刚好把整轮拖过期限，
    /// 导致可用来源的好结果被一起丢掉；严格模式下这就等于把所有人误判成「读不到」而拦下（实测踩过）。
    /// </summary>
    private static readonly TimeSpan VerifyBudget = RequestTimeout + TimeSpan.FromSeconds(2);

    /// <summary>校验轮数：读不到就再来一轮，降低网络抖动导致的误伤。</summary>
    private const int VerifyAttempts = 2;

    /// <summary>
    /// 上次成功读取的结果落在这个文件里。刻意取了个不起眼的名字，并打上「隐藏 + 系统」属性：
    /// 让用户不容易发现、顺手删掉它来绕开门锁（加了 System 之后，光在资源管理器里勾
    /// 「显示隐藏的文件」也看不到，还得另外取消勾选「隐藏受保护的操作系统文件」）。
    /// 同目录下还有壁纸解包缓存，所以只隐藏这个文件，不动整个 Cache 目录。
    /// 注意这只是提高门槛，并非绝对防护：知道路径 + 断网的用户仍能删掉它绕过。
    /// </summary>
    public static string CacheFile => Path.Combine(Paths.Cache, "hmol.idx");

    private static readonly HttpClient Client = new() { Timeout = RequestTimeout };

    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>一次门锁判定的结果。</summary>
    /// <param name="Allowed">是否允许启动。</param>
    /// <param name="Source">结果来自哪个来源（联网时为 URL，缓存时为上次的来源）。</param>
    /// <param name="At">该结果取得的时刻。</param>
    /// <param name="FromCache">是否来自本地缓存。</param>
    public sealed record SurviveState(bool Allowed, string Source, DateTimeOffset At, bool FromCache)
    {
        /// <summary>给日志与提示窗用的一句话说明。</summary>
        public string Describe()
        {
            var source = SourceName(Source);

            return FromCache
                ? $"判定依据：上次从 {source} 读取的结果（{At.ToLocalTime():yyyy-MM-dd HH:mm}）"
                : $"判定依据：{source} 实时读取";
        }
    }

    /// <summary>
    /// 启动前严格校验的结论。
    /// <paramref name="Undetected"/> 为 true 表示所有来源都没读到配置
    /// （断网 / 被墙 / 超时），严格模式下同样判定为禁用。
    /// </summary>
    /// <param name="Allowed">是否放行。</param>
    /// <param name="State">读到配置时的结果；没读到为 null。</param>
    /// <param name="Undetected">是否是「读不到」导致的拦截。</param>
    public sealed record GateVerdict(bool Allowed, SurviveState? State, bool Undetected);

    /// <summary>缓存记录的落盘结构。</summary>
    private sealed record CacheRecord
    {
        [JsonPropertyName("allowed")] public bool Allowed { get; set; }

        [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;

        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    }

    /// <summary>
    /// 读本地缓存。命中时立刻就能判定，不必等网络——被暂停过的程序即使断网也照样拦得住。
    /// 缓存缺失或损坏时返回 null。
    /// </summary>
    public static SurviveState? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;

            var record = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(CacheFile), CacheOptions);
            if (record is null) return null;

            EnsureHidden();

            return new SurviveState(record.Allowed, record.Source, record.At, FromCache: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"读取门锁缓存失败（按无缓存处理）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 联网读取开关；成功则顺手写缓存。
    /// 判定规则是「**任一说停就停**」：只要有一个来源读到 false 就判定为拦截，
    /// 所有可达来源都读不到 false 才放行——这样镜像站上残留的旧 true 绕不过去。
    /// 反过来，全部来源都读不到配置时返回 null（调用方按缓存 / 放行处理）。
    /// </summary>
    public static async Task<SurviveState?> FetchAsync(CancellationToken token = default)
    {
        // 并行请求全部来源：串行时排在前面的来源一旦卡满超时，后面的来源就轮不到被问，
        // 会导致「明明有源在放行，却因为另一个源不通而被拦下」这种错判。
        var tasks = Sources.Select(source => TryReadAsync(source, token)).ToArray();
        var values = await Task.WhenAll(tasks).ConfigureAwait(false);

        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] != false) continue;

            var blocked = new SurviveState(false, Sources[i], DateTimeOffset.Now, FromCache: false);
            Log.Warn($"门锁开关：{Sources[i]} 读到 false，按「任一说停就停」判定为拦截");
            WriteCache(blocked);

            return blocked;
        }

        var reached = values.Count(value => value is not null);

        if (reached == 0)
        {
            Log.Warn("门锁开关：所有来源都没读到配置，按缓存或放行处理");
            return null;
        }

        var source = Sources[Array.IndexOf(values, true)];
        var allowed = new SurviveState(true, source, DateTimeOffset.Now, FromCache: false);

        Log.Info($"门锁开关：HMOL_survive=true（{reached} 个可达来源都没说要停）");
        WriteCache(allowed);

        return allowed;
    }

    /// <summary>
    /// 带超时的同步读取，供启动早期在 UI 线程上调用（内部全是 ConfigureAwait(false)，
    /// 不会把延续排回 UI 线程，所以在这里阻塞等待是安全的）。
    /// 等待预算用 <see cref="VerifyBudget"/>（> 单源超时），超时或读不到返回 null。
    /// </summary>
    public static SurviveState? FetchWithTimeout()
    {
        try
        {
            var task = FetchAsync();

            if (task.Wait(VerifyBudget)) return task.Result;

            Log.Warn($"门锁开关：{VerifyBudget.TotalSeconds:0.#} 秒内没等到结果，本轮按读不到处理");
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"门锁读取失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 启动前的**严格校验**：必须读到配置才放行。
    ///
    /// - 任一来源读到 false → 拦截
    /// - 所有可达来源都读到 true → 放行
    /// - **全都读不到（断网 / 被墙 / 超时）→ 同样拦截**（这就是"特别严格"）
    ///
    /// 读不到时会重试一轮（<see cref="VerifyAttempts"/>），降低网络抖动造成的误伤；
    /// 最坏情况下的启动等待 = 轮数 × 单次超时。
    /// </summary>
    public static GateVerdict Verify()
    {
        for (var attempt = 1; attempt <= VerifyAttempts; attempt++)
        {
            var state = FetchWithTimeout();

            if (state is not null) return new GateVerdict(state.Allowed, state, Undetected: false);

            Log.Warn($"门锁开关：第 {attempt}/{VerifyAttempts} 次校验没读到配置");

            if (attempt < VerifyAttempts) Thread.Sleep(1000);
        }

        Log.Warn($"门锁开关：{SourcesText} 都读不到配置，严格模式下判定为禁用");
        return new GateVerdict(false, ReadCache(), Undetected: true);
    }

    /// <summary>读单个来源并解析出开关；读不到或解析不出返回 null。</summary>
    private static async Task<bool?> TryReadAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Client.GetAsync(url, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"门锁开关：{url} 返回 {(int)response.StatusCode}，尝试下一个来源");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var value = Parse(content);

            if (value is null) Log.Debug($"门锁开关：{url} 里没有可识别的 {KeyName} 字段");

            return value;
        }
        catch (Exception ex)
        {
            Log.Debug($"门锁开关：读取 {url} 失败（{ex.Message}），尝试下一个来源");
            return null;
        }
    }

    private static void WriteCache(SurviveState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFile);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 必须先摘掉隐藏 / 系统属性再写：带这些属性的文件在部分环境下会被拒绝写入，
            // 结果就是缓存永远停在第一次的值（实测复现）。写完由 EnsureHidden 再打回去。
            ClearHidden();

            var record = new CacheRecord { Allowed = state.Allowed, Source = state.Source, At = state.At };
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(record, CacheOptions));

            EnsureHidden();
        }
        catch (Exception ex)
        {
            Log.Debug($"写入门锁缓存失败（不影响本次判定）：{ex.Message}");
        }
    }

    /// <summary>临时摘掉隐藏 / 系统属性，方便覆盖写入。</summary>
    private static void ClearHidden()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;

            var attributes = File.GetAttributes(CacheFile);
            var cleared = attributes & ~(FileAttributes.Hidden | FileAttributes.System);

            if (attributes == cleared) return;

            File.SetAttributes(CacheFile, cleared);
        }
        catch (Exception ex)
        {
            Log.Debug($"清除门锁缓存属性失败：{ex.Message}");
        }
    }

    /// <summary>给缓存文件打上「隐藏 + 系统」属性；失败不影响功能，也不该让判定出错。</summary>
    private static void EnsureHidden()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;

            var attributes = File.GetAttributes(CacheFile);
            var wanted = attributes | FileAttributes.Hidden | FileAttributes.System;

            if (attributes == wanted) return;

            File.SetAttributes(CacheFile, wanted);
        }
        catch (Exception ex)
        {
            Log.Debug($"设置门锁缓存属性失败（不影响功能）：{ex.Message}");
        }
    }

    /// <summary>把来源 URL 归成用户看得懂的简称。</summary>
    private static string SourceName(string url)
        => url.Contains("gitee", StringComparison.OrdinalIgnoreCase) ? "Gitee" : "GitHub";

    /// <summary>
    /// 从配置文本里解析出开关值。刻意写得宽松：忽略注释与空行、键名大小写不敏感、
    /// 值允许带引号或行尾注释，并接受 true/false、yes/no、on/off、1/0 几种常见写法。
    /// 找不到或值无法识别时返回 null。
    /// </summary>
    public static bool? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            if (!line[..separator].Trim().Equals(KeyName, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(separator + 1)..];

            // 去掉行尾注释，再去掉引号与空白
            var comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment];

            value = value.Trim().Trim('"', '\'').Trim().ToLowerInvariant();

            return value switch
            {
                "true" or "yes" or "on" or "1" => true,
                "false" or "no" or "off" or "0" => false,
                _ => null
            };
        }

        return null;
    }
}
