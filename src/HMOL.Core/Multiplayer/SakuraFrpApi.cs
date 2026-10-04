using System.Net.Http;
using System.Text.Json;

namespace HMOL.Core.Multiplayer;

/// <summary>樱花FRP 账号信息。</summary>
public sealed record SakuraUser(
    string Name,
    string Speed,
    int TunnelLimit,
    int RealName,
    long TrafficUsed,
    long TrafficRemaining,
    string BanReason);

/// <summary>樱花FRP 节点。flag 是位标志，含义见各计算属性。</summary>
public sealed record SakuraNode(int Id, string Name, string Host, string Description, int Flag)
{
    /// <summary>是否还允许创建隧道（节点满载时该位为 0）。</summary>
    public bool AllowsCreate => (Flag & (1 << 2)) != 0;

    /// <summary>是否允许 UDP 流量（本方案只用 TCP，仅供界面提示）。</summary>
    public bool AllowsUdp => (Flag & (1 << 5)) != 0;

    public bool IsOffline => (Flag & (1 << 9)) != 0;

    /// <summary>能不能拿来开 MO 直连的 TCP 隧道：能建隧道且没离线。</summary>
    public bool Usable => AllowsCreate && !IsOffline;

    /// <summary>节点列表里的一句话说明。</summary>
    public string Summary
    {
        get
        {
            var state = IsOffline ? "离线" : AllowsCreate ? "可新建" : "满员";
            var traffic = AllowsUdp ? "TCP/UDP" : "TCP";

            return $"{state} · {traffic}";
        }
    }
}

/// <summary>樱花FRP 隧道。</summary>
public sealed record SakuraTunnel(
    int Id,
    string Name,
    string Type,
    int NodeId,
    string LocalIp,
    int LocalPort,
    string Remote,
    string Export,
    bool Online,
    int Status,
    string StatusReason);

/// <summary>接口调用结果。失败时 Message 是可以直接给用户看的中文说明。</summary>
public sealed record SakuraResult<T>(bool Ok, T? Value, string Message)
{
    public static SakuraResult<T> Fail(string message) => new(false, default, message);

    public static SakuraResult<T> Success(T value) => new(true, value, string.Empty);
}

/// <summary>
/// 樱花FRP（SakuraFrp）开放接口客户端。只做四件事：查账号、查节点、查隧道、创建隧道。
///
/// <para>
/// 两个必须记住的坑：
/// <list type="number">
/// <item>鉴权只用 <c>Authorization: Bearer &lt;访问密钥&gt;</c>，没有换取 access_token 的流程。</item>
/// <item>错误码在 <b>响应体</b> 里而不是 HTTP 状态码里——访问密钥无效时 HTTP 返回的是 500，
/// body 才是 <c>{"code":401,"msg":"访问密钥无效"}</c>。所以判断成功与否必须解析 body。</item>
/// </list>
/// 另外官方没有启停隧道的接口，启停要靠自己拉起 frpc。
/// </para>
///
/// <para>
/// 本方案给心灵终结做的是 <b>TCP 1233</b> 的端口映射：MO 客户端加入别人的游戏时走 TCP 1233
/// 做大厅握手（见 ClientCore 的 <c>LAN_GAME_LOBBY_PORT</c>），配合客户端里的 <c>/ip 地址:端口</c>
/// 直连补丁，队友就能用隧道地址直接加入 —— 不需要虚拟网卡，也不需要局域网广播。
/// </para>
/// </summary>
public sealed class SakuraFrpApi : IDisposable
{
    private const string BaseUrl = "https://api.natfrp.com/v4";

    /// <summary>
    /// MO 加入游戏时握手用的 TCP 端口（ClientCore 的 LAN_GAME_LOBBY_PORT）。
    /// 隧道必须映射到主机的这个本地端口。
    /// </summary>
    public const int MoJoinPort = 1233;

    /// <summary>MO 客户端的局域网大厅「发现」端口（UDP 广播，本方案不需要穿透）。</summary>
    public const int MoDiscoveryPort = 1232;

    /// <summary>MO 进游戏后的对战数据端口（UDP，写进 spawn.ini；本方案暂不穿透）。</summary>
    public const int MoInGamePort = 1234;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public void Dispose() => _http.Dispose();

    // ————— 账号 —————

