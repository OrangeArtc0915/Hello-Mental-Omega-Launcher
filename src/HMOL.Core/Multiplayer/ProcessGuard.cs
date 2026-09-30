using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 进程守护：后台轮询受监控进程（edge.exe / easytier-core.exe / supernode.exe）是否存活，
/// 异常退出时触发重启回调（带防抖与频率限制）。对应旧版 guard.py:43 <c>ProcessGuard</c>。
///
/// <para>
/// 防抖：<c>minRestartGapSeconds</c> 秒内不重复重启；
/// 频率限制：窗口期内重启超过 <c>maxRestarts</c> 次则停止自动重启（避免崩溃循环刷屏）。
/// </para>
/// </summary>
public sealed class ProcessGuard : IAsyncDisposable
{
    private readonly Func<string, Task> _onLost;
    private readonly Action<string>? _log;
    private readonly double _intervalSeconds;
    private readonly double _minRestartGapSeconds;
    private readonly int _maxRestarts;
    private readonly double _windowSeconds;
    private readonly object _lock = new();
    private readonly List<double> _restarts = [];

    private readonly CancellationTokenSource _cts = new();
    private List<string> _watch = [];
    private Task? _loopTask;
    private bool _enabled = true;
    private double _lastRestart;

    /// <param name="onLost">监控到进程消失时回调（参数是进程名），应由调用方触发引擎重启。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="intervalSeconds">轮询间隔（旧版 guard.py:53 <c>interval=3.0</c>）。</param>
    /// <param name="minRestartGapSeconds">重启防抖（旧版 guard.py:54 <c>min_restart_gap=10.0</c>）。</param>
    /// <param name="maxRestarts">窗口内最大重启次数（旧版 guard.py:55 <c>max_restarts=3</c>）。</param>
    /// <param name="windowSeconds">重启计数窗口（旧版 guard.py:56 <c>window=60.0</c>）。</param>
    public ProcessGuard(
        Func<string, Task> onLost,
        Action<string>? log = null,
        double intervalSeconds = 3,
        double minRestartGapSeconds = 10,
        int maxRestarts = 3,
        double windowSeconds = 60)
    {
        _onLost = onLost;
        _log = log;
        _intervalSeconds = intervalSeconds;
        _minRestartGapSeconds = minRestartGapSeconds;
        _maxRestarts = maxRestarts;
        _windowSeconds = windowSeconds;
    }

    /// <summary>守护是否处于开启状态（重启过于频繁会被自动关闭）。</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
    }

    /// <summary>启动守护线程。</summary>
    public void Start()
    {
        lock (_lock)
        {
            _loopTask ??= Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    /// <summary>更新要监控的进程名列表。</summary>
    public void SetWatch(IEnumerable<string>? exeNames)
    {
        lock (_lock) _watch = (exeNames ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
    }

    /// <summary>开关守护；重新开启时清空重启计数与防抖时间。</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;

            if (!enabled) return;

            _restarts.Clear();
            _lastRestart = 0;
        }
    }

    /// <summary>停止守护线程。</summary>
    public async Task StopAsync()
    {
        try { await _cts.CancelAsync().ConfigureAwait(false); }
        catch { /* 已释放 */ }

        var loop = _loopTask;
        _loopTask = null;

        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 线程退出即可 */ }
        }
    }

    /// <summary>
    /// 用 tasklist 判断进程是否存在（对应旧版 guard.py:32 <c>process_alive</c>）。
    /// 注意：查询失败时返回 true（保守处理，避免查询异常导致误重启）。
    /// </summary>
    public static async Task<bool> ProcessAliveAsync(string exeName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return true;
        if (string.IsNullOrWhiteSpace(exeName)) return true;

        var result = await ProcessRunner
            .RunAsync("tasklist", $"/FI \"IMAGENAME eq {exeName}\" /NH", TimeSpan.FromSeconds(8), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started) return true;

        return result.Output.Contains(exeName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            List<string> watch;
            bool enabled;

            lock (_lock)
            {
                watch = _watch.ToList();
                enabled = _enabled;
            }

            if (!enabled) continue;

            foreach (var name in watch)
            {
                if (cancellationToken.IsCancellationRequested) break;

                bool alive;

                try { alive = await ProcessAliveAsync(name, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                if (alive) continue;

                await OnLostAsync(name).ConfigureAwait(false);
            }
        }
    }

    private async Task OnLostAsync(string name)
    {
        lock (_lock)
        {
            var now = Now();

            if (now - _lastRestart < _minRestartGapSeconds) return;

            _restarts.RemoveAll(time => now - time >= _windowSeconds);

            if (_restarts.Count >= _maxRestarts)
            {
                _enabled = false;
                LogLine($"进程 {name} 频繁崩溃, 已停止自动重启");
                return;
            }

            _lastRestart = now;
            _restarts.Add(now);
        }

        LogLine($"检测到 {name} 异常退出, 正在自动重启...");

        try
        {
            await _onLost(name).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogLine($"自动重启失败: {ex.Message}");
        }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private void LogLine(string message)
    {
        Log.Info($"[守护] {message}");

        try { _log?.Invoke($"[守护] {message}"); }
        catch { /* 日志回调异常不影响业务 */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
