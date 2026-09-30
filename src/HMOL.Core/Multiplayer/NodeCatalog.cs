namespace HMOL.Core.Multiplayer;

/// <summary>内置公共节点。</summary>
public sealed record PublicNode(string Address, string Description);

/// <summary>
/// 内置节点列表与节点地址工具。对应旧版 nodes.py。
///
/// n2n 节点是历史上公开的公益节点（2026-08 实测大部分已失效，保留供自定义与参考）；
/// EasyTier 用的是官方公共节点。
/// </summary>
public static class NodeCatalog
{
    /// <summary>n2n 默认网段前三级（nodes.py:31 <c>N2N_DEFAULT_SUBNET</c>）。</summary>
    public const string N2nDefaultSubnet = "192.168.100";

    /// <summary>n2n 默认 IP 池（nodes.py:32 <c>N2N_IP_POOL</c>，自建 supernode 用）。</summary>
    public const string N2nIpPool = "192.168.100.0/24";

    /// <summary>EasyTier 默认网段前三级（nodes.py:33 <c>ET_DEFAULT_SUBNET</c>）。</summary>
    public const string EasyTierDefaultSubnet = "10.0.0";

    /// <summary>n2n 内置公共节点（nodes.py:10-18 <c>N2N_PUBLIC_NODES</c>）。</summary>
    public static IReadOnlyList<PublicNode> N2nPublicNodes { get; } =
    [
        new("n2n.aobacore.com:9555", "融合节点(北京/上海/广州/香港/日本/孟买)"),
        new("bj.n2n.aobacore.com:9555", "北京节点"),
        new("sh.n2n.aobacore.com:9555", "上海节点"),
        new("gz.n2n.aobacore.com:9555", "广州节点"),
        new("cd.n2n.aobacore.com:9555", "成都节点"),
        new("n2n.bugxia.com:8888", "Bugxia 节点"),
        new("supernode.ntop.org:7777", "n2n 官方测试节点")
    ];

    /// <summary>EasyTier 内置公共节点（nodes.py:20-29 <c>EASYTIER_PUBLIC_NODES</c>）。</summary>
    public static IReadOnlyList<PublicNode> EasyTierPublicNodes { get; } =
    [
        new("udp://39.108.52.138:11010", "阿里云广州(UDP)"),
        new("tcp://38.147.105.178:11010", "国内节点"),
        new("tcp://39.108.52.138:11010", "阿里云广州(TCP)"),
        new("udp://38.147.105.178:11010", "国内节点(UDP)"),
        new("tcp://103.224.243.207:11010", "腾讯云节点"),
        new("tcp://47.108.0.143:11010", "阿里云节点"),
        new("tcp://119.23.247.86:11010", "阿里云节点"),
        new("tcp://43.136.62.122:11010", "腾讯云节点")
    ];

    /// <summary>下拉框用的 n2n 节点列表（内置 + 自定义）。</summary>
    public static IReadOnlyList<string> N2nNodeOptions() => BuildOptions(N2nPublicNodes);

    /// <summary>下拉框用的 EasyTier 节点列表（内置 + 自定义）。</summary>
    public static IReadOnlyList<string> EasyTierNodeOptions() => BuildOptions(EasyTierPublicNodes);

    /// <summary>引擎用：EasyTier 节点原始地址列表（内置 + 自定义）。</summary>
    public static IReadOnlyList<string> EasyTierNodeValues()
        => BuildValues(EasyTierPublicNodes);

    /// <summary>引擎用：n2n 节点原始地址列表（内置 + 自定义）。</summary>
    public static IReadOnlyList<string> N2nNodeValues()
        => BuildValues(N2nPublicNodes);

    /// <summary>去掉 tcp:// udp:// 等前缀，返回 host:port。</summary>
    public static string StripScheme(string node)
        => string.IsNullOrEmpty(node) ? string.Empty
            : node.Contains("://") ? node[(node.IndexOf("://", StringComparison.Ordinal) + 3)..]
            : node;

    /// <summary>
    /// 节点地址 → (host, port)：兼容 tcp://ip:port / udp://ip:port / ip:port。
    /// 无法解析出端口时 port 为 null，由测速方回退为只 PING 主机。
    /// </summary>
    public static (string Host, int? Port) NodeHostPort(string node)
    {
        var text = StripScheme(node).Trim();
        var index = text.LastIndexOf(':');

        if (index > 0)
        {
            var host = text[..index];
            var portText = text[(index + 1)..];
            if (host.Length > 0 && int.TryParse(portText, out var port)) return (host, port);
        }

        return (text, null);
    }

    /// <summary>原始地址 → 下拉框显示文本：名称 (地址)。自定义节点显示原样。</summary>
    public static string NodeLabel(string node)
    {
        foreach (var item in AllPublicNodes())
        {
            if (item.Address == node) return $"{item.Description} ({item.Address})";
        }

        return node;
    }

    /// <summary>显示文本 → 原始地址（引擎/分享/配置用）。自定义节点的 label 即地址。</summary>
    public static string LabelToNode(string label)
    {
        foreach (var item in AllPublicNodes())
        {
            if ($"{item.Description} ({item.Address})" == label) return item.Address;
        }

        return label;
    }

    private static IEnumerable<PublicNode> AllPublicNodes()
        => N2nPublicNodes.Concat(EasyTierPublicNodes);

    private static IReadOnlyList<string> BuildOptions(IReadOnlyList<PublicNode> nodes)
    {
        var options = nodes.Select(item => NodeLabel(item.Address)).ToList();

        foreach (var custom in MultiplayerSettingsStore.Current.ValidCustomNodes)
        {
            if (!options.Contains(custom)) options.Add(custom);
        }

        return options;
    }

    private static IReadOnlyList<string> BuildValues(IReadOnlyList<PublicNode> nodes)
        => nodes.Select(item => item.Address)
            .Concat(MultiplayerSettingsStore.Current.ValidCustomNodes)
            .ToArray();
}
