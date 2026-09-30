using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>开机自启在注册表里的实际状态。</summary>
public enum AutoStartStatus
{
    /// <summary>注册表里没有这条启动项。</summary>
    NotRegistered = 0,

    /// <summary>已注册，且指向当前程序。</summary>
    Enabled = 1,

    /// <summary>已注册，但指向的不是当前程序（通常是程序被挪了目录）。</summary>
    PointsToOtherExe = 2,

    /// <summary>读写注册表失败，<see cref="AutoStartState.Message"/> 里是中文原因。</summary>
    Failed = 3
}

/// <summary>开机自启的查询结果。Message 可以直接展示给用户。</summary>
public sealed record AutoStartState(AutoStartStatus Status, string Command, string Message)
{
    /// <summary>是否已经为「当前这个 exe」启用了自启；指向别的 exe 不算。</summary>
    public bool IsEnabled => Status == AutoStartStatus.Enabled;
}

/// <summary>开机自启的写操作结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record AutoStartResult(bool Ok, string Message);

/// <summary>
/// 开机自启：在 <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> 下写一条值，
/// 命令行形如 <c>"&lt;exe 全路径&gt;" --autostart</c>。
///
/// <para>
/// 移植自旧版 <c>HMOL联机模块/guard.py</c> 的 auto_start_command / auto_start_set / auto_start_enabled，
/// 但修掉旧版的两个毛病：<br/>
/// 1）旧版写了 <c>--autostart</c> 却没人解析，起来后会被「禁止独立启动」的分支挡掉，等于死代码；
///    新版由 <c>App.OnStartup</c> 解析该参数（见 <see cref="IsAutoStartLaunch"/>），写了就一定有人认。<br/>
/// 2）旧版只判断注册表里有没有值，不判断指向的是不是自己；新版会比对 exe 路径。
/// </para>
///
/// <para>
/// 只动 HKCU，不写 HKLM，因此不需要管理员权限。所有失败都转成中文原因返回，不向界面抛异常。
/// </para>
/// </summary>
public static class AutoStartManager
{
    /// <summary>注册表启动项所在位置（相对 HKEY_CURRENT_USER）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>注册表值名。</summary>
    public const string ValueName = "HMOL";

    /// <summary>自启命令行开关：程序识别到它就只驻留托盘、不弹主窗口。</summary>
    public const string AutoStartSwitch = "--autostart";

    private static readonly string? ExePath = ResolveExePath();

    /// <summary>当前程序 exe 的全路径；取不到时为 null。</summary>
    public static string? CurrentExePath => ExePath;

    /// <summary>要写进注册表的自启命令行；取不到自身路径时为空串。</summary>
    public static string CurrentCommand => ExePath is null ? string.Empty : $"\"{ExePath}\" {AutoStartSwitch}";

    /// <summary>命令行里是否带了 <c>--autostart</c>（即由开机自启拉起）。</summary>
    public static bool IsAutoStartLaunch(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, AutoStartSwitch, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>查询自启状态。以注册表实际内容为准，不看配置文件。</summary>
    public static AutoStartState Query()
    {
        if (ExePath is null)
        {
            return new AutoStartState(AutoStartStatus.Failed, string.Empty,
                "取不到程序自身路径，无法判断开机自启状态。");
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);

            if (key?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command))
                return new AutoStartState(AutoStartStatus.NotRegistered, string.Empty, "未设置开机自启。");

            var target = CommandExePath(command);

            return string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase)
                ? new AutoStartState(AutoStartStatus.Enabled, command, "已设置开机自启。")
                : new AutoStartState(AutoStartStatus.PointsToOtherExe, command,
                    string.IsNullOrWhiteSpace(target)
                        ? "注册表里的启动项内容无法解析，可能不是本程序写的。"
                        : $"注册表里的启动项指向 {target}，不是当前程序（程序可能被移动过）。");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取开机自启注册表项失败：{ex.Message}");
            return new AutoStartState(AutoStartStatus.Failed, string.Empty, "读取注册表失败，无法判断开机自启状态。");
        }
    }

    /// <summary>启用开机自启。已存在指向别处的旧值时会被改写为当前位置。</summary>
    public static AutoStartResult Enable()
    {
        if (ExePath is null) return new AutoStartResult(false, "取不到程序自身路径，无法设置开机自启。");

        var command = CurrentCommand;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null) return new AutoStartResult(false, "打不开注册表启动项，无法设置开机自启。");

            key.SetValue(ValueName, command, RegistryValueKind.String);

            Log.Info($"已启用开机自启：{command}");
            return new AutoStartResult(true, "已启用开机自启。");
        }
        catch (Exception ex)
        {
            Log.Warn($"写入开机自启注册表项失败：{ex.Message}");
            return new AutoStartResult(false, $"设置开机自启失败：{ex.Message}");
        }
    }

    /// <summary>禁用开机自启。本条值不存在时也算成功。</summary>
    public static AutoStartResult Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            // 键不存在 == 本来就没有启动项，直接算完成
            if (key is null) return new AutoStartResult(true, "已关闭开机自启。");

            key.DeleteValue(ValueName, throwOnMissingValue: false);

            Log.Info("已关闭开机自启");
            return new AutoStartResult(true, "已关闭开机自启。");
        }
        catch (Exception ex)
        {
            Log.Warn($"删除开机自启注册表项失败：{ex.Message}");
            return new AutoStartResult(false, $"关闭开机自启失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 启动时的自启项修复：注册表里那条值存在、且指向的是别的 exe（程序被挪过目录）时，改写为当前位置。
    /// 未注册或已经指向当前程序时一个字节都不动；改写失败只写日志，绝不影响启动。
    /// </summary>
    public static void RepairIfStale()
    {
        if (ExePath is null) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (key?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command)) return;

            var target = CommandExePath(command);

            // 已经指向当前程序：不动。内容解析不出路径时也不是我们写的，同样不动
            if (target.Length == 0 || string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase)) return;

            var repaired = CurrentCommand;
            key.SetValue(ValueName, repaired, RegistryValueKind.String);

            Log.Info($"开机自启项指向 {target} 已失效，已自动改写为当前位置：{repaired}");
        }
        catch (Exception ex)
        {
            Log.Warn($"自动修复开机自启注册表项失败：{ex.Message}");
        }
    }

    /// <summary>从自启命令行里取出 exe 路径，兼容「带引号」与「不带引号」两种写法。</summary>
    private static string CommandExePath(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return string.Empty;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end].Trim() : string.Empty;
        }

        var space = text.IndexOf(' ');
        return (space > 0 ? text[..space] : text).Trim();
    }

    /// <summary>
    /// 解析自身 exe 路径。单文件发布时 <see cref="AppContext.BaseDirectory"/> 指向自解压目录，
    /// 所以优先用 <see cref="Environment.ProcessPath"/>（与 <see cref="RuntimeLocator"/> 同样的取法）。
    /// </summary>
    private static string? ResolveExePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"取自身 exe 路径失败：{ex.Message}");
        }

        try
        {
            var module = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(module)) return Path.GetFullPath(module);
        }
        catch (Exception ex)
        {
            Log.Warn($"从主模块取自身 exe 路径失败：{ex.Message}");
        }

        return null;
    }
}