    public async Task<SakuraResult<SakuraUser>> GetUserAsync(string accessKey, CancellationToken token = default)
    {
        var (ok, body, message) = await SendAsync(HttpMethod.Get, "/user/info", accessKey, null, token).ConfigureAwait(false);
        if (!ok || body is null) return SakuraResult<SakuraUser>.Fail(message);

        try
        {
            var root = body.Value;

            // 被冻结的账号返回的是另一套结构，得单独认
            if (root.TryGetProperty("ban", out var ban))
            {
                var title = ReadString(ban, "title");
                var reason = ReadString(ban, "reason");
                var expires = ReadString(ban, "expires_text");

                return SakuraResult<SakuraUser>.Success(new SakuraUser(
                    ReadString(root, "name"), string.Empty, 0, 0, 0, 0,
                    $"账号已被冻结：{title} {reason} {expires}".Trim()));
            }

            return SakuraResult<SakuraUser>.Success(new SakuraUser(
                Name: ReadString(root, "name"),
                Speed: ReadString(root, "speed"),
                TunnelLimit: ReadInt(root, "tunnels"),
                RealName: ReadInt(root, "realname"),
                TrafficUsed: ReadTraffic(root, 0),
                TrafficRemaining: ReadTraffic(root, 1),
                BanReason: string.Empty));
        }
        catch (Exception ex)
        {
            return SakuraResult<SakuraUser>.Fail($"解析账号信息失败：{ex.Message}");
        }
    }

    // ————— 节点 —————

