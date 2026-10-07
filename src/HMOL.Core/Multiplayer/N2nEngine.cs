using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// n2n v3 引擎（edge 客户端进程管理）。对应旧版 engine_n2n.py。
///
/// 要点与旧版一致：需要 TAP 网卡、轮询网卡新增 IP、虚拟 IP 失败时用手工地址兜底、
/// 管理端口（UDP 5644）查询对端。自建 supernode 见 <see cref="SupernodeServer"/>。
/// </summary>
public sealed class N2nEngine : EngineProcessBase, INetworkEngine
{
    /// <summary>edge 管理端口（UDP）。engine_n2n.py:25 <c>MGMT_PORT</c>。</summary>
    public const int MgmtPort = 5644;

    /// <summary>启动等待虚拟 IP 的超时（与旧版界面传的 45 秒一致）。</summary>
    private const double ReadyTimeoutSeconds = 45;

    /// <summary>解析 edge 管理端口 peers 输出里的对端 TAP IP。</summary>
    private static readonly Regex PeerRowPattern = new(@"^\s*\d+\s*\|\s*([0-9.]+)", RegexOptions.Compiled);

    private readonly List<string> _nodes;

    private NetworkSessionOptions _options =
        new(string.Empty, string.Empty, string.Empty, string.Empty, true, NetworkAddressMode.Auto, null);

    private int _nodeIndex;

    /// <param name="nodes">节点候选列表（内置 + 自定义）；传 null 用 NodeCatalog 的默认列表。</param>
    /// <param name="log">日志回调。</param>
    public N2nEngine(IEnumerable<string>? nodes = null, Action<string>? log = null)
    {
        LogCallback = log;

        _nodes = (nodes ?? NodeCatalog.N2nNodeValues())
            .Where(node => !string.IsNullOrWhiteSpace(node))
            .Select(node => node.Trim())
            .ToList();

        if (_nodes.Count == 0) _nodes.Add(NodeCatalog.N2nPublicNodes[0].Address);
    }

    public NetworkEngineKind Kind => NetworkEngineKind.N2n;

    public string DisplayName => "n2n";

    /// <summary>
    /// 用房间名当小组名时是隔开的；公共节点要求固定小组名（如 fox）时不隔开，
    /// 同一个小组里会有别的房间甚至别的启动器的人。
    /// </summary>
    public bool NetworkIsolatesRoom => NodeCatalog.N2nCommunityFor(CurrentNode()) is null;

    protected override string EnginePrefix => "n2n";

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

    public async Task<EngineStartResult> StartAsync(
        NetworkSessionOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var missing = RuntimeLocator.Verify((RuntimeLocator.N2nEdgeExe, "n2n edge.exe"));
        if (missing is not null) return Failure(missing);

        if (IsRunning) return Failure(Loc.F("{0} 引擎已在运行", DisplayName));

        if (string.IsNullOrWhiteSpace(options.RoomName)) return Failure(Loc.T("房间名（小组名称）不能为空"));

        _options = options;
        ReorderNodes(options.Node);
        LocalIp = null;

        try
        {
            // n2n 必须有 TAP 虚拟网卡，缺驱动时直接给出可操作的中文原因
            var tapCount = await NetworkToolkit.TapCountAsync(cancellationToken).ConfigureAwait(false);
            if (tapCount <= 0) return Failure(Loc.T("未检测到 TAP 虚拟网卡，请先在网络工具箱中安装 TAP 驱动（需要管理员权限）"));

            if (options.AddressMode == NetworkAddressMode.Manual)
            {
                return await StartOnceAsync(ResolveManualIp(), progress, cancellationToken).ConfigureAwait(false);
            }

            var result = await StartOnceAsync(null, progress, cancellationToken).ConfigureAwait(false);
            if (result.Ok) return result;

            // 自动（DHCP）拿不到虚拟 IP 时用手工地址兜底，这与旧版 _connect_worker 的行为一致
            LogLine(Loc.T("自动获取虚拟 IP 失败，尝试手动 IP 兜底..."));
            await StopAsync().ConfigureAwait(false);

            var fallbackIp = ResolveManualIp();
            var second = await StartOnceAsync(fallbackIp, progress, cancellationToken).ConfigureAwait(false);

            if (!second.Ok) return second;

            LogLine(Loc.F("已用手动 IP 兜底: {0}", fallbackIp));
            LogLine(Loc.T("提示: 请让队友也选 n2n, 并把手动 IP 改成不同地址 (如 192.168.100.67)"));

            return second with
            {
                Message = Loc.F("{0}（已用手动 IP 兜底：{1}，请让队友改用不同地址，如 192.168.100.67）", second.Message, fallbackIp)
            };
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            return Failure(Loc.T("启动已取消"));
        }
    }

    /// <summary>手工 IP：会话参数优先，会话未给时读联机配置，仍为空退回旧版默认 192.168.100.66。</summary>
    private string ResolveManualIp() => NetworkEngineFactory.ResolveManualIp(_options);

