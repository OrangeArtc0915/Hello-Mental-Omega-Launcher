using System.Diagnostics;
using System.Runtime.InteropServices;
using HMOL.Core.Localization;

namespace HMOL.Core.Security;

/// <summary>单项调试器检测的结果。</summary>
/// <param name="Method">检测手段（含来源 API 名）。</param>
/// <param name="Detected">true 表示命中。</param>
/// <param name="Available">false 表示该手段在本机跑不起来（API 缺失 / 权限不足），此时不算命中。</param>
/// <param name="Detail">说明：命中时的取值，或未命中 / 无法判定的原因。</param>
public sealed record DebuggerCheck(string Method, bool Detected, bool Available, string Detail);

/// <summary>
/// 调试器检测。移植自旧版 <c>HMOL_QT/anti_debug.py</c> 的 <c>is_debugger_present()</c>。
/// 只返回结果与说明，不打印日志、不弹窗，也不决定是否退出进程。
/// </summary>
public static class DebuggerDetector
{
    /// <summary>父进程名黑名单，取值与旧版 suspicious 列表一致。</summary>
    private static readonly string[] SuspiciousParents =
        ["devenv", "pycharm", "idea", "code", "x64dbg", "ollydbg"];

    private const int StatusSuccess = 0;

    /// <summary>STATUS_PORT_NOT_SET：进程上没有调试对象端口，即"没被调试"（ProcessDebugObjectHandle 的正常返回）。</summary>
    private const int StatusPortNotSet = unchecked((int)0xC0000353);

    private const int ClassBasicInformation = 0;
    private const int ClassDebugPort = 7;
    private const int ClassDebugObjectHandle = 0x1E;
    private const int ClassDebugFlags = 0x1F;

    // ————— kernel32.dll（来源：Windows SDK 的 debugapi.h / processthreadsapi.h / handleapi.h） —————

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess,
        [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // ————— ntdll.dll（未公开 API。签名来源：Process Hacker 的 phnt/ntexapi.h，用法参考 al-khaser） —————

    /// <summary>查询 ProcessDebugPort / ProcessDebugObjectHandle / ProcessDebugFlags 这类定长数值信息。</summary>
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        [Out] byte[] processInformation, int processInformationLength, out int returnLength);

