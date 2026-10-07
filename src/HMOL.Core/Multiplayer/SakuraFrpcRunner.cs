using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 樱花FRP 隧道进程管理。
///
/// <para>
/// 官方没有「启停隧道」的接口——启停就是自己把 frpc 拉起来、连到节点、把本地端口穿透出去。
/// 这里用官方的 <c>-f &lt;访问密钥&gt;:&lt;隧道ID&gt;</c>：配置由 frpc 自己从服务器拉，
/// <b>不落盘</b>（不加 <c>-w</c>），所以本机不会留下含访问密钥的配置文件。
/// </para>
///
/// <para>
/// frpc 本身不随程序分发：按官方 <c>/system/clients</c> 接口取当前版本的下载地址与 MD5，
/// 下到数据目录后校验再使用。它是樱花FRP 自家的第三方客户端，跟着版本走比内置一份更稳妥。
/// </para>
/// </summary>
public sealed class SakuraFrpcRunner : IDisposable
{
    private const string ClientsUrl = "https://api.natfrp.com/v4/system/clients";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>frpc 日志里被 &gt;&gt;...&lt;&lt; 包住的连接地址。</summary>
    private static readonly Regex BracketedAddress = new(@">>\s*([^<>]{3,80}?)\s*<<", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<string> _output = [];

    private Process? _process;

    /// <summary>frpc 的存放目录（跟着 Data 目录走，可随程序一起搬）。</summary>
    public static string ToolDirectory => Path.Combine(Paths.Data, "SakuraFrp");

    public static string FrpcPath => Path.Combine(ToolDirectory, "frpc.exe");

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// 域名形式的连接地址（形如 frp-xxx.com:10086）。MO 客户端目前只认 IP，仅作备用。
    /// </summary>
    public string DomainAddress { get; private set; } = string.Empty;

    /// <summary>IP 形式的连接地址（形如 114.51.4.191:10086）。队友要用的就是这一条。</summary>
    public string IpAddress { get; private set; } = string.Empty;

    /// <summary>frpc 打出来的日志行（已过滤过纯噪音）。</summary>
    public event Action<string>? Log;

    /// <summary>认出了新的连接地址（域名或 IP）。</summary>
    public event Action? AddressesUpdated;

    /// <summary>隧道启动成功。</summary>
    public event Action? Started;

    public void Dispose() => Stop();

    // ————— 准备 frpc —————

    /// <summary>
    /// 确保本地有可用的 frpc。已有且大小对得上就直接用，否则按官方接口给的地址下载并用 MD5 校验。
    /// </summary>
    public async Task<SakuraResult<string>> EnsureFrpcAsync(CancellationToken token = default)
    {
        try
        {
            var (url, hash, size) = await ResolveDownloadAsync(token).ConfigureAwait(false);
            if (url.Length == 0) return SakuraResult<string>.Fail(Loc.T("没能从樱花FRP 接口取到 frpc 下载地址"));

            if (IsUsable(hash, size))
            {
                LogLine(Loc.F("已有可用的 frpc：{0}", FrpcPath));
                return SakuraResult<string>.Success(FrpcPath);
            }

            Directory.CreateDirectory(ToolDirectory);

            LogLine(Loc.F("正在下载 frpc（约 {0:0.0} MB）…", size / 1024 / 1024.0));

            var bytes = await Http.GetByteArrayAsync(url, token).ConfigureAwait(false);

            if (bytes.Length == 0) return SakuraResult<string>.Fail(Loc.T("下载到的 frpc 是空文件"));

            if (hash.Length > 0)
            {
                var actual = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return SakuraResult<string>.Fail(
                        Loc.F("frpc 校验失败：官方公布 MD5 为 {0}，实际下载到 {1}。已放弃使用，请稍后重试。", hash, actual));
                }
            }

            var temp = FrpcPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, FrpcPath, overwrite: true);

            LogLine(Loc.F("frpc 已就绪（MD5 校验通过）：{0}", FrpcPath));
            return SakuraResult<string>.Success(FrpcPath);
        }
        catch (TaskCanceledException)
        {
            return SakuraResult<string>.Fail(Loc.T("下载 frpc 超时，检查网络后重试"));
        }
        catch (Exception ex)
        {
            return SakuraResult<string>.Fail(Loc.F("准备 frpc 失败：{0}", ex.Message));
        }
    }

    private static bool IsUsable(string hash, long size)
    {
        try
        {
            if (!File.Exists(FrpcPath)) return false;

            var info = new FileInfo(FrpcPath);
            if (size > 0 && info.Length != size) return false;

            if (hash.Length > 0)
            {
                using var stream = File.OpenRead(FrpcPath);
                var actual = Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
                if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 <c>/system/clients</c>。这个接口不需要鉴权，结构上是一层「分类 → archs → {url, hash, size}」，
    /// 所以这里按名字模糊找 frpc 与 windows/amd64，避免结构微调就整个失效。
    /// </summary>
    private static async Task<(string Url, string Hash, long Size)> ResolveDownloadAsync(CancellationToken token)
    {
        var text = await Http.GetStringAsync(ClientsUrl, token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (string.Empty, string.Empty, 0);

        foreach (var category in root.EnumerateObject())
        {
            if (!category.Name.Contains("frpc", StringComparison.OrdinalIgnoreCase)) continue;
            if (category.Value.ValueKind != JsonValueKind.Object) continue;
            if (!category.Value.TryGetProperty("archs", out var archs) || archs.ValueKind != JsonValueKind.Object) continue;

            foreach (var arch in archs.EnumerateObject())
            {
                var name = arch.Name.ToLowerInvariant();
                if (!name.Contains("windows")) continue;
                if (!name.Contains("amd64") && !name.Contains("x86_64") && !name.Contains("x64")) continue;

                return (
                    Read(arch.Value, "url"),
                    Read(arch.Value, "hash"),
                    ReadLong(arch.Value, "size"));
            }
        }

        return (string.Empty, string.Empty, 0);
    }

    private static string Read(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : 0;

    // ————— 启停 —————

    /// <summary>启动隧道。返回失败说明，不抛异常。</summary>
    public SakuraResult<bool> Start(string accessKey, int tunnelId)
    {
        if (IsRunning) return SakuraResult<bool>.Fail(Loc.T("隧道已经在运行"));

        if (string.IsNullOrWhiteSpace(accessKey)) return SakuraResult<bool>.Fail(Loc.T("请先填写樱花FRP 访问密钥"));
        if (tunnelId <= 0) return SakuraResult<bool>.Fail(Loc.T("隧道 ID 无效"));
        if (!File.Exists(FrpcPath)) return SakuraResult<bool>.Fail(Loc.T("还没有准备好 frpc，请先点「准备 frpc」"));

        try
        {
            lock (_gate)
            {
                _output.Clear();
                DomainAddress = string.Empty;
                IpAddress = string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = FrpcPath,
                WorkingDirectory = ToolDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // -f 让 frpc 直接去服务器拉配置；不加 -w 就不会把访问密钥写进 frpc.ini
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add($"{accessKey.Trim()}:{tunnelId}");
            psi.ArgumentList.Add("-n");

            LogLine(Loc.F("正在启动 frpc（隧道 {0}）…", tunnelId));

            _process = Process.Start(psi);
            if (_process is null) return SakuraResult<bool>.Fail(Loc.T("frpc 启动失败"));

            _ = ReadAsync(_process.StandardOutput);
            _ = ReadAsync(_process.StandardError);

            return SakuraResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return SakuraResult<bool>.Fail(Loc.F("启动 frpc 失败：{0}", ex.Message));
        }
    }

    public void Stop()
    {
        var process = _process;
        _process = null;

        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // 本类里 Log 是事件名，静态日志器得写全名
            HMOL.Core.Logging.Log.Warn(Loc.F("结束 frpc 失败：{0}", ex.Message));
        }
        finally
        {
            process.Dispose();
        }

        LogLine(Loc.T("隧道已停止"));
    }

    private async Task ReadAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimEnd();
                if (line.Length == 0) continue;

                lock (_gate)
                {
                    _output.Add(line);
                    if (_output.Count > 300) _output.RemoveRange(0, _output.Count - 200);
                }

                if (line.Contains("隧道启动成功")) Started?.Invoke();

                foreach (var address in ExtractAddresses(line)) ReportAddress(address);

                // frpc 的启动横幅（版本号、赞助信息）对用户没意义，过滤掉
                if (IsNoise(line)) continue;

                LogLine(line);
            }
        }
        catch (Exception ex)
        {
            HMOL.Core.Logging.Log.Warn(Loc.F("读取 frpc 输出失败：{0}", ex.Message));
        }
    }

    private static bool IsNoise(string line)
        => line.Contains("sakurafrp.com", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Sponsor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从一行日志里抠出连接地址。樱花FRP 的日志会用 &gt;&gt;...&lt;&lt; 包住地址，
    /// 例如「使用 &gt;&gt;frp-xxx.com:10086&lt;&lt; 连接你的隧道」，所以优先认这个标记。
    /// </summary>
    private static IEnumerable<string> ExtractAddresses(string line)
    {
        foreach (Match match in BracketedAddress.Matches(line))
        {
            var value = match.Groups[1].Value.Trim();
            if (value.Contains(':')) yield return value;
        }
    }

    /// <summary>记下一个连接地址：按主机部分是 IP 还是域名分开放，重复的丢弃。</summary>
    private void ReportAddress(string address)
    {
        var isIp = IsIpAddress(address);

        lock (_gate)
        {
            if (isIp)
            {
                if (string.Equals(IpAddress, address, StringComparison.OrdinalIgnoreCase)) return;
                IpAddress = address;
            }
            else
            {
                if (string.Equals(DomainAddress, address, StringComparison.OrdinalIgnoreCase)) return;
                DomainAddress = address;
            }
        }

        LogLine(Loc.F("连接地址（{0}）：{1}", (isIp ? Loc.T("IP") : Loc.T("域名")), address));
        AddressesUpdated?.Invoke();
    }

    /// <summary>形如 114.51.4.191:10086 视为 IP 地址，其余当域名。</summary>
    private static bool IsIpAddress(string address)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0) return false;

        return System.Net.IPAddress.TryParse(address[..colon], out _);
    }

    private void LogLine(string message) => Log?.Invoke(Loc.F("[樱花FRP] {0}", message));
}
