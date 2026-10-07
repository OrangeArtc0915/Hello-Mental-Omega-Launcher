using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// EasyTier 引擎（easytier-core 客户端进程管理）。对应旧版 engine_easytier.py。
///
/// 要点与旧版一致：TCP→UDP 自动回退、换节点重试、清理残留进程、RPC 端口动态分配、
/// 用 easytier-cli 解析 Virtual IP 与对端列表。
/// </summary>
public sealed class EasyTierEngine : EngineProcessBase, INetworkEngine
{
    /// <summary>RPC 端口分配失败时的兜底值（旧版常量）。</summary>
    private const int DefaultRpcPort = 15888;

    /// <summary>虚拟网卡 MTU。旧版固定 1500。</summary>
    private const int Mtu = 1500;

    /// <summary>
    /// 单个节点的就绪等待上限。旧版固定 45 秒——节点挂着也照等，
    /// 用户看到的就是"进度条不动"。现在超时就换下一个，最坏情况的总等待反而更短。
    /// </summary>
    private const double NodeReadySeconds = 20;

    /// <summary>一次连接最多换几个节点：够覆盖探测失准的情况，又不至于让用户干等太久。</summary>
    private const int MaxNodeAttempts = 3;

    /// <summary>连接前探测单个节点的超时。</summary>
    private const double ProbeTimeoutSeconds = 1.5;

    /// <summary>
    /// easytier WARN/ERROR 里的内部调试噪音（连接保活/路由同步/网卡探测），对用户无意义。
    /// </summary>
    private static readonly string[] LogNoise =
    [
        "proto::rpc_impl::bidirect",
        "peer rpc transport read aborted",
        "peers::peer_ospf_route",
        "session id mismatch",
        "connector::manual",
        "reconn_tasks done",
        "peers::foreign_network_client",
        "wintun::log",
        "close tcp connection",
        "bind addr fail"
    ];

    private static readonly Regex VirtualIpReadyPattern = new(@"Virtual IP\s*\|\s*([0-9.]+)", RegexOptions.Compiled);

    private static readonly Regex VirtualIpPattern = new(
        @"Virtual IP\s*\|\s*([0-9.]+/[0-9]+|[0-9.]+)", RegexOptions.Compiled);

    private static readonly Regex PeerIdPattern = new(@"Peer ID\s*\|\s*(\S+)", RegexOptions.Compiled);

    private static readonly Regex PublicIpv4Pattern = new(@"Public IPv4\s*\|\s*(\S+)", RegexOptions.Compiled);

    private static readonly Regex NatTypePattern = new(@"UDP Stun Type\s*\|\s*(\S+)", RegexOptions.Compiled);

    private static readonly Regex Ipv4CellPattern = new(@"^\d+\.\d+\.\d+\.\d+(?:/\d+)?$", RegexOptions.Compiled);

    private readonly List<string> _nodes;
    private readonly string _instanceName;
    private readonly string _devName;

    private NetworkSessionOptions _options =
        new(string.Empty, string.Empty, string.Empty, string.Empty, true, NetworkAddressMode.Auto, null);

    private int _nodeIndex;
    private int _rpcPort;

    /// <param name="nodes">节点候选列表（内置 + 自定义）；传 null 用 NodeCatalog 的默认列表。</param>
    /// <param name="instanceName">easytier 实例名，用于清理残留进程时区分本程序的实例。</param>
    /// <param name="devName">虚拟网卡名前缀。</param>
    /// <param name="log">日志回调。</param>
    public EasyTierEngine(
        IEnumerable<string>? nodes = null,
        string instanceName = "hmol",
        string devName = "et-hmol",
        Action<string>? log = null)
    {
        LogCallback = log;
        _instanceName = string.IsNullOrWhiteSpace(instanceName) ? "hmol" : instanceName;
        _devName = string.IsNullOrWhiteSpace(devName) ? "et-hmol" : devName;

        _nodes = (nodes ?? NodeCatalog.EasyTierNodeValues())
            .Where(node => !string.IsNullOrWhiteSpace(node))
            .Select(node => node.Trim())
            .ToList();

        if (_nodes.Count == 0) _nodes.Add(NodeCatalog.EasyTierPublicNodes[0].Address);
    }

