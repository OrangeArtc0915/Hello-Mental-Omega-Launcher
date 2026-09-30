namespace HMOL.Core.Security;

/// <summary>
/// 启动安全自检的结果。
/// <param name="Passed">strict=false 时恒为 true（只告警不阻断）；strict=true 时无 Issues 才为 true。</param>
/// <param name="Issues">需要告警的条目。</param>
/// <param name="Notes">说明性条目，例如"未配置校验文件，跳过比对"。</param>
/// </summary>
public sealed record SecurityReport(bool Passed, IReadOnlyList<string> Issues, IReadOnlyList<string> Notes)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// 启动安全自检汇总。对应旧版 <c>HMOL_QT/anti_debug.py</c> 的 <c>verify_runtime_integrity()</c>：
/// 调试器 + 可疑环境 + 自身完整性三块合成一份报告。
/// 只返回结果，不打日志、不弹窗，也不决定是否退出进程。
/// </summary>
public static class StartupSecurityCheck
{
    /// <param name="strict">true=有告警即视为未通过（旧版 strict 模式）；false=只告警不阻断（旧版启动时用的就是 false）。</param>
    public static SecurityReport Verify(bool strict = false)
    {
        var issues = new List<string>();
        var notes = new List<string>();

        // 1. 调试器
        foreach (var check in DebuggerDetector.Inspect())
        {
            if (check.Detected) issues.Add($"疑似被调试：{check.Method}（{check.Detail}）");
            else if (!check.Available) notes.Add($"{check.Method} 无法判定：{check.Detail}");
        }

        // 2. 可疑运行环境
        var suspicions = SandboxDetector.FindSuspicions();
        if (suspicions.Count > 0) issues.Add($"可疑运行环境：{string.Join("；", suspicions)}");

        // 3. 自身完整性。旧版校验的是 HMOL_qt.py / crypto_utils.py 两个源文件，
        //    单文件 exe 没有对应的源文件，这里校验程序本体。
        string[] targets = IntegrityChecker.SelfPath is { } self ? [self] : [];
        var tamper = IntegrityChecker.CheckTampering(targets);
        if (!tamper.Intact) issues.Add($"文件被改动：{string.Join("；", tamper.Problems)}");
        notes.AddRange(tamper.NotConfigured);

        return new SecurityReport(issues.Count == 0 || !strict, issues, notes);
    }
}
