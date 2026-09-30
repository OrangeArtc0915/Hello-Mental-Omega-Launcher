using System.Diagnostics;
using HMOL.Core.Logging;

namespace HMOL.Core.Launch;

/// <summary>
/// 一次游戏进程会话。输出捕获用三段式：回调只把行塞进带锁缓冲 → 后台定时批量取出 → 解析等级后触发
/// <see cref="LineReceived"/>，避免在 OutputDataReceived 回调里做解析而卡住游戏进程。
/// </summary>
public sealed class GameProcessSession : IDisposable
{
    private const int PumpIntervalMs = 150;

    private readonly Process _process;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly TaskCompletionSource<int?> _exitCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _buffer = [];
    private readonly object _bufferLock = new();

    private ActivityLevel _lastLevel = ActivityLevel.Info;
    private volatile bool _killed;
    private volatile bool _disposed;

    internal GameProcessSession(Process process)
    {
        _process = process;
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnOutputDataReceived;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _ = Task.Run(PumpAsync);
        _ = Task.Run(WatchAsync);
    }

    /// <summary>启动的游戏主程序路径。</summary>
    public string ExecutablePath { get; internal set; } = string.Empty;

    /// <summary>进程是否仍在运行。</summary>
    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>退出码。进程未退出时为 null。</summary>
    public int? ExitCode { get; private set; }

    /// <summary>每读到一行游戏输出时触发。</summary>
    public event Action<GameLogLine>? LineReceived;

    /// <summary>进程退出时触发。被本会话结束（或退出码读不到）时传 null。</summary>
    public event Action<int?>? Exited;

    /// <summary>结束游戏进程（含子进程树）。</summary>
    public void Kill()
    {
        _killed = true;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"结束游戏进程失败：{ex.Message}");
        }
    }

    /// <summary>等待进程退出，返回退出码（被结束或读不到时返回 null）。</summary>
    public Task<int?> WaitForExitAsync(CancellationToken token = default)
        => _exitCompletion.Task.WaitAsync(token);

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_bufferLock) _buffer.Add(e.Data);
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(PumpIntervalMs, _pumpCts.Token).ConfigureAwait(false);
                FlushBuffer();
            }
        }
        catch (OperationCanceledException) { }

        FlushBuffer();
    }

    private async Task WatchAsync()
    {
        try
        {
            // 无参重载会一直等到进程退出，并保证异步输出回调处理完毕
            _process.WaitForExit();
        }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { return; }

        FlushBuffer();

        try { await _pumpCts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        FlushBuffer();

        int? reported;
        try
        {
            var code = _process.ExitCode;
            ExitCode = code;
            reported = _killed ? null : code;
        }
        catch (InvalidOperationException)
        {
            reported = null;
        }

        _exitCompletion.TrySetResult(reported);

        if (_disposed) return;

        try { Exited?.Invoke(reported); }
        catch (Exception ex) { Log.Warn($"处理游戏退出事件失败：{ex.Message}"); }
    }

    private void FlushBuffer()
    {
        List<string>? batch = null;

        lock (_bufferLock)
        {
            if (_buffer.Count == 0) return;

            batch = [.. _buffer];
            _buffer.Clear();
        }

        foreach (var line in batch) Emit(line);
    }

    private void Emit(string line)
    {
        var level = Classify(line);
        _lastLevel = level;

        if (_disposed) return;

        try { LineReceived?.Invoke(new GameLogLine(DateTime.Now, level, line)); }
        catch (Exception ex) { Log.Warn($"处理游戏日志行失败：{ex.Message}"); }
    }

    /// <summary>按关键字粗判等级，只用于界面着色。</summary>
    private ActivityLevel Classify(string line)
    {
        // 续行（异常堆栈）沿用上一行的等级
        if (line.StartsWith("\tat ", StringComparison.Ordinal) ||
            line.StartsWith("   at ", StringComparison.Ordinal) ||
            line.StartsWith("\t... ", StringComparison.Ordinal)) return _lastLevel;

        if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("exception", StringComparison.OrdinalIgnoreCase)) return ActivityLevel.Error;

        if (line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)) return ActivityLevel.Warn;

        return ActivityLevel.Info;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try { _pumpCts.Cancel(); }
        catch (ObjectDisposedException) { }

        // 进程仍在运行时交给 WatchAsync 在退出后释放，避免与其竞争句柄
        if (IsRunning) return;

        try { _process.Dispose(); }
        catch (Exception ex) { Log.Warn($"释放游戏进程句柄失败：{ex.Message}"); }
    }
}