    public NetworkEngineKind Kind => NetworkEngineKind.EasyTier;

    public string DisplayName => "EasyTier";

    /// <summary>房间名就是 EasyTier 的网络名，不同房间不在同一个网络里，对端一定是自己人。</summary>
    public bool NetworkIsolatesRoom => true;

    protected override string EnginePrefix => "easytier";

    /// <summary>把下拉框选中的节点排到候选列表最前面，保证首选就是用户选的那个。</summary>
    private void ReorderNodes(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return;

        var index = _nodes.IndexOf(preferred);
        _nodeIndex = index >= 0 ? index : 0;

        if (index < 0)
        {
            _nodes.Insert(0, preferred);
            _nodeIndex = 0;
        }
    }

    private string CurrentNode() => _nodes[_nodeIndex % _nodes.Count];

    /// <summary>
    /// 旧版 engine_easytier.py:266 <c>_fallback_udp</c>：
    /// 公共节点常只开放 UDP（TCP 11010 被防火墙拦），tcp 连不上时优先试同主机 udp。
    /// </summary>
    private static string? FallbackUdp(string node)
        => node.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? "udp://" + node[6..] : null;

    /// <summary>
    /// 连接前探一遍候选节点。分三档排序：测到延迟的（越小越靠前）、测不出来的
    /// （UDP-only 节点或 ICMP 被拦，不代表不可用）、明确连不上的排最后。
    /// <b>不丢任何节点</b>——探测只影响顺序，真连不上还有后面的换节点重试兜底；
    /// 用户显式选中的节点只要不是"明确连不上"，仍然排第一。
    /// </summary>
    private async Task ProbeNodesAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (_nodes.Count <= 1) return;

        var preferred = CurrentNode();
        var preferredIndex = _nodes.IndexOf(preferred);

        progress?.Report(Loc.F("正在探测 {0} 个节点的连通性...", _nodes.Count));

        var probes = await Task
            .WhenAll(_nodes.Select(node => ProbeOneAsync(node, cancellationToken)))
            .ConfigureAwait(false);

        var ranked = Enumerable.Range(0, _nodes.Count)
            .Select(i => (Index: i, Node: _nodes[i], Probe: probes[i]))
            .OrderBy(item => item.Probe switch { null => 1, < 0 => 2, _ => 0 })
            .ThenBy(item => item.Probe is > 0 ? item.Probe!.Value : int.MaxValue)
            .ThenBy(item => item.Index)
            .ToList();

        var ordered = ranked.Select(item => item.Node).ToList();

        if (preferredIndex >= 0 && probes[preferredIndex] is not < 0)
        {
            ordered.Remove(preferred);
            ordered.Insert(0, preferred);
        }

        _nodes.Clear();
        _nodes.AddRange(ordered);
        _nodeIndex = 0;

