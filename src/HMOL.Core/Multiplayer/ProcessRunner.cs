using System.Diagnostics;
using System.Globalization;
using System.Text;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 「跑一次就退出」的外部命令调用（easytier-cli / tasklist / netsh / powershell 等）。
///
/// <para>
/// 编码约定（实测踩过坑）：组网组件（easytier / n2n）输出 UTF-8，直接按 UTF-8 读；
/// Windows 自带命令的输出编码取决于控制台代码页 —— 装了「使用 Unicode UTF-8 提供全球语言支持」
/// 或 chcp 65001 的机器上 netsh 输出的是 UTF-8 中文，按 ANSI 读会全变乱码（防火墙状态因此永远读不出来）。
/// 所以不传编码时统一「先按严格 UTF-8 试，失败再退回系统 ANSI 代码页」，两种机器都能读对。
/// </para>
/// </summary>
internal static class ProcessRunner
{
    /// <summary>组网组件输出编码。</summary>
    public static Encoding EngineEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Windows 自带命令输出的兜底编码（系统 ANSI 代码页，通常为 GBK）。</summary>
    public static Encoding SystemEncoding { get; } = ResolveSystemEncoding();

    /// <summary>严格 UTF-8：遇到非法字节序列会抛异常，用于判断「是不是 UTF-8」。</summary>
    private static Encoding StrictUtf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>命令执行结果。Started=false 表示进程都没能起来（Output 里是原因）。</summary>
    public sealed record Result(bool Started, int ExitCode, string Output, bool TimedOut)
    {
        public bool Ok => Started && !TimedOut && ExitCode == 0;

        /// <summary>输出第一行，供界面直接展示。</summary>
        public string FirstLine(int maxLength = 160)
        {
            var line = Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            return line.Length <= maxLength ? line : line[..maxLength] + "…";
        }
    }

    /// <summary>
    /// 执行命令并等待退出；超时会结束该进程并返回 TimedOut=true。
    /// <paramref name="encoding"/> 为 null 时按「严格 UTF-8 → 系统 ANSI」自动判定。
    ///
    /// <para>
    /// <paramref name="includeStderr"/> 默认 true（标准输出为空时用标准错误兜底报原因）。
    /// 但 PowerShell 会把「进度记录」以 CLIXML 编码写到标准错误，混进来会把解析结果整个污染
    /// （实测：<c>Get-NetAdapter</c> 首次自动加载模块时 stderr 是一大段 <c>#&lt; CLIXML</c>，
    /// 于是「TAP 网卡 IP 列表」会返回这段 XML 当 IP、残留进程清理会把 XML 里的数字当 PID）。
    /// PowerShell 调用一律传 false，只读标准输出（旧版 Python 也是只读 stdout）。
    /// </para>
    /// </summary>
    public static async Task<Result> RunAsync(
        string exe,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Encoding? encoding = null,
        string? workingDirectory = null,
        bool includeStderr = true)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };

            process = Process.Start(startInfo);
            if (process is null) return new Result(false, -1, Loc.F("无法启动进程：{0}", exe), false);

            // 直接读原始字节再自己解码，避免 StreamReader 提前用错编码把中文变成不可逆的乱码
            var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
            var stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消要原样抛出，只有自身超时才当作「超时」处理
                if (cancellationToken.IsCancellationRequested) throw;

                timedOut = true;
                TryKill(process);
            }

            var output = Merge(
                Decode(await SafeAsync(stdoutTask), encoding),
                Decode(await SafeAsync(stderrTask), encoding),
                includeStderr);

            return new Result(true, timedOut ? -1 : process.ExitCode, output, timedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, -1, ex.Message, false);
        }
        finally
        {
            try { process?.Dispose(); }
            catch { /* 释放失败无所谓 */ }
        }
    }

    /// <summary>
    /// 执行一段 PowerShell 脚本。用 <c>-EncodedCommand</c>（Base64/UTF-16LE）传脚本，
    /// 彻底绕开引号与花括号的转义问题；脚本里可以放心使用双引号与 <c>{ }</c>。
    /// 只取标准输出，避免 stderr 上的 CLIXML 进度记录污染结果。
    /// </summary>
    public static Task<Result> RunPowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            timeout,
            cancellationToken,
            includeStderr: false);
    }

    /// <summary>执行 PowerShell 脚本并返回去掉首尾空白的标准输出（失败返回空串）。</summary>
    public static async Task<string> RunPowerShellTextAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunPowerShellAsync(script, timeout, cancellationToken).ConfigureAwait(false);
        return result.Started ? result.Output.Trim() : string.Empty;
    }

    /// <summary>结束进程（含子进程）。</summary>
    public static void TryKill(Process? process)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("结束外部进程失败：{0}", ex.Message));
        }
    }

    private static async Task<byte[]> SafeAsync(Task<byte[]> read)
    {
        try { return await read.ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取外部进程输出失败：{0}", ex.Message));
            return [];
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>解码：显式编码直接用；未指定时先按严格 UTF-8 试，失败退回系统 ANSI 代码页。</summary>
    private static string Decode(byte[] bytes, Encoding? encoding)
    {
        if (bytes.Length == 0) return string.Empty;
        if (encoding is not null) return encoding.GetString(bytes);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return SystemEncoding.GetString(bytes);
        }
    }

    /// <summary>
    /// 合成最终输出：<paramref name="includeStderr"/> 为 true 时把标准错误接在标准输出后面
    /// （命令失败而 stdout 为空时用它兜底报原因），并去掉 PowerShell 的 CLIXML 进度噪声。
    /// </summary>
    private static string Merge(string stdout, string stderr, bool includeStderr)
    {
        var text = new StringBuilder(stdout ?? string.Empty);

        if (includeStderr && !string.IsNullOrWhiteSpace(stderr)) text.Append(stderr);

        return StripClixml(text.ToString()).Trim();
    }

    /// <summary>
    /// 去掉 PowerShell 写在输出里的 CLIXML 片段（进度/错误记录，形如 <c>#&lt; CLIXML …</c>）。
    /// 保留标记之前的内容，避免解析方把 XML 当数据。
    /// </summary>
    private static string StripClixml(string text)
    {
        var index = text.IndexOf("#< CLIXML", StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    private static Encoding ResolveSystemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("取系统 ANSI 代码页失败，外部命令输出将按 UTF-8 读取：{0}", ex.Message));
            return Encoding.UTF8;
        }
    }
}