    /// <summary>启动一次并等待 TAP 网卡上出现新的虚拟 IP。</summary>
    private async Task<EngineStartResult> StartOnceAsync(
        string? manualIp,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var node = CurrentNode();

        LogLine(Loc.F("启动 edge：节点={0} 房间={1} ", node, _options.RoomName) +
                Loc.F("IP模式={0}", (string.IsNullOrEmpty(manualIp) ? Loc.T("自动(DHCP)") : manualIp)));

        var arguments = BuildArguments(node, manualIp);
        LogLine(arguments);

        // 启动前先记下已有的 TAP IP，用来排除上次残留的地址
        var before = (await NetworkToolkit.TapIpsAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Launch(RuntimeLocator.N2nEdgeExe, arguments, RuntimeLocator.N2nDir);

        var watch = Stopwatch.StartNew();
        var lastProgress = 0;

        while (watch.Elapsed < TimeSpan.FromSeconds(ReadyTimeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasExited(CurrentProcess)) return Failure(Loc.T("edge 进程提前退出"));

            foreach (var ip in await NetworkToolkit.TapIpsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (before.Contains(ip) || ip == LocalIp) continue;

                LocalIp = ip;
                await NetworkToolkit.SetInterfaceMetricAsync(ip, 1, cancellationToken).ConfigureAwait(false);
                LogLine(Loc.F("edge 已就绪，虚拟IP={0}", ip));
                return new EngineStartResult(true, Loc.F("连接成功，虚拟 IP：{0}", ip), ip);
            }

            var elapsed = (int)watch.Elapsed.TotalSeconds;
            if (elapsed - lastProgress >= 15)
            {
                lastProgress = elapsed;
                progress?.Report(Loc.F("正在等待 TAP 网卡分配虚拟 IP（{0}s）...", elapsed));
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return Failure(Loc.T("edge 启动超时，未获得虚拟 IP"));
    }

    /// <summary>命令行参数与旧版 engine_n2n.py:136-145 逐条对齐。</summary>
    private string BuildArguments(string node, string? manualIp)
    {
        var parts = new List<string>();

        if (string.IsNullOrWhiteSpace(manualIp))
        {
            parts.AddRange(["-a", "dhcp:0.0.0.0/0"]);
        }
        else
        {
            parts.AddRange(["-a", Quote(manualIp.Contains('/') ? manualIp : $"{manualIp}/24")]);
        }

        // 部分公益节点只服务固定小组（如 fox）：此时必须传它要求的小组名才注册得上，
        // 房间名不参与 n2n 组网，房间隔离改由 HMOL 自己的密钥与房间逻辑负责。
        var community = NodeCatalog.N2nCommunityFor(node);
        if (community is not null)
        {
            LogLine(Loc.F("节点 {0} 固定使用小组名「{1}」，房间名不参与 n2n 组网", node, community));
        }

        parts.AddRange(["-c", Quote(community ?? _options.RoomName)]);

        if (!string.IsNullOrEmpty(_options.RoomKey)) parts.AddRange(["-k", Quote(_options.RoomKey)]);

        parts.AddRange(["-l", Quote(node), "-E", "-t", MgmtPort.ToString(), "-v"]);

        return string.Join(' ', parts);
    }

    public async Task<IReadOnlyList<NetworkPeer>> QueryPeersAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) return [];

        var text = await QueryManagementAsync(MgmtPort, "peers", TimeSpan.FromSeconds(2), cancellationToken)
            .ConfigureAwait(false);

        return ParsePeersTable(text).Select(ip => new NetworkPeer(ip, ip, string.Empty, string.Empty)).ToArray();
    }

    /// <summary>n2n 没有节点信息接口，只回报本机虚拟 IP。</summary>
    public Task<EngineNodeInfo> NodeInfoAsync(CancellationToken cancellationToken)
        => Task.FromResult(new EngineNodeInfo(
            string.IsNullOrEmpty(LocalIp) ? null : LocalIp, null, null, null));

    /// <summary>解析 edge 管理端口的 peers 输出，提取对端 TAP IP（去重、排除 0.0.0.0）。</summary>
    public static IReadOnlyList<string> ParsePeersTable(string? text)
    {
        var peers = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return peers;

        foreach (var line in text.Split('\n'))
        {
            var match = PeerRowPattern.Match(line);
            if (!match.Success) continue;

            var ip = match.Groups[1].Value;
            if (ip == "0.0.0.0" || peers.Contains(ip)) continue;

            peers.Add(ip);
        }

        return peers;
    }

    /// <summary>向 edge / supernode 的管理端口（UDP）发命令并读取返回文本。</summary>
    internal static async Task<string> QueryManagementAsync(
        int port,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            await socket.SendToAsync(
                Encoding.UTF8.GetBytes(command),
                SocketFlags.None,
                new IPEndPoint(IPAddress.Loopback, port),
                cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var buffer = new byte[65536];
            var result = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                timeoutSource.Token).ConfigureAwait(false);

            return Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 管理端口没起来、超时、被防火墙拦，统一按「没有结果」处理
            return string.Empty;
        }
    }

    protected override void OnEngineLine(string line)
    {
        var low = line.ToLowerInvariant();

        if (!low.Contains("registering") && !low.Contains("pong") && !low.Contains("error") && !low.Contains("warning"))
            return;

        LogLine(line.Trim());
    }

    private EngineStartResult Failure(string message) => new(false, message, null);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
