using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using HMOL.Core.Logging;

namespace HMOL.Core.Updater;

/// <summary>一次下载的结果。失败时 <see cref="Message"/> 里是可读原因。</summary>
public sealed record DownloadResult(bool Success, string? FilePath, long BytesReceived, string Message);

/// <summary>
/// 支持断点续传的下载器。先写 <c>目标文件.part</c>，全部成功后再原子改名，
/// 半截文件不会被当成完整文件；失败可重试，服务器支持 Range 时能从上次断点继续。
/// 启动器本体有上百兆，因此客户端超时设为 10 分钟。
/// </summary>
public static class ResumableDownloader
{
    private const int BufferSize = 81920;

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
    /// 任何异常都被捕获并写进结果，绝不向外抛。
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(string url, string destinationPath,
        IProgress<double>? progress = null, CancellationToken token = default,
        IReadOnlyDictionary<string, string>? headers = null)
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
        if (File.Exists(destinationPath) && !File.Exists(partPath))
        {
            var size = SafeLength(destinationPath);
            progress?.Report(1);
            return new DownloadResult(true, destinationPath, size, "目标文件已存在，跳过下载");
        }

        if (!HasFreeSpace(destinationPath, 1, out var spaceError))
            return new DownloadResult(false, null, 0, spaceError!);

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