    /// <summary>查询 ProcessBasicInformation（父进程 PID 在其中）。</summary>
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryProcessBasicInformation(IntPtr processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    /// <summary>PROCESS_BASIC_INFORMATION，字段顺序见 phnt 的 ntexapi.h（x64 下为 6 个指针）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>逐项跑一遍检测手段，返回每项的结果与说明。</summary>
    public static IReadOnlyList<DebuggerCheck> Inspect() =>
    [
        CheckIsDebuggerPresent(),
        CheckRemoteDebugger(),
        CheckDebugPort(),
        CheckDebugObjectHandle(),
        CheckDebugFlags(),
        CheckParentProcess()
    ];

    /// <summary>任意一项命中即认为有调试器。对应旧版 is_debugger_present() 的布尔结果。</summary>
    public static bool IsPresent()
    {
        foreach (var check in Inspect())
        {
            if (check.Detected) return true;
        }

        return false;
    }

    private static DebuggerCheck CheckIsDebuggerPresent()
    {
        const string method = "kernel32!IsDebuggerPresent";
        try
        {
            // 读 PEB.BeingDebugged：调试器附加后为 1
            return IsDebuggerPresent()
                ? new DebuggerCheck(method, true, true, Loc.T("当前进程已被调试"))
                : new DebuggerCheck(method, false, true, Loc.T("未命中"));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck CheckRemoteDebugger()
    {
        const string method = "kernel32!CheckRemoteDebuggerPresent";
        try
        {
            var ok = CheckRemoteDebuggerPresent(GetCurrentProcess(), out var flag);
            if (!ok) return new DebuggerCheck(method, false, false, Loc.T("调用失败"));

            return flag
                ? new DebuggerCheck(method, true, true, Loc.T("已被远程 / 内核调试器附加"))
                : new DebuggerCheck(method, false, true, Loc.T("未命中"));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck CheckDebugPort()
    {
        const string method = "ntdll!NtQueryInformationProcess(ProcessDebugPort)";
        try
        {
            var buffer = new byte[IntPtr.Size];
            var status = NtQueryInformationProcess(GetCurrentProcess(), ClassDebugPort, buffer, buffer.Length, out _);
            if (status != StatusSuccess) return new DebuggerCheck(method, false, false, $"NTSTATUS 0x{status:X8}");

            var value = IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 0) : BitConverter.ToInt32(buffer, 0);
            return value != 0
                ? new DebuggerCheck(method, true, true, Loc.F("调试端口 = {0}", value))
                : new DebuggerCheck(method, false, true, Loc.T("未命中"));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck CheckDebugObjectHandle()
    {
        const string method = "ntdll!NtQueryInformationProcess(ProcessDebugObjectHandle)";
        try
        {
            var buffer = new byte[IntPtr.Size];
            var status = NtQueryInformationProcess(GetCurrentProcess(), ClassDebugObjectHandle, buffer,
                buffer.Length, out _);
            if (status == StatusPortNotSet) return new DebuggerCheck(method, false, true, Loc.T("未命中"));
            if (status != StatusSuccess) return new DebuggerCheck(method, false, false, $"NTSTATUS 0x{status:X8}");

            var handle = IntPtr.Size == 8 ? BitConverter.ToInt64(buffer, 0) : BitConverter.ToInt32(buffer, 0);
            if (handle == 0) return new DebuggerCheck(method, false, true, Loc.T("未命中"));

            // 命中的话内核会把一个真的调试对象句柄塞给我们，用完必须关掉
            CloseHandle(new IntPtr(handle));
            return new DebuggerCheck(method, true, true, Loc.T("存在调试对象句柄"));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck CheckDebugFlags()
    {
        const string method = "ntdll!NtQueryInformationProcess(ProcessDebugFlags)";
        try
        {
            var buffer = new byte[sizeof(int)];
            var status = NtQueryInformationProcess(GetCurrentProcess(), ClassDebugFlags, buffer, buffer.Length, out _);
            if (status != StatusSuccess) return new DebuggerCheck(method, false, false, $"NTSTATUS 0x{status:X8}");

            // 约定（见 al-khaser）：返回 0 表示正在被调试，非 0 表示干净
            var flags = BitConverter.ToInt32(buffer, 0);
            return flags == 0
                ? new DebuggerCheck(method, true, true, Loc.T("标志位为 0"))
                : new DebuggerCheck(method, false, true, Loc.F("标志位 = {0}", flags));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck CheckParentProcess()
    {
        const string method = "父进程名比对";
        try
        {
            var status = NtQueryProcessBasicInformation(GetCurrentProcess(), ClassBasicInformation,
                out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            if (status != StatusSuccess) return new DebuggerCheck(method, false, false, $"NTSTATUS 0x{status:X8}");

            var parentId = (int)info.InheritedFromUniqueProcessId.ToInt64();
            if (parentId <= 0) return new DebuggerCheck(method, false, false, Loc.T("取不到父进程 ID"));

            // 非管理员读不了别的用户的进程（父进程提权过就会 Access Denied），这时如实记为"无法判定"
            using var parent = Process.GetProcessById(parentId);
            var parentName = parent.ProcessName.ToLowerInvariant();
            foreach (var suspect in SuspiciousParents)
            {
                if (parentName.Contains(suspect, StringComparison.Ordinal))
                    return new DebuggerCheck(method, true, true, Loc.F("父进程 {0} 命中 {1}", parent.ProcessName, suspect));
            }

            return new DebuggerCheck(method, false, true, Loc.F("父进程 {0}", parent.ProcessName));
        }
        catch (Exception ex)
        {
            return Unavailable(method, ex);
        }
    }

    private static DebuggerCheck Unavailable(string method, Exception ex) => new(method, false, false, ex.Message);
}
