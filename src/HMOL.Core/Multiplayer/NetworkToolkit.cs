using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HMOL.Core.Logging;

namespace HMOL.Core.Multiplayer;

/// <summary>PING 结果：成功次数与平均延迟。</summary>
public sealed record PingResult(double AvgMs, int Total, int Ok);

/// <summary>带宽测速结果。</summary>
public sealed record BandwidthResult(double Seconds, long Bytes, double Mbps);

/// <summary>工具箱里「有结果 / 有原因」的操作返回它。Message 是可以直接展示的中文。</summary>
public sealed record ToolkitStatus(bool Ok, string Message);

/// <summary>
/// 网络工具箱。对应旧版 toolkit.py：PING / TCP·UDP 测速 / NAT 类型（STUN）/ 防火墙 /
/// 网卡跃点 / TAP 驱动安装 / WinIPBroadcast 广播服务。
///
/// 涉及系统命令的操作一律返回可读的中文失败原因（不静默失败）；
/// 需要管理员权限的操作在提权不足时直接给出「需要管理员权限」的说明。
/// </summary>
public static class NetworkToolkit
{
    /// <summary>TCP 测速默认端口（toolkit.py:259 / :298 默认值）。</summary>
    public const int TcpSpeedPort = 48771;

    /// <summary>UDP 测速默认端口（toolkit.py:322 / :358 默认值）。</summary>
    public const int UdpSpeedPort = 48772;

    /// <summary>STUN 服务器（toolkit.py:384-388 <c>STUN_SERVERS</c>）。</summary>
    private static readonly (string Host, int Port)[] StunServers =
    [
        ("stun.qq.com", 3478),
        ("stun1.l.google.com", 19302),
        ("stun.miwifi.com", 3478)
    ];

