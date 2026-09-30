using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>提权任务的结果。</summary>
public sealed record ElevatedTaskResult(bool Ok, string Message, string? Detail);

/// <summary>
/// 按需提权：主程序以 <c>asInvoker</c> 运行，只有真正需要管理员的操作（如安装 TAP 驱动）
/// 才用 <c>runas</c> 重新拉起自身 exe，并带参数 <c>--elevated &lt;任务名&gt; --result &lt;结果文件&gt;</c>；
/// 等高权限实例退出后读回结果文件，把「成功 / 失败原因」交回主实例。
///
/// <para>
/// 提权实例的命令行分支在 <c>src\HMOL.App\App.xaml.cs</c> 的 <c>OnStartup</c> 里已接线：
/// 命中时执行 <see cref="ElevatedTasks.RunAsync"/> 并按其返回值退出。
/// 本类不弹任何窗（UAC 提示由系统自己弹）。
/// </para>
///
/// <para>
/// 结果文件放 <see cref="Paths.Temp"/>，由调用方（本类）指定路径，用完即删。
/// </para>
/// </summary>
public static class ElevationHelper
{
    /// <summary>提权实例启动自身的超时；安装驱动可能较慢，给 3 分钟。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private static readonly Lazy<bool> Elevated = new(CheckElevated);

    /// <summary>当前进程是否已具备管理员权限。</summary>
    public static bool IsElevated => Elevated.Value;

    /// <summary>自身 exe 的路径（提权时用它重新拉起自己）。</summary>
    public static string SelfExecutablePath
    {
        get
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)) return Environment.ProcessPath;
            }
            catch
            {
                // 单文件发布下 ProcessPath 偶尔取不到，退化到主模块
            }

            try { return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    /// <summary>
    /// 以管理员身份执行一个提权任务，等待其退出并读回结果。
    /// 用户点了 UAC 的「否」时返回 <c>Ok=false</c> 与中文说明，不抛异常。
    /// </summary>
    public static async Task<ElevatedTaskResult> RunElevatedAsync(
        string task,
        IEnumerable<string>? extraArgs = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task)) return new ElevatedTaskResult(false, "提权任务名为空", null);

        var self = SelfExecutablePath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
            return new ElevatedTaskResult(false, "取不到自身程序路径，无法提权", null);

        Paths.Init();

        var resultFile = Path.Combine(
            Paths.Temp,
            $"elevated-{Sanitize(task)}-{Guid.NewGuid():N}.json");

        var arguments = new List<string> { ElevatedTasks.ElevatedOption, task };

        if (extraArgs is not null) arguments.AddRange(extraArgs);

        arguments.AddRange([ElevatedTasks.ResultOption, resultFile]);

        var startInfo = new ProcessStartInfo(self)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments.Select(Quote)),
            WorkingDirectory = RuntimeLocator.BaseDir
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new ElevatedTaskResult(false, "提权进程启动失败", null);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;

                return new ElevatedTaskResult(false, "提权任务超时未返回结果", null);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户在 UAC 弹窗里点了「否」
            return new ElevatedTaskResult(false, "已取消提权：用户拒绝了管理员权限请求", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"启动提权进程失败：{ex.Message}");
            return new ElevatedTaskResult(false, $"无法启动提权进程：{ex.Message}", null);
        }

        return ReadResult(resultFile);
    }

    private static ElevatedTaskResult ReadResult(string resultFile)
    {
        try
        {
            if (!File.Exists(resultFile))
                return new ElevatedTaskResult(false, "提权进程没有返回结果（可能被系统拦截或提前退出）", null);

            var payload = JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(resultFile));
            if (payload is null) return new ElevatedTaskResult(false, "提权结果文件内容损坏", null);

            return new ElevatedTaskResult(payload.Ok, payload.Message, payload.Task);
        }
        catch (Exception ex)
        {
            return new ElevatedTaskResult(false, $"读取提权结果失败：{ex.Message}", null);
        }
        finally
        {
            try { File.Delete(resultFile); }
            catch { /* 删不掉就留着，下次覆盖 */ }
        }
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Log.Warn($"判断管理员权限失败，按普通权限处理：{ex.Message}");
            return false;
        }
    }

    private static string Sanitize(string task)
        => new(task.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
