using System.Diagnostics;
using System.IO;
using HMOL.Core.Games;
using HMOL.Core.Logging;

namespace HMOL.Core.Launch;

/// <summary>
/// 游戏启动入口。按旧版 <c>_launch_game</c> 的顺序在实例目录里找主程序，
/// 以主程序所在目录为工作目录拉起进程，并捕获输出。
/// </summary>
public static class GameLauncher
{
    /// <summary>
    /// 启动游戏。失败时返回 false 并给出可读原因，不抛异常。
    /// <paramref name="executableOverride"/> 是用户手工指定的主程序（可空）：指定了就只认它，
    /// 没指定才按 <paramref name="kind"/> 的默认主程序名顺序探测。
    /// </summary>
    public static bool TryLaunch(string? gameDirectory, GameKind kind, string? executableOverride,
        out GameProcessSession? session, out string? error)
    {
        session = null;
        error = null;

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            error = $"游戏目录不存在：{gameDirectory}";
            return false;
        }

        string? executable;

        if (!string.IsNullOrWhiteSpace(executableOverride))
        {
            executable = GameLocator.ResolveExecutable(gameDirectory, executableOverride);
            if (executable is null)
            {
                error = $"指定的主程序不存在：{executableOverride}";
                return false;
            }
        }
        else
        {
            executable = GameLocator.FindExecutable(gameDirectory, kind);

            if (executable is null)
            {
                var names = GameLocator.DefaultExecutables(kind);
                error = names.Count == 0
                    ? $"该实例没有指定主程序，请在「编辑」里选择一个可执行文件：{gameDirectory}"
                    : $"未找到游戏主程序（{string.Join(" / ", names)}）：{gameDirectory}";
                return false;
            }
        }

        try
        {
            // TODO：未显式指定 StandardOutputEncoding。旧版用 Popen 不重定向输出，无法参考；
            // 若实际日志出现乱码，需要按系统控制台代码页（中文 Windows 通常是 GBK）再指定编码。
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? gameDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "进程启动失败：Process.Start 返回空";
                return false;
            }

            session = new GameProcessSession(process) { ExecutablePath = executable };
            Log.Info($"已启动游戏：{executable}");

            return true;
        }
        catch (Exception ex)
        {
            error = $"启动游戏失败：{ex.Message}";
            Log.Error($"启动游戏失败：{executable}", ex);
            return false;
        }
    }
}
