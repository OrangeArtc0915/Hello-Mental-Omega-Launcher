using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.Localization;
using HMOL.Core.Logging;

namespace HMOL.Core.Diagnostics;

/// <summary>
/// 一键诊断包：把版本、系统信息、日志、设置快照与实例清单打成一个 zip，方便反馈问题时直接附上。
/// 不含游戏文件、插件包与联机配置（里面有个人昵称与好友列表）。
/// </summary>
public static class DiagnosticBundle
{
    /// <summary>默认文件名。</summary>
    public static string SuggestedFileName => $"HMOL_diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

    /// <summary>导出诊断包，返回（是否成功, 给用户看的说明）。</summary>
    public static (bool Ok, string Message) Export(string destination)
    {
        var staging = Path.Combine(Paths.Temp, $"diag_{Guid.NewGuid():N}");

        try
        {
            var root = Path.Combine(staging, "HMOL-diagnostics");
            Directory.CreateDirectory(root);

            var logCount = CopyLogs(root);
            var instanceCount = CopyInstances(root);

            File.WriteAllText(Path.Combine(root, "info.txt"), BuildInfo(instanceCount), new UTF8Encoding(true));

            var settings = Paths.SettingsFile;
            if (File.Exists(settings))
                File.Copy(settings, Path.Combine(root, "Settings.json"), overwrite: true);

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(destination)) File.Delete(destination);

            ZipFile.CreateFromDirectory(staging, destination, CompressionLevel.Optimal, includeBaseDirectory: false);

            return (true, Loc.F("诊断包已导出：{0}\n包含 {1} 份日志、{2} 个实例配置。", destination, logCount, instanceCount));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导出诊断包失败：{0}", destination), ex);
            return (false, Loc.F("导出诊断包失败：{0}", ex.Message));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // ————— 采集 —————

    private static int CopyLogs(string root)
    {
        var count = 0;

        try
        {
            if (!Directory.Exists(Paths.Log)) return 0;

            var target = Path.Combine(root, "logs");
            Directory.CreateDirectory(target);

            foreach (var file in new DirectoryInfo(Paths.Log).GetFiles("*.log"))
            {
                try
                {
                    file.CopyTo(Path.Combine(target, file.Name), overwrite: true);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn(Loc.F("诊断包：日志文件读取失败 {0}（{1}）", file.Name, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("诊断包：读取日志目录失败（{0}）", ex.Message));
        }

        return count;
    }

    private static int CopyInstances(string root)
    {
        var count = 0;

        try
        {
            if (!Directory.Exists(Paths.Instances)) return 0;

            var target = Path.Combine(root, "instances");
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(Paths.Instances, "*.json"))
            {
                try
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn(Loc.F("诊断包：实例文件读取失败 {0}（{1}）", Path.GetFileName(file), ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("诊断包：读取实例目录失败（{0}）", ex.Message));
        }

        return count;
    }

    private static string BuildInfo(int instanceCount)
    {
        var builder = new StringBuilder();

        builder.AppendLine("HMOL 诊断信息");
        builder.AppendLine(new string('-', 40));
        builder.AppendLine($"启动器：{AppInfo.Name} {AppInfo.VersionDisplay}");
        builder.AppendLine($"作者：{AppInfo.Author}");
        builder.AppendLine($"许可：{AppInfo.License}");
        builder.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"操作系统：{RuntimeInformation.OSDescription}");
        builder.AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"运行时：.NET {Environment.Version}");
        builder.AppendLine($"管理员权限：{(IsElevated() ? "是" : "否")}");
        builder.AppendLine($"界面语言：{Loc.Current}");
        builder.AppendLine($"数据目录：{Paths.Data}");
        builder.AppendLine($"日志目录：{Paths.Log}");
        builder.AppendLine();

        builder.AppendLine($"实例数量：{instanceCount}");
        foreach (var instance in InstanceStore.All)
        {
            var state = Directory.Exists(instance.GameDir) ? "目录可用" : "目录不可用";
            var last = instance.LastLaunchedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "从未启动";
            builder.AppendLine($"  - {instance.Name} | {instance.GameDir} | {state} | 上次启动 {last}");
        }

        builder.AppendLine();
        builder.AppendLine("说明：联机配置（multiplayer.json）含个人昵称与好友列表，未包含在本诊断包中。");

        return builder.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响结果
        }
    }
}