    private static readonly Regex PingMsPattern = new(@"(\d+(?:\.\d+)?)\s*ms", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>防火墙状态行：兼容英文与中文（中文版 netsh 用「启用 / 禁用」）。</summary>
    private static readonly Regex FirewallStatePattern = new(
        @"state\s+(?<on>on|启用|已启用|开启|已开启)"
        + @"|state\s+(?<off>off|禁用|已禁用|关闭|已关闭)"
        + @"|状态\s+(?<on>启用|已启用|开启|已开启)"
        + @"|状态\s+(?<off>禁用|已禁用|关闭|已关闭)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // ————— PING / 延迟 —————

    /// <summary>PING 测试，返回成功次数与平均延迟。对应旧版 toolkit.py:220。</summary>
    public static async Task<PingResult> PingAsync(
        string host,
        int count = 4,
        double timeoutSeconds = 2.0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return new PingResult(0, count, 0);

        var ok = 0;
        var times = new List<double>();
        var waitMs = (int)(timeoutSeconds * 1000);

        for (var i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
        {
            var result = await ProcessRunner.RunAsync(
                "ping",
                $"-n 1 -w {waitMs} {Quote(host)}",
                TimeSpan.FromSeconds(timeoutSeconds + 1),
                cancellationToken).ConfigureAwait(false);

            var text = result.Output;
            if (result.ExitCode == 0 || text.Contains("TTL=", StringComparison.OrdinalIgnoreCase))
            {
                ok++;
                var ms = ExtractPingMs(text);
                if (ms is not null) times.Add(ms.Value);
            }
        }

        return new PingResult(times.Count > 0 ? Math.Round(times.Average(), 1) : 0, count, ok);
    }

    /// <summary>兼容中文（时间=1ms）与英文（time=1ms / time&lt;1ms）输出，取最后一次回应的延迟。</summary>
    public static double? ExtractPingMs(string? text)
    {
        var matches = PingMsPattern.Matches(text ?? string.Empty);
        if (matches.Count == 0) return null;

        return double.TryParse(matches[^1].Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>TCP 建连耗时（毫秒），用于节点测速；连接失败返回 null。</summary>
    public static async Task<int?> TcpLatencyAsync(
        string host,
        int? port,
        double timeoutSeconds = 3.0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is null or <= 0) return null;

        try
        {
            using var client = new TcpClient();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var watch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port.Value, timeoutSource.Token).ConfigureAwait(false);
            watch.Stop();

            return (int)Math.Round(watch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // ————— 带宽测速 —————

    /// <summary>TCP 测速服务端：监听并接收客户端数据，返回吞吐量（Mbps）。</summary>
    public static async Task<BandwidthResult> TcpBandwidthServerAsync(
        int port = TcpSpeedPort,
        double seconds = 3.0,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Any, port);

        try
        {
            listener.Start(1);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法在端口 {port} 启动测速服务端：{ex.Message}");
        }

        progress?.Report("等待客户端连接...");

        try
        {
            Socket connection;

            using (var acceptSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                acceptSource.CancelAfter(TimeSpan.FromSeconds(20));

                try
                {
                    connection = await listener.AcceptSocketAsync(acceptSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new BandwidthResult(0, 0, 0);
                }
            }

            using (connection)
            {
                return await MeasureTcpReceiveAsync(connection, seconds, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>TCP 测速客户端：连接服务端并持续发送数据。连接失败抛出带中文说明的异常。</summary>
    public static async Task<BandwidthResult> TcpBandwidthClientAsync(
        string host,
        int port = TcpSpeedPort,
        double seconds = 3.0,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"连接 {host}:{port} 并发送数据...");

        using var client = new TcpClient();

        try
        {
            using var connectSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectSource.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(host, port, connectSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法连接 {host}:{port}（{ex.Message}），请确认对方已开启测速服务端且防火墙已放行。");
        }

        var payload = RandomNumberGenerator.GetBytes(65536);
        var stream = client.GetStream();

        var watch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(seconds);
        long total = 0;

        while (watch.Elapsed < deadline)
        {
            using var writeSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeSource.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await stream.WriteAsync(payload, writeSource.Token).ConfigureAwait(false);
                total += payload.Length;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                break;
            }
        }

        watch.Stop();
        return Build(watch.Elapsed.TotalSeconds, total);
    }

    /// <summary>UDP 测速服务端：接收 UDP 数据包统计吞吐（以收到首个包为计时起点）。</summary>
    public static async Task<BandwidthResult> UdpBandwidthServerAsync(
        int port = UdpSpeedPort,
        double seconds = 3.0,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法在端口 {port} 启动 UDP 测速服务端：{ex.Message}");
        }

        progress?.Report("等待客户端数据...");

        var buffer = new byte[65536];
        long total = 0;
        var received = false;
        var watch = new Stopwatch();

        while (true)
        {
            using var receiveSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveSource.CancelAfter(TimeSpan.FromSeconds(20));

            int length;

            try
            {
                var result = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    receiveSource.Token).ConfigureAwait(false);

                length = result.ReceivedBytes;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                break;
            }

            if (!received)
            {
                received = true;
                watch.Start();
            }

            total += length;

            if (watch.Elapsed.TotalSeconds >= seconds) break;
        }

        watch.Stop();
        return received ? Build(watch.Elapsed.TotalSeconds, total) : new BandwidthResult(0, 0, 0);
    }

    /// <summary>UDP 测速客户端：持续向服务端发送 UDP 数据。</summary>
    public static async Task<BandwidthResult> UdpBandwidthClientAsync(
        string host,
        int port = UdpSpeedPort,
        double seconds = 3.0,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"发送 UDP 数据到 {host}:{port}...");

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var payload = RandomNumberGenerator.GetBytes(1200);
        var endpoint = new IPEndPoint(IPAddress.Parse(host), port);

        var watch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(seconds);
        long total = 0;

        while (watch.Elapsed < deadline)
        {
            try
            {
                await socket.SendToAsync(payload, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
                total += payload.Length;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                break;
            }
        }

        watch.Stop();
        return Build(watch.Elapsed.TotalSeconds, total);
    }

    private static async Task<BandwidthResult> MeasureTcpReceiveAsync(
        Socket connection,
        double seconds,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var watch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(seconds);
        long total = 0;

        while (watch.Elapsed < deadline)
        {
            using var readSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readSource.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                var received = await connection.ReceiveAsync(buffer, SocketFlags.None, readSource.Token).ConfigureAwait(false);
                if (received <= 0) break;

                total += received;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                break;
            }
        }

        watch.Stop();
        return Build(watch.Elapsed.TotalSeconds, total);
    }

    private static BandwidthResult Build(double seconds, long bytes)
        => new(Math.Round(seconds, 2), bytes, Math.Round(bytes * 8 / (seconds * 1_000_000.0), 2));

    // ————— NAT 类型（STUN） —————

    /// <summary>
    /// 检测 NAT 类型：开放 / 全锥形 / 受限锥形 / 对称 / 检测失败。对应旧版 toolkit.py:441。
    /// 判定法（STUN 标准）：用同一本地端口向多个不同的 STUN 服务器请求，
    /// 不同服务器看到不同公网地址 =&gt; 对称型；相同 =&gt; 锥形；公网 IP 等于本机 =&gt; 开放。
    /// </summary>
    public static async Task<string> NatTypeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在请求 STUN 服务器...");

        var local = LocalIpv4();
        if (string.IsNullOrEmpty(local)) return "检测失败";

        var localPort = RandomNumberGenerator.GetInt32(20000, 60000);
        var results = new List<(string Ip, int Port)>();

        foreach (var (host, port) in StunServers)
        {
            var mapped = await StunRequestAsync(host, port, localPort, TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);

            if (mapped is not null) results.Add(mapped.Value);
        }

        if (results.Count == 0) return "检测失败";

        var (publicIp, publicPort) = results[0];
        if (publicIp == local) return "开放(公网IP)";

        foreach (var (ip, port) in results.Skip(1))
        {
            if (ip != publicIp || port != publicPort) return "对称型 NAT";
        }

        return "锥形 NAT(受限锥形/全锥形)";
    }

    /// <summary>本机在默认路由上使用的 IPv4 地址；取不到时返回空串。</summary>
    public static string LocalIpv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80));

            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>发送 STUN binding 请求，返回 (公网IP, 公网端口)；失败返回 null。</summary>
    private static async Task<(string Ip, int Port)?> StunRequestAsync(
        string host,
        int port,
        int localPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, localPort));

            var request = new byte[28];
            request[1] = 0x01;                                  // Binding Request
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42;  // Magic Cookie
            RandomNumberGenerator.GetBytes(12).CopyTo(request, 8);                       // Transaction ID
            request[21] = 0x00; request[22] = 0x03; request[23] = 0x00; request[24] = 0x04; // Change Request, 0

            await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(IPAddress.Parse(host), port), cancellationToken)
                .ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var buffer = new byte[2048];
            var received = await socket.ReceiveFromAsync(
                buffer,
                SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0),
                timeoutSource.Token).ConfigureAwait(false);

