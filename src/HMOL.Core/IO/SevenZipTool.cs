using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HMOL.Core.App;

namespace HMOL.Core.IO;

/// <summary>
/// 随包分发的 7-Zip 独立命令行（<c>runtime\7zip\7za.exe</c>）的调用封装，用于**创建** 7z 压缩包。
///
/// 为什么必须借外部程序：项目已引的 SharpCompress 只能**写** zip / tar / gz，没有 7z 写入能力；
/// 7z 的容器格式本身也没有可用的托管实现。rar 更极端——专有格式，没有任何开源库能写，
/// 因此 rar 只支持导入（解压）不支持导出。
/// </summary>
public static class SevenZipTool
{
    /// <summary>组件相对 exe 目录的位置。</summary>
    private const string RelativePath = "runtime/7zip/7za.exe";

    /// <summary>组件缺失时的提示文案。</summary>
    public const string MissingMessage = "缺少 7-Zip 组件（runtime\\7zip\\7za.exe），请重新解压完整发行包";

    /// <summary>把 <see cref="System.IO.Compression.CompressionLevel"/> 折算成 7-Zip 的 -mx 等级。</summary>
    public const int LevelStore = 0;
    public const int LevelFastest = 1;
    public const int LevelNormal = 5;
    public const int LevelUltra = 9;

    private static readonly Lazy<string?> Resolved = new(Resolve, isThreadSafe: true);

    private static readonly Regex PercentPattern = new(@"(\d{1,3})%", RegexOptions.Compiled);

    /// <summary>组件是否可用。</summary>
    public static bool IsAvailable => Resolved.Value is not null;

    /// <summary>组件绝对路径，不可用时为 null。</summary>
    public static string? ExePath => Resolved.Value;

    /// <summary>
    /// 把 <paramref name="sourceDirectory"/> 的**顶层条目**原样打包成 7z。
    /// 调用方负责按归档结构准备目录（例如实例导出会准备 <c>instance_info.json</c> 与 <c>game_files</c>）。
    /// 同步阻塞直到 7-Zip 退出；调用方应当已经在后台线程上（实例导出正是如此）。
    /// </summary>
    public static (bool Ok, string Error) Create(
        string sourceDirectory, string archivePath, int level,
        IProgress<double>? progress, CancellationToken token)
    {
        var exe = ExePath;
        if (exe is null) return (false, MissingMessage);

        if (!Directory.Exists(sourceDirectory)) return (false, $"待打包目录不存在：{sourceDirectory}");

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(sourceDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception ex)
        {
            return (false, $"无法读取待打包目录：{ex.Message}");
        }

        if (entries.Length == 0) return (false, "待打包目录为空");

        var startInfo = new ProcessStartInfo(exe)
        {
            // 工作目录设为源目录，归档内条目即为相对路径（不含盘符与临时目录）
            WorkingDirectory = sourceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add(archivePath);
        foreach (var entry in entries) startInfo.ArgumentList.Add(entry);
        startInfo.ArgumentList.Add("-t7z");
        startInfo.ArgumentList.Add($"-mx={Math.Clamp(level, 0, 9)}");
        startInfo.ArgumentList.Add("-y");         // 不询问
        startInfo.ArgumentList.Add("-bsp1");      // 进度写 stdout
        startInfo.ArgumentList.Add("-bse1");      // 错误写 stderr
        startInfo.ArgumentList.Add("-sccUTF-8");  // 控制台输出用 UTF-8，中文文件名不乱码

        var output = new StringBuilder();
        var gate = new object();

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;

            lock (gate)
            {
                output.AppendLine(Clean(args.Data));
                ReportProgress(args.Data, progress);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.Data)) return;

            lock (gate) output.AppendLine(Clean(args.Data));
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, $"无法启动 7-Zip：{ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 轮询等待，顺便处理取消（同步 API 没有 WaitForExitAsync 那种带 token 的重载）
        while (!process.WaitForExit(200))
        {
            if (!token.IsCancellationRequested) continue;

            TryKill(process);
            token.ThrowIfCancellationRequested();
        }

        // WaitForExit 返回后输出流可能还有残余，再等一次确保错误信息拿全
        process.WaitForExit();

        if (process.ExitCode is 0 or 1) return (true, string.Empty);   // 7-Zip：1 是"有警告但成功"

        return (false, $"7-Zip 返回错误码 {process.ExitCode}：{LastLines(output.ToString())}");
    }

    /// <summary>解析 7-Zip 的进度行（形如 " 45% 12 + file.txt"）。</summary>
    private static void ReportProgress(string line, IProgress<double>? progress)
    {
        if (progress is null) return;

        var match = PercentPattern.Match(line);
        if (!match.Success) return;

        if (!int.TryParse(match.Groups[1].Value, out var percent)) return;

        progress.Report(Math.Clamp(percent, 0, 100) / 100d);
    }

    private static string Clean(string line)
        => line.Replace("\b", string.Empty).Replace('\r', ' ').Trim();

    private static string LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length <= 3 ? string.Join(" / ", lines) : string.Join(" / ", lines[^3..]);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 杀不掉只能放弃，异常本身已经带出去了
        }
    }

    /// <summary>
    /// 定位组件：优先用程序记录的 exe 目录，其次用进程自身的 exe 目录。
    /// 单文件发布下 <see cref="AppContext.BaseDirectory"/> 在 .NET 5+ 就是 exe 目录，两条兜底是为了稳妥。
    /// </summary>
    private static string? Resolve()
    {
        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) continue;

            try
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, RelativePath));
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 路径非法就试下一个
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBaseDirectories()
    {
        yield return Paths.ExecutableDirectory;

        var processPath = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);

        if (!string.IsNullOrWhiteSpace(directory)) yield return directory!;
    }
}