        LogLine(Loc.T("节点探测：") + string.Join("、", ranked.Select(item => item.Probe switch
        {
            null => $"{item.Node}=未测出",
            < 0 => $"{item.Node}=连不上",
            _ => $"{item.Node}={item.Probe}ms"
        })));
    }

    /// <summary>
    /// 探测单个节点：返回延迟毫秒；<c>null</c> 表示测不出来（UDP-only 或 ICMP 被拦，不代表不可用）；
    /// <c>-1</c> 表示明确连不上。
    /// </summary>
    private static async Task<int?> ProbeOneAsync(string node, CancellationToken cancellationToken)
    {
        var (host, port) = NodeCatalog.NodeHostPort(node);
        if (string.IsNullOrWhiteSpace(host)) return -1;

        // tcp:// 直接建连最准；没写协议或 udp:// 的节点用一次 ICMP，通不了只算"未测出"
        if (node.Contains("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var latency = await NetworkToolkit
                .TcpLatencyAsync(host, port, ProbeTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return latency ?? -1;
        }

        var ping = await NetworkToolkit
            .PingAsync(host, 1, ProbeTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        return ping.Ok > 0 ? (int)Math.Round(ping.AvgMs) : null;
    }

    public async Task<EngineStartResult> StartAsync(
        NetworkSessionOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var missing = RuntimeLocator.Verify(
            (RuntimeLocator.EasyTierCoreExe, "easytier-core.exe"),
            (RuntimeLocator.EasyTierCliExe, "easytier-cli.exe"));

        if (missing is not null) return Failure(missing);

        if (IsRunning) return Failure(Loc.F("{0} 引擎已在运行", DisplayName));

        if (string.IsNullOrWhiteSpace(options.RoomName)) return Failure(Loc.T("房间名（小组名称）不能为空"));

        _options = options;
        ReorderNodes(options.Node);
        LocalIp = null;

        try
        {
            // 旧实例残留会占住 RPC 端口与 TUN 网卡，先清理超过 2 分钟的本程序残留
            var cleaned = await CleanupStaleProcessesAsync(_instanceName, 120, cancellationToken).ConfigureAwait(false);
            if (cleaned > 0) LogLine(Loc.F("已清理 {0} 个残留 easytier 进程", cleaned));

            // 先探一遍节点，把明确连不上的排到后面（只排序、不丢节点）
            await ProbeNodesAsync(progress, cancellationToken).ConfigureAwait(false);

            // 依次尝试节点：单个节点超时就换下一个，最多换 MaxNodeAttempts 个
            var tried = 0;
            var portConflictRetried = false;

            while (true)
            {
                _rpcPort = FreeTcpPort();
                tried++;

                var result = await StartOnceAsync(progress, cancellationToken).ConfigureAwait(false);
                if (result.Ok) return result;

                // RPC 端口被残留进程占住：清理后换个端口、用同一个节点再试一次（不占节点次数）
                if (!portConflictRetried && PortConflictDetected())
                {
                    portConflictRetried = true;
                    tried--;

                    var again = await CleanupStaleProcessesAsync(_instanceName, 120, cancellationToken).ConfigureAwait(false);
                    LogLine(again > 0
                        ? Loc.F("检测到 {0} 个残留 easytier 进程，已清理后重试", again)
                        : Loc.T("检测到 RPC 端口被占用，更换端口后重试"));

                    await StopAsync().ConfigureAwait(false);
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (tried >= MaxNodeAttempts || _nodes.Count <= 1)
                {
                    await StopAsync().ConfigureAwait(false);
                    return tried >= MaxNodeAttempts && _nodes.Count > 1
                        ? result with { Message = Loc.F("{0}（已依次尝试 {1} 个节点）", result.Message, tried) }
                        : result;
                }

                var old = CurrentNode();
                var udp = FallbackUdp(old);

                // 先按当前长度算出插入位置，再插入并切到下一个节点（与旧版 next_node 顺序一致）
                if (udp is not null && !_nodes.Contains(udp)) _nodes.Insert((_nodeIndex + 1) % _nodes.Count, udp);

                _nodeIndex = (_nodeIndex + 1) % _nodes.Count;
                LogLine(Loc.F("节点 {0} 未在 {1:0} 秒内就绪，自动切换 {2} 重试（第 {3} 个节点）...", old, NodeReadySeconds, CurrentNode(), tried + 1));

                await StopAsync().ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            return Failure(Loc.T("启动已取消"));
        }
    }

    /// <summary>启动一次并等待虚拟 IP。</summary>
    private async Task<EngineStartResult> StartOnceAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var node = CurrentNode();
        var manual = NetworkEngineFactory.ResolveManualIp(_options);

        LogLine(Loc.F("启动 easytier-core：节点={0} 房间={1} ", node, _options.RoomName) +
                Loc.F("IP模式={0}", (_options.AddressMode == NetworkAddressMode.Manual ? manual : Loc.T("自动(DHCP)"))));

        var arguments = BuildArguments(node, manual);
        LogLine(arguments);

        Launch(
            RuntimeLocator.EasyTierCoreExe,
            arguments,
            RuntimeLocator.EasyTierDir,
            new Dictionary<string, string> { ["RUST_LOG"] = "warn" });

        var watch = Stopwatch.StartNew();
        var lastProgress = 0;

        while (watch.Elapsed < TimeSpan.FromSeconds(NodeReadySeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasExited(CurrentProcess)) return Failure(Loc.T("easytier-core 进程提前退出"));

            try
            {
                var info = await RunCliAsync("node info", TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

                if (info is { Started: true, ExitCode: 0 } && info.Output.Contains("Virtual IP", StringComparison.Ordinal))
                {
                    var match = VirtualIpReadyPattern.Match(info.Output);
                    if (match.Success)
                    {
                        LocalIp = match.Groups[1].Value;
                        await NetworkToolkit.SetInterfaceMetricAsync(LocalIp, 1, cancellationToken).ConfigureAwait(false);
                        LogLine(Loc.F("easytier-core 已就绪，虚拟IP={0}", LocalIp));
                        return new EngineStartResult(true, Loc.F("连接成功，虚拟 IP：{0}", LocalIp), LocalIp);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("查询 easytier 节点信息失败：{0}", ex.Message));
            }

            var elapsed = (int)watch.Elapsed.TotalSeconds;
            if (elapsed - lastProgress >= 15)
            {
                lastProgress = elapsed;
                progress?.Report(Loc.F("正在连接节点 {0}（已等待 {1}s）...", node, elapsed));
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return Failure(Loc.T("easytier-core 启动超时，未获得虚拟 IP"));
    }

    /// <summary>命令行参数与旧版 engine_easytier.py:214-233 逐条对齐。</summary>
    private string BuildArguments(string node, string manualIp)
    {
        var options = _options;

        var parts = new List<string>
        {
            "--network-name", Quote(options.RoomName)
        };

        if (!string.IsNullOrEmpty(options.RoomKey)) parts.AddRange(["--network-secret", Quote(options.RoomKey)]);

        parts.AddRange(["--peers", Quote(node)]);

        if (options.AddressMode == NetworkAddressMode.Manual && !string.IsNullOrWhiteSpace(manualIp))
        {
            var ip = manualIp.Contains('/') ? manualIp : $"{manualIp}/24";
            parts.AddRange(["--ipv4", Quote(ip)]);
        }
        else
        {
            parts.AddRange(["--dhcp", "true"]);
        }

        parts.AddRange(["--rpc-portal", $"127.0.0.1:{_rpcPort}"]);
        parts.AddRange(["--instance-name", _instanceName]);
        parts.AddRange(["--dev-name", _devName]);
        parts.AddRange(["--enable-udp-broadcast-relay", "true"]);
        parts.AddRange(["--latency-first", "true"]);
        parts.AddRange(["--mtu", Mtu.ToString()]);
        parts.AddRange(["--multi-thread", "true"]);
        parts.AddRange(["--multi-thread-count", "4"]);
        parts.AddRange(["--disable-relay-quic", "true"]);
        parts.AddRange(["--listeners", "wg:0"]);

        return string.Join(' ', parts);
    }

    /// <summary>按旧版规则判断启动失败是否因端口被占用。</summary>
    private bool PortConflictDetected()
    {
        foreach (var line in RecentOutputOf(300))
        {
            var low = line.ToLowerInvariant();
            if (low.Contains("failed to listen") || low.Contains("10048") || low.Contains("address already in use"))
                return true;
        }

        return false;
    }

    public async Task<IReadOnlyList<NetworkPeer>> QueryPeersAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return [];

        try
        {
            var result = await RunCliAsync("peer list", TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            var peers = ParsePeerTable(result.Output);
            var own = (LocalIp ?? string.Empty).Split('/')[0];

            return peers.Where(peer => peer.Ip != own && peer.Ip != LocalIp).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("查询 easytier 对端失败：{0}", ex.Message));
            return [];
        }
    }

    public async Task<EngineNodeInfo> NodeInfoAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return new EngineNodeInfo(null, null, null, null);

        try
        {
            var result = await RunCliAsync("node info", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var text = result.Output;

            return new EngineNodeInfo(
                Match(VirtualIpPattern, text),
                Match(PeerIdPattern, text),
                Match(PublicIpv4Pattern, text),
                Match(NatTypePattern, text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("查询 easytier 节点信息失败：{0}", ex.Message));
            return new EngineNodeInfo(null, null, null, null);
        }
    }

    /// <summary>
    /// 解析 <c>easytier-cli peer list</c> 的表格。
    /// easytier 2.6.4 实测表头：列索引 1..10 对应 ipv4 / hostname / cost / lat(ms) / loss / rx / tx / tunnel / NAT / version，
    /// 延迟列是纯数字（如 231.17，无 ms 后缀），空或 '-' 表示未知。
    /// </summary>
    public static IReadOnlyList<NetworkPeer> ParsePeerTable(string? text)
    {
        var peers = new List<NetworkPeer>();
        if (string.IsNullOrWhiteSpace(text)) return peers;

        foreach (var line in text.Split('\n'))
        {
            var cells = line.Split('|');
            if (cells.Length < 11) continue;

            for (var i = 0; i < cells.Length; i++) cells[i] = cells[i].Trim();

            var ipv4 = cells[1];
            var hostname = cells[2];

            if (hostname is "" or "hostname") continue;

            // 表头 / 分隔线 / 没有虚拟 IP 的公共节点行都过滤掉
            if (!Ipv4CellPattern.IsMatch(ipv4)) continue;
            if (cells[8] == "tunnel") continue;

            var latency = cells[4];
            if (latency is "" or "-")
            {
                latency = string.Empty;
            }
            else if (double.TryParse(latency, out var value))
            {
                latency = ((int)value).ToString();
            }

            peers.Add(new NetworkPeer(hostname, ipv4, latency, cells[8]));
        }

        return peers;
    }

    /// <summary>
    /// 清理超过 <paramref name="maxAgeSeconds"/> 秒的残留 easytier-core 进程（只清本程序实例，
    /// 按 <c>--instance-name</c> 过滤）。返回清理掉的进程数。对应旧版 cleanup_stale_easytier。
    /// </summary>
    public static async Task<int> CleanupStaleProcessesAsync(
        string instanceName = "hmol",
        int maxAgeSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        try
        {
            var script =
                "Get-CimInstance Win32_Process -Filter \"Name='easytier-core.exe'\" " +
                $"| Where-Object {{ $_.CommandLine -like '*--instance-name {instanceName}*' " +
                $"-and $_.CreationDate -lt (Get-Date).AddSeconds(-{maxAgeSeconds}) }} " +
                "| ForEach-Object { $_.ProcessId }";

            var output = await ProcessRunner
                .RunPowerShellTextAsync(script, TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);

            var killed = 0;

            foreach (var token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.All(char.IsDigit)) continue;

                var result = await ProcessRunner
                    .RunAsync("taskkill", $"/F /PID {token}", TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);

                if (result.Ok) killed++;
            }

            return killed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理残留 easytier 进程失败：{0}", ex.Message));
            return 0;
        }
    }

    private Task<ProcessRunner.Result> RunCliAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(
            RuntimeLocator.EasyTierCliExe,
            $"-p 127.0.0.1:{_rpcPort} {arguments}",
            timeout,
            cancellationToken,
            ProcessRunner.EngineEncoding);

    protected override void OnEngineLine(string line)
    {
        var low = line.ToLowerInvariant();

        if (LogNoise.Any(keyword => low.Contains(keyword))) return;

        // 旧版只把含 error / warn / connect 的行转给界面，避免刷屏
        if (!low.Contains("error") && !low.Contains("warn") && !low.Contains("connect")) return;

        LogLine(line.Trim());
    }

    private EngineStartResult Failure(string message) => new(false, message, null);

    /// <summary>给可能含空格或引号的参数加引号（房间名允许中文与空格）。</summary>
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string? Match(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>取一个当前空闲的 TCP 端口，避免固定 15888 被残留实例占用。</summary>
    private static int FreeTcpPort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        catch
        {
            return DefaultRpcPort;
        }
    }
}
