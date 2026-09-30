using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Appearance;

/// <summary>壁纸包导入结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record WallpaperImport(bool Ok, string Message, BackgroundKind Kind, string FileName);

/// <summary>
/// Wallpaper Engine 壁纸包（<c>.pkg</c> / <c>.mpkg</c>）的导入。
/// 流程：把内嵌的第三方解包工具 RePKG.exe 落到缓存目录 → 调它把包解到缓存目录 →
/// 从解出的文件里挑一个可用的视频 / 图片，收进 <see cref="Paths.Backgrounds"/> 当背景素材。
/// 对应旧版 HMOL_qt.py 的 _ensure_repkg_exe（第 11991 行）、_settings_import_mpkg（第 12030 行）
/// 与 _find_bg_media_in_dir（第 12015 行）。
///
/// RePKG 是第三方程序（NotScuffed/RePKG 0.2.2），这里只调用它：不改写、不反编译、不依赖它的内部格式。
/// </summary>
public static class WallpaperPackageService
{
    /// <summary>内嵌工具的扩展名，用来在程序集资源里定位它。</summary>
    private const string ToolFileName = "RePKG.exe";

    private static readonly string[] PackageExtensions = [".pkg", ".mpkg"];

    private static readonly string[] PackageVideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv"];

    private static readonly string[] PackageImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    /// <summary>解包最长等 5 分钟，超时就掐掉（旧版同样是 300 秒）。</summary>
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(300);

    /// <summary>工具落盘位置：<c>Data\Cache\RePKG.exe</c>。</summary>
    public static string ToolPath => Path.Combine(Paths.Cache, ToolFileName);

    /// <summary>
    /// 读 RePKG 输出用的编码。它是 .NET Framework 程序，输出重定向后按系统 ANSI 代码页写字节，
    /// 用 UTF-8 去读会把中文错误信息读成乱码（实测如此）。
    /// </summary>
    private static Encoding ConsoleEncoding { get; } = ResolveConsoleEncoding();

