namespace HMOL.Core.Multiplayer;

/// <summary>
/// 组网引擎类型。范围已裁剪为两套：EasyTier 与 n2n。
/// 旧版 engine_zerotier.py / engine_awl.py 两个引擎已按要求删除，不在此列。
/// </summary>
public enum NetworkEngineKind
{
    EasyTier = 0,
    N2n = 1
}

/// <summary>虚拟 IP 的分配方式。对应旧版的 ip_mode（'auto' / 'manual'）。</summary>
public enum NetworkAddressMode
{
    Auto = 0,
    Manual = 1
}

/// <summary>在线对端。Latency / Status 为引擎能给出的附加信息，未知时为空串。</summary>
public sealed record NetworkPeer(string Name, string Ip, string Latency, string Status);

/// <summary>
/// 一次组网会话的入参。对应旧版 HMOL联机模块.py:1769 <c>_connect_worker</c> 里传给引擎构造函数的
/// community / key / node / ip_mode / manual_ip / is_owner（昵称另用）。
///
/// <para>
/// <see cref="ManualIp"/> 只在 <see cref="AddressMode"/> 为 <see cref="NetworkAddressMode.Manual"/> 时生效；
/// 传 null 或空串表示「用配置里的 ManualIp」，与旧版界面把表单值直接交给引擎等价。
/// </para>
/// </summary>
public sealed record NetworkSessionOptions(
    string RoomName,
    string RoomKey,
    string Node,
    string Nickname,
    bool IsOwner,
    NetworkAddressMode AddressMode,
    string? ManualIp);

/// <summary>引擎启动结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record EngineStartResult(bool Ok, string Message, string? LocalIp);

/// <summary>本节点信息。对应旧版 engine_easytier.py:344 <c>node_info()</c> 返回的字典。</summary>
public sealed record EngineNodeInfo(string? Ip, string? PeerId, string? PublicIpv4, string? NatType);

/// <summary>
/// 组网引擎的统一契约（下一轮界面按它写）。
/// 对应旧版 engine_easytier.py:160 <c>EasyTierEngine</c> 与 engine_n2n.py:98 <c>N2nEngine</c> 的公开接口。
///
/// <para>
/// 除冻结契约本身的成员外，另有两个附加成员（不影响契约，界面可用可不用）：
/// <see cref="OutputReceived"/> 供界面实时刷引擎日志，
/// <see cref="NodeInfoAsync"/> 对应旧版 <c>node_info()</c>（虚拟IP/PeerID/公网IP/NAT类型）。
/// </para>
/// </summary>
public interface INetworkEngine : IAsyncDisposable
{
    /// <summary>引擎类型。</summary>
    NetworkEngineKind Kind { get; }

    /// <summary>展示名（界面直接用）。</summary>
    string DisplayName { get; }

    /// <summary>引擎进程是否存活。</summary>
    bool IsRunning { get; }

    /// <summary>已获得的虚拟 IP；未就绪时为 null。</summary>
    string? LocalIp { get; }

    /// <summary>
    /// 引擎最近输出（最后 40 行拼接，行间为 <see cref="Environment.NewLine"/>），
    /// 供连接失败时诊断展示（旧版 <c>recent_output(20)</c> 的用途）。
    /// </summary>
    string RecentOutput { get; }

    /// <summary>引擎每输出一行触发一次。回调可能来自后台线程，界面需自行切回 UI 线程。</summary>
    event Action<string>? OutputReceived;

    /// <summary>启动引擎并等待虚拟 IP 就绪。<paramref name="progress"/> 用于回报等待进度。</summary>
    Task<EngineStartResult> StartAsync(NetworkSessionOptions options, IProgress<string>? progress, CancellationToken token);

    /// <summary>停止引擎并回收进程。</summary>
    Task StopAsync();

    /// <summary>查询在线对端（已排除本机）。</summary>
    Task<IReadOnlyList<NetworkPeer>> QueryPeersAsync(CancellationToken token);

    /// <summary>查询本节点信息；引擎未运行或查询失败时返回空记录。</summary>
    Task<EngineNodeInfo> NodeInfoAsync(CancellationToken token);
}