    public async Task<SakuraResult<List<SakuraNode>>> GetNodesAsync(string accessKey, CancellationToken token = default)
    {
        var (ok, body, message) = await SendAsync(HttpMethod.Get, "/nodes", accessKey, null, token).ConfigureAwait(false);
        if (!ok || body is null) return SakuraResult<List<SakuraNode>>.Fail(message);

        try
        {
            var nodes = new List<SakuraNode>();

            if (body.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in body.Value.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, out var id)) continue;

                    nodes.Add(new SakuraNode(
                        id,
                        ReadString(property.Value, "name"),
                        ReadString(property.Value, "host"),
                        ReadString(property.Value, "description"),
                        ReadInt(property.Value, "flag")));
                }
            }

            return SakuraResult<List<SakuraNode>>.Success(nodes);
        }
        catch (Exception ex)
        {
            return SakuraResult<List<SakuraNode>>.Fail($"解析节点列表失败：{ex.Message}");
        }
    }

    // ————— 隧道 —————

    public async Task<SakuraResult<List<SakuraTunnel>>> GetTunnelsAsync(string accessKey, CancellationToken token = default)
    {
        var (ok, body, message) = await SendAsync(HttpMethod.Get, "/tunnels", accessKey, null, token).ConfigureAwait(false);
        if (!ok || body is null) return SakuraResult<List<SakuraTunnel>>.Fail(message);

        try
        {
            var tunnels = new List<SakuraTunnel>();

            if (body.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in body.Value.EnumerateArray()) tunnels.Add(ReadTunnel(item));
            }

            return SakuraResult<List<SakuraTunnel>>.Success(tunnels);
        }
        catch (Exception ex)
        {
            return SakuraResult<List<SakuraTunnel>>.Fail($"解析隧道列表失败：{ex.Message}");
        }
    }

    /// <summary>创建一条 TCP 隧道，默认就映射到 MO 的 1233，省得用户填错。</summary>
    public async Task<SakuraResult<SakuraTunnel>> CreateTcpTunnelAsync(
        string accessKey, string name, int nodeId, int localPort = MoJoinPort, CancellationToken token = default)
    {
        var form = new Dictionary<string, string>
        {
            ["name"] = name,
            ["type"] = "tcp",
            ["node"] = nodeId.ToString(),
            ["local_ip"] = "127.0.0.1",
            ["local_port"] = localPort.ToString()
        };

        var (ok, body, message) = await SendAsync(HttpMethod.Post, "/tunnels", accessKey, form, token).ConfigureAwait(false);
        if (!ok || body is null) return SakuraResult<SakuraTunnel>.Fail(message);

        try
        {
            return SakuraResult<SakuraTunnel>.Success(new SakuraTunnel(
                Id: ReadInt(body.Value, "id"),
                Name: ReadString(body.Value, "name"),
                Type: "tcp",
                NodeId: nodeId,
                LocalIp: "127.0.0.1",
                LocalPort: localPort,
                Remote: ReadString(body.Value, "remote"),
                Export: string.Empty,
                Online: false,
                Status: 0,
                StatusReason: string.Empty));
        }
        catch (Exception ex)
        {
            return SakuraResult<SakuraTunnel>.Fail($"解析创建结果失败：{ex.Message}");
        }
    }

    /// <summary>把隧道换算成队友要输入的连接地址（节点域名:远程端口）。拿不到时返回空串。</summary>
    public static string BuildAddress(SakuraTunnel tunnel, IReadOnlyList<SakuraNode> nodes)
    {
        var host = nodes.FirstOrDefault(n => n.Id == tunnel.NodeId)?.Host ?? string.Empty;

        // export 形如「tcp|1.2.3.4|10086」，第二段就是公网出口 IP，比节点域名更直接
        if (!string.IsNullOrWhiteSpace(tunnel.Export))
        {
            var parts = tunnel.Export.Split('|');
            if (parts.Length >= 3 && parts[2].Length > 0)
            {
                var ip = parts[1].Length > 0 ? parts[1] : host;
                if (ip.Length > 0) return $"{ip}:{parts[2]}";
            }
        }

        var port = tunnel.Remote.Contains(':') ? tunnel.Remote[(tunnel.Remote.LastIndexOf(':') + 1)..] : tunnel.Remote;
        if (host.Length > 0 && port.Length > 0) return $"{host}:{port}";

        return string.Empty;
    }

    /// <summary>流量数值转成人看的大小（0 或负数表示接口没给，显示「未知」）。</summary>
    public static string FormatTraffic(long bytes)
    {
        if (bytes <= 0) return "未知";

        const double giga = 1024d * 1024 * 1024;
        return bytes >= giga ? $"{bytes / giga:0.##} GB" : $"{bytes / 1024d / 1024:0.#} MB";
    }

    // ————— 底层 —————

    private async Task<(bool Ok, JsonElement? Body, string Message)> SendAsync(
        HttpMethod method, string path, string accessKey, Dictionary<string, string>? form, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(accessKey)) return (false, null, "请先填写樱花FRP 访问密钥");

        try
        {
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessKey.Trim()}");

            if (form is not null) request.Content = new FormUrlEncodedContent(form);

            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                return response.IsSuccessStatusCode
                    ? (true, null, string.Empty)
                    : (false, null, $"服务端返回 HTTP {(int)response.StatusCode}");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            // 只认 body 里的 code：鉴权失败时 HTTP 是 500，光看状态码会误判
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() != 0)
            {
                var value = code.GetInt32();
                var message = root.TryGetProperty("msg", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                return (false, null, DescribeError(value, message));
            }

            return (true, root.Clone(), string.Empty);
        }
        catch (TaskCanceledException)
        {
            return (false, null, "请求超时，检查网络后重试");
        }
        catch (Exception ex)
        {
            return (false, null, $"请求失败：{ex.Message}");
        }
    }

    private static string DescribeError(int code, string message) => code switch
    {
        401 => $"访问密钥无效或已过期（{message}）。请到樱花FRP 管理面板重新复制「访问密钥」。",
        403 => $"无权访问（{message}）。可能没有实名认证，或该功能对你的账号未开放。",
        404 => $"接口不存在（{message}）。",
        _ => string.IsNullOrWhiteSpace(message) ? $"服务端返回错误代码 {code}" : $"{message}（代码 {code}）"
    };

    private static SakuraTunnel ReadTunnel(JsonElement item) => new(
        Id: ReadInt(item, "id"),
        Name: ReadString(item, "name"),
        Type: ReadString(item, "type"),
        NodeId: ReadInt(item, "node"),
        LocalIp: ReadString(item, "local_ip"),
        LocalPort: ReadInt(item, "local_port"),
        Remote: ReadString(item, "remote"),
        Export: ReadString(item, "export"),
        Online: item.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.True,
        Status: ReadInt(item, "status"),
        StatusReason: ReadString(item, "status_reason"));

    private static string ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        if (!element.TryGetProperty(name, out var value)) return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        };
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty(name, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0
        };
    }

    /// <summary>traffic 是 [本日消耗, 总剩余] 的二元数组，单位字节。</summary>
    private static long ReadTraffic(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        if (!element.TryGetProperty("traffic", out var traffic)) return 0;
        if (traffic.ValueKind != JsonValueKind.Array || traffic.GetArrayLength() <= index) return 0;

        var item = traffic[index];
        return item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value) ? value : 0;
    }
}
