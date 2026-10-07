using System.Diagnostics;
using System.IO;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 随包分发的组网组件（<c>runtime\</c>）的定位。
///
/// <para>
/// <b>关键点：</b>程序以「自包含单文件 exe」发布时，<see cref="AppContext.BaseDirectory"/> 指向的是
/// 运行时解压目录（形如 <c>%TEMP%\.net\HMOL\xxxx</c>），<b>不是</b> exe 所在目录。
/// 用它去找 exe 旁边的 runtime 必然失败，所以这里优先用 <see cref="Environment.ProcessPath"/>
/// （.NET 6+ 在单文件下返回真实 exe 路径），退化用
/// <c>Process.GetCurrentProcess().MainModule.FileName</c>，最后才回退 BaseDirectory。
/// </para>
///
/// <para>
/// runtime 目录内容为发行时随包分发的第三方二进制（easytier / n2n / tap / winipbroadcast），
/// 本程序只调用它们，不改写、不反编译。
/// </para>
/// </summary>
public static class RuntimeLocator
{
    /// <summary>runtime 目录整体缺失时给用户看的提示（属性而非常量：随界面语言变化）。</summary>
    public static string MissingRuntimeMessage => Loc.T("缺少组网组件，请到左侧「下载」页下载");

    private static readonly string Root = ResolveBaseDirectory();

    /// <summary>exe 所在目录（已处理单文件发布）。</summary>
    public static string BaseDir => Root;

    /// <summary><c>&lt;exe目录&gt;\runtime</c>。</summary>
    public static string RuntimeRoot => Path.Combine(Root, "runtime");

    public static string EasyTierDir => Path.Combine(RuntimeRoot, "easytier");

    public static string N2nDir => Path.Combine(RuntimeRoot, "n2n");

    public static string TapDir => Path.Combine(RuntimeRoot, "tap");

    public static string WinIpBroadcastDir => Path.Combine(RuntimeRoot, "winipbroadcast");

    public static string EasyTierCoreExe => Path.Combine(EasyTierDir, "easytier-core.exe");

    public static string EasyTierCliExe => Path.Combine(EasyTierDir, "easytier-cli.exe");

    public static string N2nEdgeExe => Path.Combine(N2nDir, "edge.exe");

    public static string N2nSupernodeExe => Path.Combine(N2nDir, "supernode.exe");

    public static string TapInstallExe => Path.Combine(TapDir, "tapinstall.exe");

    /// <summary>TAP 驱动安装信息文件。tapinstall 需要它做驱动绑定。</summary>
    public static string TapInfFile => Path.Combine(TapDir, "OemVista.inf");

    public static string WinIpBroadcastExe => Path.Combine(WinIpBroadcastDir, "WinIPBroadcast-1.6.exe");

    /// <summary>runtime 目录是否存在。</summary>
    public static bool IsRuntimePresent => Directory.Exists(RuntimeRoot);

    /// <summary>文件（含路径）是否存在。</summary>
    public static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>拼一段缺少组件的中文原因。</summary>
    public static string MissingMessage(string component)
        => IsRuntimePresent ? Loc.F("{0}（缺少 {1}）", MissingRuntimeMessage, component) : MissingRuntimeMessage;

    /// <summary>校验组件是否存在，缺失时返回中文原因，齐备时返回 null。</summary>
    public static string? Verify(params (string Path, string Name)[] required)
    {
        if (!IsRuntimePresent) return MissingRuntimeMessage;

        foreach (var (path, name) in required)
        {
            if (!File.Exists(path)) return MissingMessage(name);
        }

        return null;
    }

    /// <summary>
    /// 解析 exe 所在目录：Environment.ProcessPath → MainModule.FileName → AppContext.BaseDirectory。
    /// </summary>
    private static string ResolveBaseDirectory()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) return directory;
            }
            catch
            {
                // 取不到就换下一个来源，不影响程序启动
            }
        }

        return AppContext.BaseDirectory;

        static IEnumerable<string> Candidates()
        {
            yield return Environment.ProcessPath ?? string.Empty;

            string? module = null;
            try { module = Process.GetCurrentProcess().MainModule?.FileName; }
            catch { /* 权限不足等情况下取不到，交给下一个来源 */ }

            yield return module ?? string.Empty;
        }
    }
}