            var length = received.ReceivedBytes;
            if (length < 20 || buffer[0] != 0x01) return null;

            // 校验 Magic Cookie（0x2112A442）
            if (buffer[4] != 0x21 || buffer[5] != 0x12 || buffer[6] != 0xA4 || buffer[7] != 0x42) return null;

            var position = 20;

            while (position + 4 <= length)
            {
                var type = (buffer[position] << 8) | buffer[position + 1];
                var attributeLength = (buffer[position + 2] << 8) | buffer[position + 3];
                var value = position + 4;

                if (type == 0x0020 && attributeLength >= 8)   // XOR-MAPPED-ADDRESS
                {
                    var mappedPort = ((buffer[value + 2] << 8) | buffer[value + 3]) ^ 0x2112;
                    var ip = string.Join('.', new[]
                    {
                        buffer[value + 4] ^ 0x21,
                        buffer[value + 5] ^ 0x12,
                        buffer[value + 6] ^ 0xA4,
                        buffer[value + 7] ^ 0x42
                    });

                    return (ip, mappedPort);
                }

                position += 4 + ((attributeLength + 3) & ~3);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // ————— 防火墙 —————

    /// <summary>防火墙状态：已关闭 / 已开启 / 未知。对应旧版 toolkit.py:468。</summary>
    public static async Task<string> FirewallStateAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner
            .RunAsync("netsh", "advfirewall show allprofiles", TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started || string.IsNullOrWhiteSpace(result.Output)) return "未知";

        var matches = FirewallStatePattern.Matches(result.Output);
        if (matches.Count == 0) return "未知";

        var hasOff = matches.Any(match => match.Groups["off"].Success);
        if (hasOff) return "已关闭";

        var hasOn = matches.Any(match => match.Groups["on"].Success);
        return hasOn ? "已开启" : "未知";
    }

