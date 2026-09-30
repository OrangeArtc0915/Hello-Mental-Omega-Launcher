namespace HMOL.Core.Logging;

/// <summary>日志来源：启动器自己的操作，还是游戏进程的输出。</summary>
public enum LogSource
{
    /// <summary>启动器自身的操作记录（联机、安装、下载等）。</summary>
    App,

    /// <summary>游戏进程的输出与启动器的启动/退出记录。</summary>
    Game
}

/// <summary>日志级别，只用于决定文字颜色。</summary>
public enum ActivityLevel
{
    Info,
    Warn,
    Error
}

/// <summary>一条运行日志。</summary>
public sealed record ActivityLine(DateTime Time, LogSource Source, ActivityLevel Level, string Text);

/// <summary>
/// 运行日志的内存环形缓冲。页面随时可以订阅，因此日志不再绑在某个页面上——
/// 切走页面、甚至页面被销毁重建，历史都还在（最多留最近 <see cref="Capacity"/> 条）。
///
/// 只放内存、不落盘：落盘那份由 <see cref="Log"/> 负责，两者用途不同。
/// </summary>
public static class ActivityLog
{
    /// <summary>缓冲上限。超出后丢弃最早的，避免长时间挂机把内存吃满。</summary>
    public const int Capacity = 500;

    private static readonly object Gate = new();
    private static readonly List<ActivityLine> Lines = [];

    /// <summary>有新日志时触发。可能来自后台线程，订阅方自己负责切回 UI 线程。</summary>
    public static event Action<ActivityLine>? Appended;

    /// <summary>写入一条日志。</summary>
    public static void Write(LogSource source, string text, ActivityLevel level = ActivityLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var line = new ActivityLine(DateTime.Now, source, level, text);

        lock (Gate)
        {
            Lines.Add(line);
            if (Lines.Count > Capacity) Lines.RemoveRange(0, Lines.Count - Capacity);
        }

        Appended?.Invoke(line);
    }

    /// <summary>取当前缓冲的快照，用于页面初次显示时补齐历史。</summary>
    public static IReadOnlyList<ActivityLine> Snapshot()
    {
        lock (Gate) return [.. Lines];
    }
}
