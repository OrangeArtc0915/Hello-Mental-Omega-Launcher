namespace HMOL.Core.Security;

/// <summary>
/// 可疑运行环境（沙箱 / 虚拟机 / 分析机）检测。
/// 移植自旧版 <c>HMOL_QT/anti_debug.py</c> 的 <c>check_suspicious_environment()</c>。
/// 命中只代表"看起来像分析环境"，不阻断启动；只返回说明，不打日志、不弹窗。
/// </summary>
public static class SandboxDetector
{
    /// <summary>已知沙箱用户名，取值与旧版 sandbox_users 一致。</summary>
    private static readonly HashSet<string> SandboxUsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "sandbox", "currentuser", "maltest", "malware", "sample",
        "virus", "analyst", "vmware", "vagrant", "john"
    };

    /// <summary>常见沙箱环境变量名，取值与旧版 sandbox_envs 一致。</summary>
    private static readonly string[] SandboxEnvVars =
        ["SANDBOX", "VBOX", "VMWARE_TOOLS", "VIRTUALBOX", "ANALYSIS_ENVIRONMENT", "MALWARE_ANALYSIS"];

    /// <summary>开机不足 10 秒视为可疑（沙箱典型特征），阈值与旧版一致。</summary>
    private const long ShortUptimeMs = 10_000;

    /// <summary>返回命中的可疑特征，空列表表示没发现。</summary>
    public static IReadOnlyList<string> FindSuspicions()
    {
        var suspicions = new List<string>();

        // 1. 已知沙箱用户名
        var user = Environment.GetEnvironmentVariable("USERNAME")
                   ?? Environment.GetEnvironmentVariable("USER");
        if (!string.IsNullOrEmpty(user) && SandboxUsers.Contains(user))
            suspicions.Add($"沙箱用户名：{user}");

        // 2. 常见沙箱环境变量
        foreach (var name in SandboxEnvVars)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                suspicions.Add($"沙箱环境变量：{name}");
        }

        // 3. 开机时间过短。旧版用的是 time.CLOCK_UPTIME_RAW / CLOCK_UPTIME / CLOCK_BOOTTIME，
        //    这几个常量只存在于 Linux/macOS，在 Windows 上恒抛 AttributeError，该判断实际从未生效；
        //    这里用 Windows 上等价的 GetTickCount64（BCL 封装为 Environment.TickCount64）补上。
        var uptimeMs = Environment.TickCount64;
        if (uptimeMs < ShortUptimeMs)
            suspicions.Add($"系统刚启动（{uptimeMs / 1000.0:F1} 秒）");

        // 4. 异常 CPU 数（虚拟机常见，旧版条件为 0 < cpu_count <= 1）。
        //    旧版依赖 psutil，没装就静默跳过；这里用 BCL 的等价实现。
        var cpuCount = Environment.ProcessorCount;
        if (cpuCount <= 1)
            suspicions.Add($"异常 CPU 数：{cpuCount}");

        return suspicions;
    }
}
