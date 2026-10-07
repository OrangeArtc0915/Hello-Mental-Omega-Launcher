using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>收到一个待确认的文件（由界面决定保存到哪里）。</summary>
public sealed record IncomingFile(string Name, long Size, string From, string FromIp);

/// <summary>发送进度。</summary>
public sealed record FileSendProgress(long Done, long Total)
{
    /// <summary>0~1 的完成比例。</summary>
    public double Fraction => Total <= 0 ? 0 : (double)Done / Total;
}

/// <summary>发送结果。Message 是可以直接展示给用户的中文。</summary>
public sealed record FileSendResult(bool Ok, string Message);

/// <summary>
/// 在线对端文件传输。对应旧版 filetrans.py：后台常驻 TCP 监听接收（默认端口 17800），
/// 发送方直连对端虚拟 IP；单文件，默认上限 500MB。
///
/// <para>
/// <b>协议互通：</b>首行是 JSON 头部（<c>v/t/name/size/from</c>）加一个 <c>\n</c>，
/// 接收方回一行 <c>OK</c> / <c>REJECT</c>，随后是文件名+大小的二进制流。与旧版逐字节一致。
/// </para>
/// </summary>
public sealed class FileTransfer : IAsyncDisposable
{
    /// <summary>文件传输 TCP 端口（filetrans.py:14 <c>FILE_PORT</c>，必须一致才能互通）。</summary>
    public const int FilePort = 17800;

    /// <summary>单文件上限 500MB（filetrans.py:15 <c>MAX_FILE_SIZE</c>）。</summary>
    public const long MaxFileSize = 500L * 1024 * 1024;

    /// <summary>传输分块大小（filetrans.py:16 <c>CHUNK</c>）。</summary>
    private const int ChunkSize = 1024 * 1024;

    /// <summary>套接字读写超时（旧版接收侧 20 秒 filetrans.py:119、发送侧 120 秒 filetrans.py:220）。</summary>
    private const int ReceiveTimeoutSeconds = 20;
    private const int SendTimeoutSeconds = 120;

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly object _lock = new();
    private readonly int _port;
    private readonly long _maxSize;
    private readonly Action<string>? _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    /// <param name="port">监听端口，默认 17800。</param>
    /// <param name="maxSize">单文件上限。</param>
    /// <param name="log">日志回调。</param>
    public FileTransfer(int port = FilePort, long maxSize = MaxFileSize, Action<string>? log = null)
    {
        _port = port;
        _maxSize = maxSize;
        _log = log;
    }

    /// <summary>
    /// 收到文件请求时回调，返回保存路径（已存在同名文件时由本类自动改名）；返回空表示拒收。
    /// 回调在后台线程执行，界面需自行切线程弹确认框。
    /// </summary>
    public Func<IncomingFile, string?>? IncomingFileHandler { get; set; }

    /// <summary>文件接收完成（参数为最终保存路径）。</summary>
    public event Action<string>? FileReceived;

