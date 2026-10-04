using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Updater;

/// <summary>一次下载的结果。失败时 <see cref="Message"/> 里是可读原因。</summary>
public sealed record DownloadResult(bool Success, string? FilePath, long BytesReceived, string Message);

/// <summary>
/// 支持断点续传的下载器。先写 <c>目标文件.part</c>，全部成功后再原子改名，
/// 半截文件不会被当成完整文件；失败可重试，服务器支持 Range 时能从上次断点继续。
/// 服务器支持 Range 且文件够大时改用多线程分片并发下载（见 <see cref="TrySegmentedAsync"/>），
/// 分片各自写 <c>.part.sNNN</c>，下完按顺序合并；分片失败会自动回退单连接。
/// 启动器本体有上百兆，因此客户端超时设为 10 分钟。
/// </summary>
public static class ResumableDownloader
{
    private const int BufferSize = 81920;

    /// <summary>分片下载时每片的目标大小（字节）。</summary>
    private const long SegmentUnitSize = 4L * 1024 * 1024;

    /// <summary>分片下载的最大并发路数。</summary>
    private const int MaxSegments = 4;

    /// <summary>小于这个大小的文件不值得分片，直接单连接下载。</summary>
    private const long MultiThreadThreshold = 8L * 1024 * 1024;

    /// <summary>重试等待：最多重试 3 次，指数退避；命中 Retry-After 时按服务器要求等待。</summary>
    private static readonly int[] RetryDelaysMs = [1000, 3000, 7000];

    /// <summary>下载用的 User-Agent，与检查更新保持一致，避免部分站点按 UA 拒绝。</summary>
    private const string DefaultUserAgent = LauncherUpdater.UserAgent;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    /// <summary>
    /// 下载到目标文件，支持断点续传。目标已存在且没有残留 .part 时不重复下载。
    /// <paramref name="multiThread"/> 为 null 时按设置里的开关决定是否分片并发。
    /// 任何异常都被捕获并写进结果，绝不向外抛。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken token = default,
        IReadOnlyDictionary<string, string>? headers = null, bool? multiThread = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new DownloadResult(false, null, 0, "下载地址为空");

        if (string.IsNullOrWhiteSpace(destinationPath))
            return new DownloadResult(false, null, 0, "目标文件路径为空");