    private static Encoding ResolveConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex)
        {
            Log.Warn($"取系统 ANSI 代码页失败，RePKG 的输出将按 UTF-8 读取：{ex.Message}");
            return Encoding.UTF8;
        }
    }

    /// <summary>是不是壁纸包。</summary>
    public static bool IsPackage(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           PackageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ————— 工具落盘 —————

    /// <summary>
    /// 确保缓存目录里有可用的 RePKG.exe，返回它的路径；失败返回 null 并记日志。
    /// 已有文件且大小一致就不再写盘，避免每次导入都往磁盘写 2 MB。
    /// </summary>
    public static string? EnsureTool()
    {
        try
        {
            var payload = ReadEmbeddedTool();
            if (payload is null)
            {
                Log.Error("内嵌的 RePKG.exe 资源缺失，无法导入壁纸包");
                return null;
            }

            Directory.CreateDirectory(Paths.Cache);

            if (File.Exists(ToolPath) && new FileInfo(ToolPath).Length == payload.Length) return ToolPath;

            File.WriteAllBytes(ToolPath, payload);
            Log.Info($"已释放壁纸包解包工具：{ToolPath}（{payload.Length} 字节）");

            return ToolPath;
        }
        catch (Exception ex)
        {
            Log.Error("释放 RePKG.exe 失败", ex);
            return null;
        }
    }

    private static byte[]? ReadEmbeddedTool()
    {
        var assembly = typeof(WallpaperPackageService).Assembly;

        // 资源名由 MSBuild 生成，形如 HMOL.Core.Resources.RePKG.exe；按后缀找，改了目录也不用同步改这里
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(ToolFileName, StringComparison.OrdinalIgnoreCase));

        if (name is null) return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ————— 导入 —————

    /// <summary>
    /// 导入壁纸包：解包 → 找素材 → 收进素材目录。整个过程在后台线程跑，不阻塞界面。
    /// </summary>
    public static async Task<WallpaperImport> ImportAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return Fail("壁纸包文件不存在。");

        if (!IsPackage(packagePath))
            return Fail($"只支持 {string.Join(" / ", PackageExtensions)} 格式的壁纸包。");

        var tool = EnsureTool();
        if (tool is null)
        {
            return Fail("无法释放内嵌的解包工具 RePKG.exe（程序文件可能不完整），本次导入未执行。");
        }

        var displayName = Path.GetFileName(packagePath);
        var extractDir = Path.Combine(Paths.Cache, "wallpaper", BuildCacheFolderName(displayName));

        // 旧版会先把 .mpkg 复制成临时 .pkg：早期 RePKG 只看扩展名，.mpkg 会被拒。
        // 0.2.2 的用法说明里输入是任意路径，但沿用旧版做法更稳妥，代价只是多复制一份。
        string input = packagePath;
        string? tempPkg = null;

        try
        {
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);

            if (Path.GetExtension(packagePath).Equals(".mpkg", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Paths.Temp);
                tempPkg = Path.Combine(Paths.Temp, $"hmol_{Guid.NewGuid():N}.pkg");
                File.Copy(packagePath, tempPkg, overwrite: true);
                input = tempPkg;
            }

            var (exitCode, output) = await RunExtractAsync(tool, input, extractDir, cancellationToken)
                .ConfigureAwait(false);

            if (exitCode is null) return Fail("解包超时（超过 5 分钟），已中止。");

            if (exitCode != 0)
            {
                // 输出常常是一整段异常堆栈：界面只放第一行，完整内容写进日志
                Log.Warn($"RePKG 解包失败（退出码 {exitCode}）：{output}");

                return Fail($"RePKG 解包失败（退出码 {exitCode}）：{FirstLine(output)}");
            }

            var media = FindBackgroundMedia(extractDir);
            if (media is null) return Fail("解包完成，但包内没有可用的视频 / 图片文件，无法作为背景。");

            Log.Info($"壁纸包已解出可用素材：{media}");

            var imported = BackgroundService.ImportFile(media, moveSource: true);
            if (!imported.Ok) return new WallpaperImport(false, imported.Message, BackgroundKind.None, string.Empty);

            return new WallpaperImport(true,
                $"已导入壁纸包 {displayName}，当前背景：{Path.GetFileName(media)}（{BackgroundService.DescribeKind(imported.Kind)}）",
                imported.Kind, imported.FileName);
        }
        catch (OperationCanceledException)
        {
            return Fail("导入已取消。");
        }
        catch (Exception ex)
        {
            Log.Error($"导入壁纸包失败：{packagePath}", ex);
            return Fail($"导入壁纸包失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(tempPkg);

            // 解包目录只是中转：素材已经收进 Data\backgrounds，剩下的贴图等没用了
            TryDeleteDirectory(extractDir);
        }

        static WallpaperImport Fail(string message) => new(false, message, BackgroundKind.None, string.Empty);
    }

    /// <summary>
    /// 调用 RePKG。返回退出码与合并后的输出；超时返回 (null, 输出)。
    /// 参数形式：<c>RePKG.exe extract -o &lt;输出目录&gt; --overwrite &lt;输入文件&gt;</c>（已用 --help 核对）。
    /// </summary>
    private static async Task<(int? ExitCode, string Output)> RunExtractAsync(
        string tool, string input, string outputDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(tool) ?? Paths.Cache,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
            Arguments = $"extract -o \"{outputDirectory}\" --overwrite \"{input}\""
        };

        using var process = Process.Start(startInfo);
        if (process is null) return (null, "进程启动失败：Process.Start 返回空。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 只有超时才走这里；用户主动取消要原样抛出去，由调用方区分两种提示
            TryKill(process);
            return (null, await ReadOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false));
        }

        var output = await ReadOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
        return (process.ExitCode, output);
    }

    private static async Task<string> ReadOutputAsync(Task<string> stdout, Task<string> stderr)
    {
        var text = new StringBuilder();

        try { text.Append(await stdout.ConfigureAwait(false)); }
        catch (Exception ex) { Log.Warn($"读取 RePKG 标准输出失败：{ex.Message}"); }

        try { text.Append(await stderr.ConfigureAwait(false)); }
        catch (Exception ex) { Log.Warn($"读取 RePKG 标准错误失败：{ex.Message}"); }

        return text.ToString().Trim();
    }

    /// <summary>输出可能很长（多半是异常堆栈），只取第一行做界面提示。</summary>
    private static string FirstLine(string output, int maxLength = 160)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return "RePKG 没有输出任何信息";

        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }

    /// <summary>
    /// 在解包目录里挑一个能当背景的文件：先找视频，再找图片 / 动图。
    /// 与旧版 _find_bg_media_in_dir 的偏好一致（视频优先）。
    /// </summary>
    private static string? FindBackgroundMedia(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files.FirstOrDefault(IsVideo) ?? files.FirstOrDefault(IsImage);

        static bool IsVideo(string file) =>
            PackageVideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

        static bool IsImage(string file) =>
            PackageImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());
    }

    /// <summary>缓存目录名：把包名洗干净再加 8 位摘要，避免同名包互相覆盖、也避免非法字符。</summary>
    private static string BuildCacheFolderName(string displayName)
    {
        var raw = Path.GetFileNameWithoutExtension(displayName);
        var safe = new string(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        if (safe.Length > 24) safe = safe[..24];

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(displayName)))[..8];
        return $"{safe}_{hash}".ToLowerInvariant();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"结束 RePKG 进程失败：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn($"删除临时文件失败 {path}：{ex.Message}"); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理解包目录失败 {path}：{ex.Message}");
        }
    }
}
