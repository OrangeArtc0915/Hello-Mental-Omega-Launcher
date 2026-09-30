using System.Diagnostics;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// n2n supernode 服务端模式（本机自建节点）。对应旧版 engine_n2n.py:228 <c>SupernodeServer</c>。
/// </summary>
public sealed class SupernodeServer : EngineProcessBase
{
    /// <summary>supernode 管理端口（UDP）。engine_n2n.py:26 <c>SN_MGMT_PORT</c>。</summary>
    public const int MgmtPort = 5645;

    private readonly int _port;
    private readonly string _ipPool;

    public SupernodeServer(int port = 9555, string ipPool = NodeCatalog.N2nIpPool, Action<string>? log = null)
    {
        LogCallback = log;
        _port = port <= 0 ? 9555 : port;
        _ipPool = string.IsNullOrWhiteSpace(ipPool) ? NodeCatalog.N2nIpPool : ipPool.Trim();
    }

    protected override string EnginePrefix => "supernode";

    /// <summary>对外公布的节点地址：公网 IP:端口。</summary>
    public async Task<string> PublicAddressAsync(CancellationToken cancellationToken = default)
    {
        var ip = await NetworkToolkit.WanIpAsync(cancellationToken).ConfigureAwait(false);
        return $"{ip}:{_port}";
    }

    /// <summary>启动 supernode 并等待管理端口可应答。</summary>
    public async Task<EngineStartResult> StartAsync(
        double timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        var missing = RuntimeLocator.Verify((RuntimeLocator.N2nSupernodeExe, "n2n supernode.exe"));
        if (missing is not null) return new EngineStartResult(false, missing, null);

        if (IsRunning) return new EngineStartResult(false, "supernode 已在运行", null);

        var pool = BuildIpPool(_ipPool);

        LogLine($"启动 supernode：端口={_port} 自动IP池={_ipPool}");

        var arguments = $"-p {_port} -v";

        // 池区间与旧版一致：<网段首地址>-<网段.255.0>/<前缀长度>
        if (!string.IsNullOrEmpty(pool)) arguments += $" -a \"{pool}\"";

        LogLine(arguments);
        Launch(RuntimeLocator.N2nSupernodeExe, arguments, RuntimeLocator.N2nDir);

        var watch = Stopwatch.StartNew();

        while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HasExited(CurrentProcess)) return new EngineStartResult(false, "supernode 进程提前退出", null);

            var reply = await N2nEngine
                .QueryManagementAsync(MgmtPort, "*", TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(reply))
            {
                LogLine("supernode 已就绪");
                return new EngineStartResult(true, $"自建节点已启动，监听端口 {_port}", null);
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return new EngineStartResult(false, "supernode 启动超时", null);
    }

    /// <summary>把 <c>192.168.100.0/24</c> 变成 <c>192.168.100.0-192.168.255.0/24</c>。</summary>
    private static string BuildIpPool(string ipPool)
    {
        var parts = ipPool.Split('/');
        if (parts.Length != 2) return string.Empty;

        var segments = parts[0].Split('.');
        if (segments.Length != 4) return string.Empty;

        return $"{parts[0]}-{segments[0]}.{segments[1]}.255.0/{parts[1]}";
    }

    protected override void OnEngineLine(string line)
    {
        var low = line.ToLowerInvariant();

        if (!low.Contains("listening") && !low.Contains("new community")
            && !low.Contains("assign") && !low.Contains("error"))
            return;

        LogLine(line.Trim());
    }
}
