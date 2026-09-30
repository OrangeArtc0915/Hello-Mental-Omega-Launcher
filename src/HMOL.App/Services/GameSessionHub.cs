using HMOL.Core.Instances;
using HMOL.Core.Launch;
using HMOL.Core.Logging;

namespace HMOL.App.Services;

/// <summary>
/// 游戏进程会话的共享入口。主页与实例页都能发起启动，若各自持有会话就会出现两个「运行中」状态，
/// 因此统一放在这里：同一时刻只允许一个会话，日志转投运行日志页的游戏日志，退出后释放句柄。
/// </summary>
internal static class GameSessionHub
{
    private static GameProcessSession? _session;

    /// <summary>游戏是否正在运行。</summary>
    public static bool IsRunning => _session is { IsRunning: true };

    /// <summary>会话状态变化（启动成功 / 已退出）。可能在后台线程触发，订阅方自行切回 UI 线程。</summary>
    public static event Action? StateChanged;

    /// <summary>
    /// 启动指定实例的游戏。失败时通过 error 给出可读原因。
    /// 启动成功会把「上次启动时间」写进实例配置并落盘（<see cref="InstanceStore.Save"/>），重启程序后仍保留。
    /// </summary>
    public static bool TryLaunch(GameInstance instance, out string? error)
    {
        error = null;

        if (IsRunning)
        {
            error = "游戏已经在运行中。";
            return false;
        }

        if (!GameLauncher.TryLaunch(instance.GameDir, instance.Kind, instance.Executable, out var session, out error)
            || session is null) return false;

        Attach(session);
        RecordLaunch(instance);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>把启动时间写进实例并落盘。只写文件、不动内存列表、不触发 Changed，界面由调用方自行刷新。</summary>
    private static void RecordLaunch(GameInstance instance)
    {
        instance.LastLaunchedAt = DateTime.Now;

        if (InstanceStore.Save(instance))
            Log.Info($"已记录实例「{instance.Name}」的启动时间：{instance.LastLaunchedAt:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>结束当前游戏进程（含子进程树）。进程退出后由会话自身收尾。</summary>
    public static void Kill() => _session?.Kill();

    private static void Attach(GameProcessSession session)
    {
        _session = session;

        session.LineReceived += line => ActivityLog.Write(LogSource.Game, line.Text, line.Level);

        session.Exited += code =>
        {
            // 先摘掉会话再通知界面，避免界面在退出事件里又读到旧会话
            if (ReferenceEquals(_session, session)) _session = null;

            ActivityLog.Write(LogSource.Game,
                code is null ? "游戏进程已结束" : $"游戏进程已结束（退出码 {code}）");

            session.Dispose();
            StateChanged?.Invoke();
        };
    }
}
