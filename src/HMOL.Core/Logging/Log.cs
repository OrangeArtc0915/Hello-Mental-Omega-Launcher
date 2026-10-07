using System.IO;
using System.Text;
using System.Threading.Channels;
using HMOL.Core.Localization;

namespace HMOL.Core.Logging;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>
/// 通道 + 批量落盘的日志写入器。
/// 批量阈值 198 行 / 325ms，避免高频写入拖慢调用方。
/// </summary>
public sealed class Logger : IAsyncDisposable
{
    private const int MaxBatchLines = 198;
    private const int WriteTimeoutMs = 325;

    private readonly Channel<string> _queue;
    private readonly string _storeFolder;
    private readonly long _maxFileSize;
    private readonly int _maxFileCount;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _fileLock = new();

    private string _currentFile;
    private int _droppedCount;

    public LogLevel MinLevel { get; set; } = LogLevel.Debug;

    public int DroppedCount => _droppedCount;

    public string CurrentFile
    {
        get { lock (_fileLock) return _currentFile; }
    }

    public Logger(string storeFolder, long maxFileSize, int maxFileCount, LogLevel minLevel)
    {
        _storeFolder = storeFolder;
        _maxFileSize = maxFileSize;
        _maxFileCount = maxFileCount;
        MinLevel = minLevel;

        Directory.CreateDirectory(storeFolder);
        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _currentFile = CreateFile();
        _worker = Task.Run(ProcessAsync);
    }

    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (level < MinLevel) return;

        var builder = new StringBuilder(128);
        builder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(']');
        builder.Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']');
        builder.Append(" [").Append(source).Append("] ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        if (!_queue.Writer.TryWrite(builder.ToString()))
            Interlocked.Increment(ref _droppedCount);
    }

    private async Task ProcessAsync()
    {
        var buffer = new List<string>(MaxBatchLines);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                buffer.Clear();
                var deadline = DateTime.UtcNow.AddMilliseconds(WriteTimeoutMs);

                while (buffer.Count < MaxBatchLines)
                {
                    if (_queue.Reader.TryRead(out var line))
                    {
                        buffer.Add(line);
                        continue;
                    }

                    if (DateTime.UtcNow >= deadline) break;
                    await Task.Delay(12, _cts.Token).ConfigureAwait(false);
                }

                if (buffer.Count > 0) Flush(buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { Flush(new List<string> { Loc.F("[FATAL] 日志写入线程异常：{0}", ex) }); } catch { }
        }
    }

    private void Flush(List<string> lines)
    {
        lock (_fileLock)
        {
            try
            {
                var info = new FileInfo(_currentFile);
                if (info.Exists && info.Length >= _maxFileSize) _currentFile = CreateFile();

                File.AppendAllLines(_currentFile, lines, Encoding.UTF8);
            }
            catch
            {
                // 落盘失败不抛出，避免影响业务线程
            }
        }
    }

    private string CreateFile()
    {
        var name = $"HMOL-{DateTime.Now:yyyy-M-d}-{DateTime.Now:HHmmssfff}.log";
        var path = Path.Combine(_storeFolder, name);
        CleanupOldFiles();
        return path;
    }

    private void CleanupOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(_storeFolder).GetFiles("*.log");
            if (files.Length <= _maxFileCount) return;

            foreach (var file in files.OrderBy(f => f.CreationTime).Take(files.Length - _maxFileCount))
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { }

        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>日志静态入口。调用点由 CallerFilePath 自动填充。</summary>
public static class Log
{
    private static Logger? _logger;
    private static string _module = "App";

    public static Logger? Current => _logger;

    public static void Init(Logger logger) => _logger = logger;

    /// <summary>设置后续日志的默认模块名，通常由页面在进入时调用。</summary>
    public static void SetModule(string module) => _module = module;

    public static void Debug(string message, [System.Runtime.CompilerServices.CallerFilePath] string? file = null)
        => Write(LogLevel.Debug, ModuleOf(file), message, null);

    public static void Info(string message, [System.Runtime.CompilerServices.CallerFilePath] string? file = null)
        => Write(LogLevel.Info, ModuleOf(file), message, null);

    public static void Warn(string message, [System.Runtime.CompilerServices.CallerFilePath] string? file = null)
        => Write(LogLevel.Warning, ModuleOf(file), message, null);

    public static void Error(string message, Exception? exception = null,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null)
        => Write(LogLevel.Error, ModuleOf(file), message, exception);

    public static void Fatal(string message, Exception? exception = null,
        [System.Runtime.CompilerServices.CallerFilePath] string? file = null)
        => Write(LogLevel.Fatal, ModuleOf(file), message, exception);

    /// <summary>
    /// 同时写两处：落盘的 <see cref="Logger"/> 与内存环形缓冲 <see cref="ActivityLog"/>。
    /// 后者供运行日志页实时显示，因此日志不再只活在磁盘上；日志文件不可用时也仍能在界面里看到。
    /// </summary>
    private static void Write(LogLevel level, string module, string message, Exception? exception)
    {
        _logger?.Write(level, module, message, exception);

        ActivityLog.Write(LogSource.App, Compose(module, message, exception), ActivityLevelOf(level));
    }

    /// <summary>内存日志的正文：带上模块名，异常只取类型与消息，避免整段调用栈铺满界面。</summary>
    private static string Compose(string module, string message, Exception? exception)
    {
        var text = string.IsNullOrEmpty(module) ? message : $"[{module}] {message}";
        return exception is null ? text : $"{text}：{exception.GetType().Name} {exception.Message}";
    }

    private static ActivityLevel ActivityLevelOf(LogLevel level) => level switch
    {
        LogLevel.Error or LogLevel.Fatal => ActivityLevel.Error,
        LogLevel.Warning => ActivityLevel.Warn,
        _ => ActivityLevel.Info
    };

    private static string ModuleOf(string? file)
    {
        if (string.IsNullOrEmpty(file)) return _module;
        var name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrEmpty(name) ? _module : name;
    }
}
