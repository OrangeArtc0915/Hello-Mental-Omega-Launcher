using System.IO;
using System.Windows;
using HMOL.App.Controls;
using HMOL.App.Windows;
using HMOL.Core.Instances;
using HMOL.Core.Launch;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

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
        => TryLaunchExecutable(instance, instance.Executable, null, out error);

    /// <summary>
    /// 启动游戏。给实例配了 2 个以上版本时，先让用户选这次启动哪个版本；
    /// 只有一个版本（或没配版本）时与 <see cref="TryLaunch"/> 完全等价。
    /// 返回 false 表示这次没启动（用户取消了选择，或启动失败），失败原因在 <paramref name="error"/>。
    /// </summary>
    public static bool TryLaunchPicked(Window? owner, GameInstance instance, out string? error)
    {
        error = null;

        if (!instance.HasMultipleVersions) return TryLaunch(instance, out error);

        // 按钮文字用版本名（用户起的名字），细节里补上各自的可执行文件名
        var options = instance.Versions
            .Select(item => new ChoiceOption(item.DisplayName, item.Executable))
            .Append(new ChoiceOption(Loc.T("取消"), Cancel))
            .ToArray();

        var detail = Loc.F("这个实例配了 {0} 个版本，请选择这次启动哪个：\n{1}",
            instance.Versions.Count,
            string.Join("\n", instance.Versions.Select(item =>
                Loc.F("  · {0} → {1}", item.DisplayName, Path.GetFileName(item.Executable)))));

        var picked = ChoiceWindow.Ask(owner, Loc.T("选择要启动的版本"),
            Loc.F("「{0}」配了多个版本，启动前先选一个。", instance.Name),
            detail, options);

        if (picked is null || picked == Cancel) return false;

        var version = instance.Versions.FirstOrDefault(item =>
            string.Equals(item.Executable, picked, StringComparison.OrdinalIgnoreCase));

        return version is not null && TryLaunchVersion(instance, version, out error);
    }

    /// <summary>取消按钮的标识值。</summary>
    private const string Cancel = "cancel";

    /// <summary>用指定版本启动（日志里会带上版本名，便于回看启动的是哪个版本）。</summary>
    private static bool TryLaunchVersion(GameInstance instance, InstanceVersion version, out string? error)
        => TryLaunchExecutable(instance, version.Executable, version.DisplayName, out error);

    /// <summary>真正的启动：<paramref name="executable"/> 是要跑的主程序（空表示按类型自动探测）。</summary>
    private static bool TryLaunchExecutable(GameInstance instance, string? executable, string? versionName, out string? error)
    {
        error = null;

        if (IsRunning)
        {
            error = Loc.T("游戏已经在运行中。");
            return false;
        }

        HmolSessionStamp.Write(instance, executable);

        if (!GameLauncher.TryLaunch(instance.GameDir, instance.Kind, executable, out var session, out error)
            || session is null) return false;

        Attach(session);
        RecordLaunch(instance, versionName);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>把启动时间写进实例并落盘。只写文件、不动内存列表、不触发 Changed，界面由调用方自行刷新。</summary>
    private static void RecordLaunch(GameInstance instance, string? versionName)
    {
        instance.LastLaunchedAt = DateTime.Now;

        if (!InstanceStore.Save(instance)) return;

        Log.Info(versionName is null
            ? Loc.F("已记录实例「{0}」的启动时间：{1:yyyy-MM-dd HH:mm:ss}", instance.Name, instance.LastLaunchedAt)
            : Loc.F("已记录实例「{0}」版本「{1}」的启动时间：{2:yyyy-MM-dd HH:mm:ss}", instance.Name, versionName, instance.LastLaunchedAt));
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
                code is null ? Loc.T("游戏进程已结束") : Loc.F("游戏进程已结束（退出码 {0}）", code));

            session.Dispose();
            StateChanged?.Invoke();
        };
    }
}
