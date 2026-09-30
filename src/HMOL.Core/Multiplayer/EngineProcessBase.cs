using System.Diagnostics;
using System.IO;
using System.Text;
using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 长驻引擎进程的公共部分：启动、逐行读取输出、环形缓冲、停止、释放。
///
/// 旧版 engine_easytier.py 与 engine_n2n.py 各自维护了一套一模一样的
/// <c>_proc / _output / _lock / _reader / recent_output / stop</c>，这里合并为基类，
/// 派生类只保留「参数怎么拼」与「怎么判定就绪」。
/// </summary>
public abstract class EngineProcessBase : IAsyncDisposable
{
    /// <summary>缓冲区上限；超过后只保留最近 <see cref="OutputKeep"/> 行（与旧版一致）。</summary>
    private const int OutputMax = 300;
    private const int OutputKeep = 200;

    /// <summary>RecentOutput 默认返回的行数（与旧版 recent_output(40) 一致）。</summary>
    private const int RecentDefault = 40;

    private readonly List<string> _output = [];
    private readonly object _outputLock = new();

    private Process? _process;

    /// <summary>引擎日志回调；由派生类在构造时传入。</summary>
    protected Action<string>? LogCallback { get; set; }

    /// <summary>已获得的虚拟 IP；未就绪时为 null。</summary>
    public string? LocalIp { get; protected set; }

    /// <summary>引擎进程是否存活。</summary>
    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null) return false;

            try { return !process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>最近 40 行输出拼接后的文本（行间 <see cref="Environment.NewLine"/>）。</summary>
    public string RecentOutput => string.Join(Environment.NewLine, RecentOutputOf(RecentDefault));

    /// <summary>引擎每输出一行触发一次（可能来自后台线程）。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>当前引擎进程；未启动或已回收时为 null。</summary>
    protected Process? CurrentProcess => _process;

    /// <summary>进程是否已退出（状态取不到时按已退出处理）。</summary>
    protected static bool HasExited(Process? process)
    {
        if (process is null) return true;

        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>取最近 <paramref name="count"/> 行输出。</summary>
    public IReadOnlyList<string> RecentOutputOf(int count)
    {
        if (count <= 0) return [];

        lock (_outputLock)
        {
            var take = Math.Min(count, _output.Count);
            return _output.GetRange(_output.Count - take, take);
        }
    }

    /// <summary>向日志回调追加一行（带引擎前缀）。</summary>
    protected void LogLine(string message)
    {
        try { LogCallback?.Invoke($"[{EnginePrefix}] {message}"); }
        catch (Exception ex) { Log.Warn($"引擎日志回调异常：{ex.Message}"); }
    }

    /// <summary>日志前缀，默认取展示名。</summary>
    protected virtual string EnginePrefix => GetType().Name;

    /// <summary>启动一个被重定向输出的子进程，并在后台逐行读取它的 stdout / stderr。</summary>
    protected Process Launch(
        string exe,
        string arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Encoding? encoding = null)
    {
        var startInfo = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? ProcessRunner.EngineEncoding,
            StandardErrorEncoding = encoding ?? ProcessRunner.EngineEncoding,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };

        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"无法启动进程：{exe}");

        _process = process;

        // stdout / stderr 各起一条泵；进程退出时读取会自然结束
        _ = PumpAsync(process.StandardOutput);
        _ = PumpAsync(process.StandardError);

        return process;
    }

    /// <summary>停止引擎进程并清空虚拟 IP。</summary>
    public Task StopAsync() => StopProcessAsync(TimeSpan.FromSeconds(5));

    /// <summary>停止引擎进程并回收资源（界面用 <c>await using</c> 时走这里）。</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>停止引擎进程，最多等 <paramref name="timeout"/> 秒后强杀。</summary>
    public async Task StopProcessAsync(TimeSpan timeout)
    {
        var process = Interlocked.Exchange(ref _process, null);
        LocalIp = null;

        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"结束 {EnginePrefix} 进程失败：{ex.Message}");
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : timeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // 等不到就算了，进程已被 Kill
        }
        finally
        {
            try { process.Dispose(); }
            catch { /* 忽略 */ }
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                AppendOutput(line.TrimEnd());
            }
        }
        catch
        {
            // 进程退出或管道关闭时抛出，属正常情况
        }
    }

    private void AppendOutput(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        lock (_outputLock)
        {
            _output.Add(line);
            if (_output.Count > OutputMax) _output.RemoveRange(0, _output.Count - OutputKeep);
        }

        try { OutputReceived?.Invoke(line); }
        catch (Exception ex) { Log.Warn($"引擎输出回调异常：{ex.Message}"); }

        try { OnEngineLine(line); }
        catch (Exception ex) { Log.Warn($"引擎日志过滤异常：{ex.Message}"); }
    }

    /// <summary>
    /// 派生类可覆写：决定哪些引擎输出要转发给日志回调。
    /// 默认全部不转发，因为 easytier / edge 会打印大量对用户无意义的调试噪音。
    /// </summary>
    protected virtual void OnEngineLine(string line)
    {
    }
}