    /// <summary>
    /// 开启/关闭全部配置文件防火墙（需要管理员权限）。
    /// 权限不足时直接返回可读原因，不做静默失败。
    /// </summary>
    public static async Task<ToolkitStatus> SetFirewallAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!ElevationHelper.IsElevated)
            return new ToolkitStatus(false, "修改防火墙需要管理员权限（当前以普通权限运行）");

        var state = enabled ? "on" : "off";
        var result = await ProcessRunner
            .RunAsync("netsh", $"advfirewall set allprofiles state {state}", TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok) return new ToolkitStatus(true, enabled ? "防火墙已开启" : "防火墙已关闭");

        var reason = result.FirstLine();
        return new ToolkitStatus(false, string.IsNullOrEmpty(reason)
            ? $"防火墙{(enabled ? "开启" : "关闭")}失败（netsh 退出码 {result.ExitCode}）"
            : $"防火墙{(enabled ? "开启" : "关闭")}失败：{reason}");
    }

    /// <summary>
    /// 为指定 TCP 端口添加防火墙入站放行规则（对应旧版 filetrans.py:86 的 <c>_try_firewall</c>）。
    /// 需要管理员权限；权限不足时返回可读原因，由调用方决定是否提示。
    /// </summary>
    public static async Task<ToolkitStatus> SetFirewallRuleAsync(
        string ruleName,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new ToolkitStatus(false, "非 Windows 无需放行端口");

        var arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}";

        var result = await ProcessRunner
            .RunAsync("netsh", arguments, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);

        if (result.Ok) return new ToolkitStatus(true, $"已放行 TCP {port}");

        var reason = result.FirstLine();
        if (LooksLikePermissionError(reason) || !ElevationHelper.IsElevated)
            return new ToolkitStatus(false, "需要管理员权限才能自动放行端口（可在 Windows 防火墙里手动放行）");

        return new ToolkitStatus(false, string.IsNullOrEmpty(reason)
            ? $"添加防火墙规则失败（netsh 退出码 {result.ExitCode}）"
            : $"添加防火墙规则失败：{reason}");
    }

    private static bool LooksLikePermissionError(string text)
        => text.Contains("提升", StringComparison.Ordinal)
           || text.Contains("管理员", StringComparison.Ordinal)
           || text.Contains("拒绝访问", StringComparison.Ordinal)
           || text.Contains("elevat", StringComparison.OrdinalIgnoreCase)
           || text.Contains("access is denied", StringComparison.OrdinalIgnoreCase);

    // ————— 网卡 / TAP / 广播服务 —————
    /// <summary>本机 TAP 虚拟网卡数量。对应旧版 toolkit.py:500。</summary>
    public static async Task<int> TapCountAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return 0;

        var text = await ProcessRunner.RunPowerShellTextAsync(
            "(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -like '*TAP*' } | Measure-Object).Count",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        return int.TryParse(text.Trim(), out var count) ? count : 0;
    }

    /// <summary>本机 TAP 虚拟网卡上的 IPv4 地址（非 0.0.0.0）。对应旧版 engine_n2n.py:47。</summary>
    public static async Task<IReadOnlyList<string>> TapIpsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return [];

        var text = await ProcessRunner.RunPowerShellTextAsync(
            "(Get-NetAdapter -ErrorAction SilentlyContinue "
            + "| Where-Object { $_.InterfaceDescription -like '*TAP*' } "
            + "| ForEach-Object { Get-NetIPAddress -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue } "
            + "| Where-Object { $_.IPAddress -ne '0.0.0.0' } "
            + "| Select-Object -ExpandProperty IPAddress) -join ','",
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 降低持有虚拟 IP 的网卡接口跃点数，让游戏广播优先走虚拟网卡。
    /// 对应旧版 engine_easytier.py:114 / engine_n2n.py:60（两处实现相同）。
    /// </summary>
    public static async Task<ToolkitStatus> SetInterfaceMetricAsync(
        string ip,
        int metric = 1,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ip))
            return new ToolkitStatus(false, "没有可调整的虚拟 IP");

        var script =
            $"Get-NetIPAddress -IPAddress {ip} -ErrorAction SilentlyContinue "
            + $"| ForEach-Object {{ Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -InterfaceMetric {metric} -ErrorAction SilentlyContinue }}";

        var result = await ProcessRunner
            .RunPowerShellAsync(script, TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);

        return result.Started
            ? new ToolkitStatus(true, $"已将 {ip} 所在网卡跃点数设为 {metric}")
            : new ToolkitStatus(false, $"调整网卡跃点数失败：{result.Output}");
    }

    /// <summary>
    /// 静默安装 TAP 驱动（tapinstall.exe + OemVista.inf）。需要管理员权限，由提权实例调用。
    /// 对应旧版 toolkit.py:513，命令形式：<c>tapinstall.exe install OemVista.inf tap0901</c>。
    /// </summary>
    public static async Task<ToolkitStatus> InstallTapDriverAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new ToolkitStatus(false, "非 Windows 无需安装 TAP 驱动");

        var missing = RuntimeLocator.Verify(
            (RuntimeLocator.TapInstallExe, "tapinstall.exe"),
            (RuntimeLocator.TapInfFile, "OemVista.inf"));

        if (missing is not null) return new ToolkitStatus(false, missing);

        var result = await ProcessRunner.RunAsync(
            RuntimeLocator.TapInstallExe,
            "install OemVista.inf tap0901",
            TimeSpan.FromSeconds(60),
            cancellationToken,
            workingDirectory: RuntimeLocator.TapDir).ConfigureAwait(false);

        if (!result.Ok)
        {
            var reason = result.FirstLine();
            return new ToolkitStatus(false, string.IsNullOrEmpty(reason)
                ? $"TAP 驱动安装失败（tapinstall 退出码 {result.ExitCode}）"
                : $"TAP 驱动安装失败：{reason}");
        }

        // 驱动装完系统需要几秒枚举网卡，等一下再确认
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        var count = await TapCountAsync(cancellationToken).ConfigureAwait(false);
        return count > 0
            ? new ToolkitStatus(true, "TAP 驱动安装完成")
            : new ToolkitStatus(false, "驱动已执行安装，但未检测到 TAP 网卡，请重启后再试");
    }

    /// <summary>
    /// 确保 WinIPBroadcast 服务安装并运行（局域网广播转发）。需要管理员权限。
    /// 对应旧版 toolkit.py:535。
    /// </summary>
    public static async Task<ToolkitStatus> EnsureWinIpBroadcastAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new ToolkitStatus(false, "非 Windows 不支持 WinIPBroadcast");

        var query = await ProcessRunner
            .RunAsync("sc", "query WinIPBroadcast", TimeSpan.FromSeconds(8), cancellationToken)
            .ConfigureAwait(false);

        if (query.Ok)
        {
            var start = await ProcessRunner
                .RunAsync("net", "start WinIPBroadcast", TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);

            return new ToolkitStatus(true, start.Ok ? "WinIPBroadcast 服务已在运行" : "WinIPBroadcast 服务已安装（启动未成功，请手动启动）");
        }

        if (!RuntimeLocator.Exists(RuntimeLocator.WinIpBroadcastExe))
            return new ToolkitStatus(false, RuntimeLocator.MissingMessage("WinIPBroadcast-1.6.exe"));

        var install = await ProcessRunner.RunAsync(
            RuntimeLocator.WinIpBroadcastExe,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            TimeSpan.FromSeconds(90),
            cancellationToken,
            workingDirectory: RuntimeLocator.WinIpBroadcastDir).ConfigureAwait(false);

        if (!install.Started)
            return new ToolkitStatus(false, $"WinIPBroadcast 安装程序启动失败：{install.Output}");

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        var started = await ProcessRunner
            .RunAsync("net", "start WinIPBroadcast", TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);

        return new ToolkitStatus(true, started.Ok ? "WinIPBroadcast 已安装并启动" : "WinIPBroadcast 已安装（启动未成功，请手动启动）");
    }

    /// <summary>查询本机公网 IP；失败返回 "?"（与旧版 _wan_ip 一致）。</summary>
    public static async Task<string> WanIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ip = (await Http.GetStringAsync("https://api.ipify.org", cancellationToken).ConfigureAwait(false)).Trim();
            return ip.Length > 0 && !ip.Contains(' ') ? ip : "?";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"查询公网 IP 失败：{ex.Message}");
            return "?";
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