    /// <summary>是否正在监听。</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _listener is not null; }
    }

    /// <summary>开始后台监听。端口被占用等失败会返回 false 并写日志。停止后可以再次调用。</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_listener is not null) return true;
        }

        var previous = _cts;
        previous?.Dispose();

        var cts = new CancellationTokenSource();
        TcpListener listener;

        try
        {
            listener = new TcpListener(IPAddress.Any, _port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start(8);
        }
        catch (Exception ex)
        {
            cts.Dispose();
            LogLine(Loc.F("文件传输监听端口 {0} 启动失败: {1}", _port, ex.Message));
            return false;
        }

        lock (_lock)
        {
            _listener = listener;
            _cts = cts;
        }

        _acceptTask = Task.Run(() => AcceptLoopAsync(cts.Token));
        LogLine(Loc.F("文件传输后台监听已启动 (TCP {0})", _port));
        _ = TryOpenFirewallAsync();

        return true;
    }

    /// <summary>停止监听并等待接收线程退出（可再次 Start）。</summary>
    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;

        lock (_lock)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }

        try { cts?.Cancel(); }
        catch { /* 已释放 */ }

        try { listener?.Stop(); }
        catch { /* 已停止 */ }

        var accept = _acceptTask;
        _acceptTask = null;

        if (accept is not null)
        {
            try { await accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 线程退出即可 */ }
        }

        cts?.Dispose();
    }

    /// <summary>发送单文件到对端虚拟 IP。</summary>
    public async Task<FileSendResult> SendAsync(
        string? ip,
        string? path,
        string? fromName = null,
        IProgress<FileSendProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new FileSendResult(false, Loc.T("文件不存在"));

        var info = new FileInfo(path);
        var size = info.Length;
        var name = info.Name;

        if (size <= 0) return new FileSendResult(false, Loc.T("空文件无法发送"));

        if (size > _maxSize)
            return new FileSendResult(false, Loc.F("文件超过 {0}MB 上限, 请压缩后再试", _maxSize / 1048576));

        using var client = new TcpClient();

        try
        {
            using var connectSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectSource.CancelAfter(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(ip, _port, connectSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new FileSendResult(false, Loc.T("已取消发送"));
        }
        catch (Exception ex)
        {
            return new FileSendResult(false, Loc.F("无法连接 {0}:{1} ({2}), 请确认对方在线且防火墙已放行", ip, _port, ex.Message));
        }

        try
        {
            using var stream = client.GetStream();

            var header = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["v"] = 1,
                    ["t"] = "file",
                    ["name"] = name,
                    ["size"] = size,
                    ["from"] = fromName ?? string.Empty
                },
                RoomChat.JsonOptions);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(header + "\n"), cancellationToken).ConfigureAwait(false);

            var reply = await ReadLineAsync(stream, 64, cancellationToken, SendTimeoutSeconds).ConfigureAwait(false);
            if (reply is null || reply.Trim() != "OK") return new FileSendResult(false, Loc.T("对方拒绝了接收"));

            var buffer = new byte[ChunkSize];
            long sent = 0;

            await using (var file = File.OpenRead(path))
            {
                while (sent < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sent += read;

                    progress?.Report(new FileSendProgress(sent, size));
                }
            }

            return new FileSendResult(true, Loc.F("发送完成: {0} ({1})", name, FormatSize(size)));
        }
        catch (OperationCanceledException)
        {
            return new FileSendResult(false, Loc.T("已取消发送"));
        }
        catch (Exception ex)
        {
            return new FileSendResult(false, Loc.F("发送失败: {0}", ex.Message));
        }
    }

    /// <summary>清洗文件名：只取末段、去掉非法字符、限长 255。</summary>
    public static string SanitizeName(string? name)
    {
        var text = (name ?? string.Empty).Replace('\\', '/');
        var index = text.LastIndexOf('/');
        if (index >= 0) text = text[(index + 1)..];

        text = text.Trim();

        foreach (var invalid in InvalidNameChars) text = text.Replace(invalid, '_');

        text = new string(text.Where(c => c >= 0x20 && c != (char)0x7f).ToArray());

        return text.Length > 255 ? text[..255] : text;
    }

    /// <summary>同名文件已存在时自动加 (1) (2) 后缀。</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>把字节数格式化成展示文本。</summary>
    public static string FormatSize(long size)
        => size >= 1048576 ? $"{size / 1048576.0:0.0} MB" : $"{size / 1024} KB";

    // ————— 接收 —————

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpListener? listener;

            lock (_lock) listener = _listener;

            if (listener is null) break;

            Socket socket;

            try
            {
                socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLine(Loc.F("文件传输监听异常: {0}", ex.Message));
                break;
            }

            _ = Task.Run(() => ReceiveWorkerAsync(socket), CancellationToken.None);
        }
    }

    private async Task ReceiveWorkerAsync(Socket socket)
    {
        var peerIp = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
        string? savePath = null;

        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);

            var header = await ReadLineAsync(stream, 8192, CancellationToken.None).ConfigureAwait(false);
            if (header is null) return;

            using var document = JsonDocument.Parse(header);
            var root = document.RootElement;

            var name = SanitizeName(RoomChat.GetString(root, "name"));
            var size = RoomChat.GetLong(root, "size", 0);
            var from = Truncate(RoomChat.GetString(root, "from"), 32);

            if (name.Length == 0 || size <= 0)
            {
                await ReplyAsync(stream, "REJECT").ConfigureAwait(false);
                return;
            }

            if (size > _maxSize)
            {
                await ReplyAsync(stream, "REJECT").ConfigureAwait(false);
                LogLine(Loc.F("拒绝接收 {0}: 超过 {1}MB 上限", name, _maxSize / 1048576));
                return;
            }

            try
            {
                savePath = IncomingFileHandler?.Invoke(new IncomingFile(name, size, from, peerIp));
            }
            catch (Exception ex)
            {
                LogLine(Loc.F("接收确认回调异常: {0}", ex.Message));
                savePath = null;
            }

            if (string.IsNullOrWhiteSpace(savePath))
            {
                await ReplyAsync(stream, "REJECT").ConfigureAwait(false);
                LogLine(Loc.F("已拒绝来自 {0} 的文件 {1}", (from.Length > 0 ? from : peerIp), name));
                return;
            }

            savePath = UniquePath(savePath);

            if (!await ReplyAsync(stream, "OK").ConfigureAwait(false)) return;

            var target = savePath;
            long received = 0;
            var buffer = new byte[ChunkSize];

            await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (received < size)
                {
                    var read = await ReceiveAsync(stream, buffer, (int)Math.Min(ChunkSize, size - received)).ConfigureAwait(false);
                    if (read <= 0) break;

                    await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    received += read;
                }
            }

            if (received == size)
            {
                LogLine(Loc.F("已接收文件 {0} ({1})", Path.GetFileName(target), FormatSize(size)));

                try { FileReceived?.Invoke(target); }
                catch (Exception ex) { Log.Warn(Loc.F("接收完成回调异常：{0}", ex.Message)); }
            }
            else
            {
                TryDelete(target);
                LogLine(Loc.F("接收中断: {0} 仅收到 {1}/{2} 字节, 已删除", name, received, size));
            }
        }
        catch (Exception ex)
        {
            LogLine(Loc.F("接收失败: {0}", ex.Message));

            // 中途异常（网络断开/磁盘错误/对方中断）残留的残缺文件一律清理
            if (!string.IsNullOrEmpty(savePath)) TryDelete(savePath);
        }
    }

    private static async Task<int> ReceiveAsync(NetworkStream stream, byte[] buffer, int count)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(ReceiveTimeoutSeconds));

        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, count), timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>读一行（\n 结束），最多 <paramref name="limit"/> 字节；超时或对端关闭返回 null。</summary>
    private static async Task<string?> ReadLineAsync(
        NetworkStream stream,
        int limit,
        CancellationToken cancellationToken,
        double timeoutSeconds = ReceiveTimeoutSeconds)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var buffer = new byte[limit];
        var length = 0;

        try
        {
            while (length < limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, 1), timeoutSource.Token).ConfigureAwait(false);
                if (read <= 0) break;

                if (buffer[length] == (byte)'\n') break;
                length++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (length == 0) return null;

        return Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }

    private static async Task<bool> ReplyAsync(NetworkStream stream, string text)
    {
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n")).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>尝试为本端口加一条防火墙放行规则；权限不足时给出可读提示（不静默失败）。</summary>
    private async Task TryOpenFirewallAsync()
    {
        var result = await NetworkToolkit.SetFirewallRuleAsync(
            Loc.T("HMOL 文件传输"),
            _port,
            CancellationToken.None).ConfigureAwait(false);

        if (!result.Ok) LogLine(Loc.F("自动放行防火墙未成功：{0}", result.Message));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("删除残缺文件失败 {0}：{1}", path, ex.Message));
        }
    }

    private void LogLine(string message)
    {
        Log.Info(Loc.F("[文件传输] {0}", message));

        try { _log?.Invoke(Loc.F("[文件传输] {0}", message)); }
        catch { /* 日志回调异常不影响业务 */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
