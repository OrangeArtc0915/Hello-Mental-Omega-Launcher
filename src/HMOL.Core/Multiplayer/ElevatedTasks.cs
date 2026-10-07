using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>提权实例写回的结果文件内容。</summary>
public sealed class ElevatedResult
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>任务名（如 <c>tap-install</c>）。</summary>
    public string Task { get; set; } = string.Empty;
}

/// <summary>解析出的提权请求。</summary>
public sealed record ElevatedRequest(string Task, string? ResultFile, IReadOnlyList<string> ExtraArgs);

/// <summary>
/// 高权限侧的任务执行入口。命令行形如
/// <c>HMOL.exe --elevated tap-install --result &lt;结果文件&gt;</c>。
///
/// <para>
/// <c>src\HMOL.App\App.xaml.cs</c> 的 <c>OnStartup</c> 已在最前面分流该命令行：
/// 命中 <see cref="TryParse"/> 时调用 <see cref="RunAsync"/> 真正执行被委托的任务，
/// 把结果写进结果文件后用退出码 0 / 1 结束（<c>Shutdown(code)</c>），主实例再读回结果。
/// 提权实例不占单实例互斥量、不建窗口与托盘，可与主实例并存。
/// </para>
/// </summary>
public static class ElevatedTasks
{
    /// <summary>命令行开关：任务名。</summary>
    public const string ElevatedOption = "--elevated";

    /// <summary>命令行开关：结果文件路径。</summary>
    public const string ResultOption = "--result";

    /// <summary>安装 TAP 虚拟网卡驱动。</summary>
    public const string TapInstall = "tap-install";

    /// <summary>关闭防火墙（全部配置文件）。</summary>
    public const string FirewallOff = "firewall-off";

    /// <summary>开启防火墙（全部配置文件）。</summary>
    public const string FirewallOn = "firewall-on";

    /// <summary>安装并启动 WinIPBroadcast 广播转发服务。</summary>
    public const string WinIpBroadcastInstall = "winipbroadcast-install";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>从命令行里识别提权请求；不是提权调用时返回 false。</summary>
    public static bool TryParse(IReadOnlyList<string>? args, out ElevatedRequest request)
    {
        request = new ElevatedRequest(string.Empty, null, []);

        if (args is null || args.Count == 0) return false;

        var task = ReadOption(args, ElevatedOption);
        if (string.IsNullOrWhiteSpace(task)) return false;

        request = new ElevatedRequest(task, ReadOption(args, ResultOption), []);
        return true;
    }

    /// <summary>执行提权任务并写回结果文件；返回值可直接当作进程退出码（0 成功 / 1 失败）。</summary>
    public static async Task<int> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
    {
        var status = await ExecuteAsync(request.Task, cancellationToken).ConfigureAwait(false);

        Log.Info(Loc.F("提权任务「{0}」执行结果：{1}", request.Task, status.Message));
        WriteResult(request.ResultFile, request.Task, status.Ok, status.Message);

        return status.Ok ? 0 : 1;
    }

    /// <summary>按任务名分派；未知任务返回可读的中文原因。</summary>
    public static Task<ToolkitStatus> ExecuteAsync(string task, CancellationToken cancellationToken = default)
        => task switch
        {
            TapInstall => NetworkToolkit.InstallTapDriverAsync(cancellationToken),
            FirewallOn => NetworkToolkit.SetFirewallAsync(true, cancellationToken),
            FirewallOff => NetworkToolkit.SetFirewallAsync(false, cancellationToken),
            WinIpBroadcastInstall => NetworkToolkit.EnsureWinIpBroadcastAsync(cancellationToken),
            _ => Task.FromResult(new ToolkitStatus(false, Loc.F("未知的提权任务：{0}", task)))
        };

    /// <summary>把结果写进结果文件，供主实例读回。</summary>
    public static void WriteResult(string? resultFile, string task, bool ok, string message)
    {
        if (string.IsNullOrWhiteSpace(resultFile)) return;

        try
        {
            var directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var payload = new ElevatedResult { Ok = ok, Message = message, Task = task };
            File.WriteAllText(resultFile, JsonSerializer.Serialize(payload, Options));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("写入提权结果文件失败：{0}", resultFile), ex);
        }
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