        string partPath;
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            partPath = destinationPath + ".part";
        }
        catch (Exception ex)
        {
            Log.Warn($"准备下载目录失败：{destinationPath}（{ex.Message}）");
            return new DownloadResult(false, null, 0, $"准备下载目录失败：{ex.Message}");
        }

        // 目标文件已在、且没有残留分片，视为上一次已下完
        if (File.Exists(destinationPath) && !HasResidue(destinationPath))
        {
            var size = SafeLength(destinationPath);
            progress?.Report(1);
            return new DownloadResult(true, destinationPath, size, "目标文件已存在，跳过下载");
        }

        if (!HasFreeSpace(destinationPath, 1, out var spaceError))
            return new DownloadResult(false, null, 0, spaceError!);

        // 大文件优先走多线程分片；不支持 Range / 失败时回退到下面的单连接路径
        if (multiThread ?? SettingsStore.Current.MultiThreadDownload)
        {
            DownloadResult? segmented;

            try
            {
                segmented = await TrySegmentedAsync(url, destinationPath, partPath, headers, progress, token);
            }
            catch (OperationCanceledException)
            {
                Log.Info($"下载已取消：{url}");
                return new DownloadResult(false, null, SafeLength(partPath), "下载已取消");
            }
            catch (Exception ex)
            {
                Log.Warn($"分片下载异常，改用单连接：{url}（{ex.Message}）");
                segmented = null;
            }

            if (segmented is not null)
            {
                if (segmented.Success) return segmented;

                Log.Warn($"分片下载失败，改用单连接重试：{url}（{segmented.Message}）");
                CleanupResidue(destinationPath);
            }
        }
        else
        {
            // 关掉多线程时，清掉上次可能留下的分片，免得被当成「未下完」一直挂着
            CleanupSegments(destinationPath);
        }

        var received = File.Exists(partPath) ? SafeLength(partPath) : 0;
        var lastMessage = "下载失败";

        for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            AttemptOutcome outcome;

            try
            {
                outcome = await TryOnceAsync(url, destinationPath, partPath, progress, headers, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Log.Info($"下载已取消：{url}");
                return new DownloadResult(false, null, SafeLength(partPath), "下载已取消");
            }
            catch (OperationCanceledException)
            {
                outcome = new AttemptOutcome(true, null, "下载超时");
            }
            catch (Exception ex)
            {
                outcome = new AttemptOutcome(true, null, ex.Message);
            }

            // 兜底：正常路径下 Retry=false 一定带结果，这里避免任何情况下返回 null
            if (!outcome.Retry)
                return outcome.Result ?? new DownloadResult(false, null, SafeLength(partPath),
                    outcome.Message ?? "下载失败");

            lastMessage = outcome.Message ?? lastMessage;
            received = outcome.BytesReceived ?? SafeLength(partPath);
            Log.Warn($"下载失败（第 {attempt + 1} 次）：{url}（{lastMessage}）");

            if (attempt >= RetryDelaysMs.Length) break;

            var delay = outcome.RetryDelay ?? TimeSpan.FromMilliseconds(RetryDelaysMs[attempt]);

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return new DownloadResult(false, null, received, "下载已取消");
            }
        }

        return new DownloadResult(false, null, received, $"重试 {RetryDelaysMs.Length} 次后仍失败：{lastMessage}");
    }

    /// <summary>单次尝试：返回是否应重试，以及最终结果。</summary>
    private static async Task<AttemptOutcome> TryOnceAsync(string url, string destinationPath, string partPath,
        IProgress<double>? progress, IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        var startAt = File.Exists(partPath) ? SafeLength(partPath) : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);

        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key)) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        if (startAt > 0) request.Headers.Range = new RangeHeaderValue(startAt, null);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter;
            var message = $"HTTP {code} {response.ReasonPhrase}";
            if (retryAfter is not null) message += $"（Retry-After：{retryAfter}）";

            // 429 与 5xx 属于临时故障，可重试；其余（404/403 等）重试也没用
            var retryable = code is 408 or 429 or >= 500;
            var failure = new DownloadResult(false, null, startAt, message);

            return retryable
                ? new AttemptOutcome(true, failure, message,
                    retryAfter?.Delta is { } delta ? delta : null, startAt)
                : new AttemptOutcome(false, failure, message);
        }

        bool append;
        long total;

        if (response.StatusCode == HttpStatusCode.PartialContent && startAt > 0)
        {
            // 服务器接受了 Range，从断点续写
            append = true;
            total = response.Content.Headers.ContentRange?.Length
                    ?? startAt + (response.Content.Headers.ContentLength ?? 0);
        }
        else
        {
            // 服务器不支持 Range（返回 200），丢掉分片从头开始
            if (startAt > 0)
            {
                Log.Info($"服务器不支持断点续传，将从头下载：{url}");
                TryDelete(partPath);
                startAt = 0;
            }

            append = false;
            total = response.Content.Headers.ContentLength ?? -1;
        }

        if (total > 0 && !HasFreeSpace(destinationPath, total - startAt, out var spaceError))
            return new AttemptOutcome(false, new DownloadResult(false, null, startAt, spaceError!));

        var received = startAt;

        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var target = new FileStream(partPath,
                         append ? FileMode.Append : FileMode.Create, FileAccess.Write,
                         FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;

            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;

                // 拿不到总长度时回报不确定值 0
                progress?.Report(total > 0 ? Math.Clamp((double)received / total, 0d, 1d) : 0d);
            }
        }

        if (total > 0 && received < total)
        {
            // 连接被中途掐断：保留分片，下次续传
            return new AttemptOutcome(true, null,
                $"下载中断：已收 {FormatSize(received)} / {FormatSize(total)}", null, received);
        }

        File.Move(partPath, destinationPath, overwrite: true);
        progress?.Report(1);
        Log.Info($"下载完成：{destinationPath}（{FormatSize(received)}）");

        return new AttemptOutcome(false, new DownloadResult(true, destinationPath, received, "下载完成"));
    }

    /// <summary>
    /// 探测服务器是否支持 Range 以及文件总长度：只请求第 1 个字节，
    /// 返回 206 说明支持分片。探测失败不报错，交给单连接路径去暴露具体原因。
    /// </summary>
    private static async Task<(bool Supported, long Length)> ProbeAsync(string url,
        IReadOnlyDictionary<string, string>? headers, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        ApplyHeaders(request, headers);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var length = response.Content.Headers.ContentRange?.Length;
            return length is > 0 ? (true, length.Value) : (false, -1);
        }

        if (response.IsSuccessStatusCode)
            return (false, response.Content.Headers.ContentLength ?? -1);

        return (false, -1);
    }

    /// <summary>
    /// 多线程分片下载。返回 null 表示「这次不分片」（服务器不支持 Range 或文件太小），
    /// 由调用方退回单连接；否则返回分片下载的最终结果。
    /// </summary>
    private static async Task<DownloadResult?> TrySegmentedAsync(string url, string destinationPath,
        string partPath, IReadOnlyDictionary<string, string>? headers, IProgress<double>? progress,
        CancellationToken token)
    {
        var (supported, total) = await ProbeAsync(url, headers, token);

        if (!supported || total < MultiThreadThreshold) return null;

        if (!HasFreeSpace(destinationPath, total, out var spaceError))
            return new DownloadResult(false, null, 0, spaceError!);

        // 上次单连接留下的半截 .part 正好是文件开头，直接挪作第 1 片继续用
        ReusePartialAsFirstSegment(destinationPath, partPath);

        var segmentCount = (int)Math.Clamp((total + SegmentUnitSize - 1) / SegmentUnitSize, 2, MaxSegments);
        var prefix = destinationPath + ".part.s";

        // 多线程共同累加已收字节；Interlocked 保证不丢更新
        var receivedBox = new long[1];
        var tasks = new List<Task<(bool Ok, string Message)>>(segmentCount);

        for (var i = 0; i < segmentCount; i++)
        {
            var from = total * i / segmentCount;
            var to = total * (i + 1) / segmentCount - 1;
            if (to < from) continue;

            var segmentPath = $"{prefix}{i:000}";
            var existing = Math.Min(SafeLength(segmentPath), to - from + 1);
            if (existing > 0) Interlocked.Add(ref receivedBox[0], existing);

            tasks.Add(DownloadSegmentAsync(url, segmentPath, from, to, headers, receivedBox, progress, total, token));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (!result.Ok)
                return new DownloadResult(false, null, Interlocked.Read(ref receivedBox[0]), result.Message);
        }

        try
        {
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, BufferSize, useAsync: true))
            {
                for (var i = 0; i < segmentCount; i++)
                {
                    var segmentPath = $"{prefix}{i:000}";
                    if (!File.Exists(segmentPath)) continue;

                    await using var input = File.OpenRead(segmentPath);
                    await input.CopyToAsync(output, BufferSize, token);
                }
            }

            File.Move(partPath, destinationPath, overwrite: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"分片合并失败：{destinationPath}（{ex.Message}）");
            return new DownloadResult(false, null, Interlocked.Read(ref receivedBox[0]), $"分片合并失败：{ex.Message}");
        }

        CleanupSegments(destinationPath);
        progress?.Report(1);
        Log.Info($"分片下载完成：{destinationPath}（{FormatSize(total)}，{segmentCount} 路并发）");

        return new DownloadResult(true, destinationPath, total, "下载完成");
    }

    /// <summary>下载单个分片（含重试与断点续传）；已收字节累加到 <paramref name="receivedBox"/> 用于进度。</summary>
    private static async Task<(bool Ok, string Message)> DownloadSegmentAsync(string url, string segmentPath,
        long from, long to, IReadOnlyDictionary<string, string>? headers, long[] receivedBox,
        IProgress<double>? progress, long total, CancellationToken token)
    {
        var segmentLength = to - from + 1;
        var lastMessage = "分片下载失败";

        for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            var existing = SafeLength(segmentPath);

            if (existing > segmentLength)
            {
                // 分片比预期长（多半是上次残留的大小对不上），丢掉重来
                TryDelete(segmentPath);
                existing = 0;
            }

            if (existing >= segmentLength) return (true, string.Empty);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                ApplyHeaders(request, headers);
                request.Headers.Range = new RangeHeaderValue(from + existing, to);

                using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.StatusCode != HttpStatusCode.PartialContent)
                    return (false, $"服务器未按分片返回内容（HTTP {(int)response.StatusCode}）");

                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var target = new FileStream(segmentPath,
                                 existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
                                 FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;

                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), token);
                        Interlocked.Add(ref receivedBox[0], read);

                        var got = Interlocked.Read(ref receivedBox[0]);
                        progress?.Report(total > 0 ? Math.Clamp((double)got / total, 0d, 1d) : 0d);
                    }
                }

                var now = SafeLength(segmentPath);
                if (now >= segmentLength) return (true, string.Empty);

                lastMessage = $"分片下载中断：已收 {FormatSize(now - from)} / {FormatSize(segmentLength)}";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastMessage = "下载超时";
            }
            catch (Exception ex)
            {
                lastMessage = ex.Message;
            }

            if (attempt >= RetryDelaysMs.Length) break;

            try
            {
                await Task.Delay(RetryDelaysMs[attempt], token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return (false, $"重试 {RetryDelaysMs.Length} 次后仍失败：{lastMessage}");
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;

        foreach (var pair in headers)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key)) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    /// <summary>把上次单连接留下的半截 <c>.part</c> 挪作第 1 片，避免切换下载模式时白下。</summary>
    private static void ReusePartialAsFirstSegment(string destinationPath, string partPath)
    {
        try
        {
            if (!File.Exists(partPath) || SafeLength(partPath) == 0) return;

            var firstSegment = destinationPath + ".part.s000";

            if (File.Exists(firstSegment)) TryDelete(partPath);
            else File.Move(partPath, firstSegment);
        }
        catch (Exception ex)
        {
            Log.Warn($"复用半截下载文件失败：{partPath}（{ex.Message}）");
            TryDelete(partPath);
        }
    }

    /// <summary>是否有残留分片（<c>.part</c> 或 <c>.part.sNNN</c>），有就说明上次没下完。</summary>
    private static bool HasResidue(string destinationPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;

            var pattern = Path.GetFileName(destinationPath) + ".part*";
            return Directory.EnumerateFiles(directory, pattern).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清掉多线程分片（<c>.part.sNNN</c>），保留单连接的 <c>.part</c>。</summary>
    private static void CleanupSegments(string destinationPath)
        => CleanupMatching(destinationPath, ".part.s*");

    /// <summary>清掉目标文件的所有临时分片（单连接与多线程的都要）。</summary>
    private static void CleanupResidue(string destinationPath)
        => CleanupMatching(destinationPath, ".part*");

    private static void CleanupMatching(string destinationPath, string suffixPattern)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            var pattern = Path.GetFileName(destinationPath) + suffixPattern;
            foreach (var file in Directory.EnumerateFiles(directory, pattern)) TryDelete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理下载分片失败：{destinationPath}（{ex.Message}）");
        }
    }

    /// <summary>目标盘剩余空间预检查。</summary>
    private static bool HasFreeSpace(string path, long requiredBytes, out string? error)
    {
        error = null;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;

            if (requiredBytes > 0 && drive.AvailableFreeSpace < requiredBytes)
            {
                error = $"磁盘剩余空间不足：需要 {FormatSize(requiredBytes)}，可用 {FormatSize(drive.AvailableFreeSpace)}";
                Log.Warn($"磁盘空间不足：{path}（{error}）");
                return false;
            }
        }
        catch (Exception ex)
        {
            // 空间检查失败不阻断下载
            Log.Warn($"磁盘空间检查失败：{path}（{ex.Message}）");
        }

        return true;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理分片文件失败：{path}（{ex.Message}）");
        }
    }

    /// <summary>把字节数格式化成可读大小，供界面与日志共用。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }

    /// <summary>单次尝试的结果：要么给出最终结果，要么给出可重试的原因。</summary>
    private sealed record AttemptOutcome(bool Retry, DownloadResult? Result, string? Message = null,
        TimeSpan? RetryDelay = null, long? BytesReceived = null);
}