namespace HMOL.Core.Multiplayer;

/// <summary>
/// 按引擎类型创建实例，并提供守护需要的进程名。
/// 对应旧版 HMOL联机模块.py:1769 <c>_connect_worker</c> 里的引擎选择分支（已删除 ZeroTier / Anywherelan 分支）。
/// </summary>
public static class NetworkEngineFactory
{
    /// <summary>创建引擎实例。节点候选列表用 NodeCatalog 的内置 + 自定义节点。</summary>
    public static INetworkEngine Create(NetworkEngineKind kind, Action<string>? log = null)
        => kind switch
        {
            NetworkEngineKind.N2n => new N2nEngine(NodeCatalog.N2nNodeValues(), log),
            _ => new EasyTierEngine(NodeCatalog.EasyTierNodeValues(), log: log)
        };

    /// <summary>界面上显示的方案名。</summary>
    public static string DisplayName(NetworkEngineKind kind)
        => kind == NetworkEngineKind.N2n ? "n2n" : "EasyTier";

    /// <summary>该引擎需要被进程守护看住的可执行文件名。</summary>
    public static string WatchProcessName(NetworkEngineKind kind)
        => kind == NetworkEngineKind.N2n ? "edge.exe" : "easytier-core.exe";

    /// <summary>把旧的方案字符串（旧版配置里的 plan）解析成引擎类型；无法识别时按 EasyTier 处理。</summary>
    public static NetworkEngineKind ParseKind(string? plan)
        => string.Equals(plan?.Trim(), "n2n", StringComparison.OrdinalIgnoreCase)
            ? NetworkEngineKind.N2n
            : NetworkEngineKind.EasyTier;

    /// <summary>序列化成旧版使用的方案字符串：easytier / n2n（大厅邀请与分享文本用）。</summary>
    public static string ToPlan(NetworkEngineKind kind)
        => kind == NetworkEngineKind.N2n ? "n2n" : "easytier";

    /// <summary>
    /// 解析本次会话实际要用的手工虚拟 IP：
    /// <see cref="NetworkSessionOptions.ManualIp"/> 优先 → 联机配置的 ManualIp → 旧版默认 192.168.100.66。
    /// 对应旧版界面把 manual_ip 表单值直接交给引擎构造函数的行为（engine_easytier.py:219 / engine_n2n.py:137）。
    /// </summary>
    public static string ResolveManualIp(NetworkSessionOptions? options)
    {
        var fromOptions = options?.ManualIp;

        if (!string.IsNullOrWhiteSpace(fromOptions)) return fromOptions.Trim();

        var configured = MultiplayerSettingsStore.Current.ManualIp;
        return string.IsNullOrWhiteSpace(configured)
            ? $"{NodeCatalog.N2nDefaultSubnet}.66"
            : configured.Trim();
    }
}
