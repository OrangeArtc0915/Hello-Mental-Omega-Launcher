using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Appearance;

/// <summary>壁纸包导入结果。Ok 为 false 时 Message 是可以直接展示给用户的中文原因。</summary>
public sealed record WallpaperImport(bool Ok, string Message, BackgroundKind Kind, string FileName);

/// <summary>壁纸包里的一个可用素材。<see cref="RelativePath"/> 是相对 Data 的路径。</summary>
public sealed record WallpaperMedia(
    string RelativePath,
    string Title,
    BackgroundKind Kind,
    string PackageId,
    string PackageName,
    long Size);

/// <summary>一个已保存的壁纸包，包含原包与解出的全部可用素材。</summary>
public sealed record WallpaperPackageInfo(
    string Id,
    string Name,
    string OriginalFile,
    DateTime ImportedAt,
    IReadOnlyList<WallpaperMedia> Media);

/// <summary>写在 <c>Data\wallpapers\&lt;id&gt;\package.json</c> 里的清单。</summary>
internal sealed class WallpaperPackageManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OriginalFile { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public List<WallpaperMediaManifest> Media { get; set; } = [];
}

internal sealed class WallpaperMediaManifest
{
    public string File { get; set; } = string.Empty;
    public long Size { get; set; }
}

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

    /// <summary>解包最长等 5 分钟，超时就掐掉（旧版同样是 300 秒）。</summary>
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(300);

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
            Log.Warn(Loc.F("取系统 ANSI 代码页失败，RePKG 的输出将按 UTF-8 读取：{0}", ex.Message));
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
                Log.Error(Loc.T("内嵌的 RePKG.exe 资源缺失，无法导入壁纸包"));
                return null;
            }

            Directory.CreateDirectory(Paths.Cache);

            if (File.Exists(ToolPath) && new FileInfo(ToolPath).Length == payload.Length) return ToolPath;

            File.WriteAllBytes(ToolPath, payload);
            Log.Info(Loc.F("已释放壁纸包解包工具：{0}（{1} 字节）", ToolPath, payload.Length));

            return ToolPath;
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("释放 RePKG.exe 失败"), ex);
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
    /// 导入壁纸包：原包与解出的全部可用素材都保存到 <c>Data\wallpapers\&lt;id&gt;</c>。
    /// 导入后当前背景会指向包内第一个素材（视频优先），之后可以在设置里随时切换。
    /// </summary>
    public static async Task<WallpaperImport> ImportAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return Fail(Loc.T("壁纸包文件不存在。"));

        if (!IsPackage(packagePath))
            return Fail(Loc.F("只支持 {0} 格式的壁纸包。", string.Join(Loc.T(" / "), PackageExtensions)));

        var tool = EnsureTool();
        if (tool is null)
        {
            return Fail(Loc.T("无法释放内嵌的解包工具 RePKG.exe（程序文件可能不完整），本次导入未执行。"));
        }

        var displayName = Path.GetFileName(packagePath);
        var packageId = BuildPackageId(displayName, packagePath);
        var packageDir = Path.Combine(Paths.Wallpapers, packageId);
        var mediaDir = Path.Combine(packageDir, "media");
        var extractDir = Path.Combine(Paths.Cache, "wallpaper", packageId + "_extract");

        // 同名包重复导入：先清掉旧目录；如果旧素材正被播放占用，换个带时间戳的目录，避免影响当前背景。
        if (Directory.Exists(packageDir))
        {
            try
            {
                Directory.Delete(packageDir, recursive: true);
            }
            catch
            {
                packageId = $"{packageId}_{DateTime.Now:yyyyMMdd_HHmmss}";
                packageDir = Path.Combine(Paths.Wallpapers, packageId);
                mediaDir = Path.Combine(packageDir, "media");
                extractDir = Path.Combine(Paths.Cache, "wallpaper", packageId + "_extract");
            }
        }

        // 旧版会先把 .mpkg 复制成临时 .pkg：早期 RePKG 只看扩展名，.mpkg 会被拒。
        string input = packagePath;
        string? tempPkg = null;

        try
        {
            Directory.CreateDirectory(Paths.Wallpapers);
            Directory.CreateDirectory(mediaDir);

            var extension = Path.GetExtension(packagePath).ToLowerInvariant();
            var packageCopy = Path.Combine(packageDir, "package" + extension);

            if (!string.Equals(Path.GetFullPath(packagePath), Path.GetFullPath(packageCopy), StringComparison.OrdinalIgnoreCase))
                File.Copy(packagePath, packageCopy, overwrite: true);

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

            if (exitCode is null) return Fail(Loc.T("解包超时（超过 5 分钟），已中止。"));

            if (exitCode != 0)
            {
                Log.Warn(Loc.F("RePKG 解包失败（退出码 {0}）：{1}", exitCode, output));
                return Fail(Loc.F("RePKG 解包失败（退出码 {0}）：{1}", exitCode, FirstLine(output)));
            }

            var mediaFiles = FindAllMedia(extractDir);
            var manifest = new WallpaperPackageManifest
            {
                Id = packageId,
                Name = Path.GetFileNameWithoutExtension(displayName),
                OriginalFile = Path.GetFileName(packageCopy),
                ImportedAt = DateTime.Now
            };

            var index = 0;
            foreach (var media in mediaFiles)
            {
                var targetName = BuildMediaFileName(media, ++index);
                var targetPath = Path.Combine(mediaDir, targetName);

                File.Copy(media, targetPath, overwrite: true);

                manifest.Media.Add(new WallpaperMediaManifest
                {
                    File = targetName,
                    Size = new FileInfo(targetPath).Length
                });
            }

            File.WriteAllText(Path.Combine(packageDir, "package.json"),
                JsonSerializer.Serialize(manifest, ManifestJson));

            if (manifest.Media.Count == 0)
            {
                Log.Warn(Loc.F("壁纸包已保存，但包内没有可用的视频 / 图片：{0}", packagePath));
                return Fail(Loc.T("壁纸包已保存到 Data 文件夹，但包内没有可用的视频 / 图片，暂不能作为背景。"));
            }

            var first = manifest.Media[0];
            var kind = BackgroundService.DetectKind(first.File);
            var relativePath = $"wallpapers/{packageId}/media/{first.File}";

            Log.Info(Loc.F("壁纸包已保存：{0}（{1} 个素材）→ {2}", displayName, manifest.Media.Count, packageDir));

            return new WallpaperImport(true,
                Loc.F("已保存壁纸包 {0}（{1} 个素材），当前背景：{2}（{3}）",
                    displayName, manifest.Media.Count, first.File, BackgroundService.DescribeKind(kind)),
                kind, relativePath);
        }
        catch (OperationCanceledException)
        {
            return Fail(Loc.T("导入已取消。"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导入壁纸包失败：{0}", packagePath), ex);
            return Fail(Loc.F("导入壁纸包失败：{0}", ex.Message));
        }
        finally
        {
            TryDeleteFile(tempPkg);

            // 解包目录只是中转：可用素材已经复制进 Data\wallpapers，贴图等无关内容不保留
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
        if (process is null) return (null, Loc.T("进程启动失败：Process.Start 返回空。"));

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
        catch (Exception ex) { Log.Warn(Loc.F("读取 RePKG 标准输出失败：{0}", ex.Message)); }

        try { text.Append(await stderr.ConfigureAwait(false)); }
        catch (Exception ex) { Log.Warn(Loc.F("读取 RePKG 标准错误失败：{0}", ex.Message)); }

        return text.ToString().Trim();
    }

    /// <summary>输出可能很长（多半是异常堆栈），只取第一行做界面提示。</summary>
    private static string FirstLine(string output, int maxLength = 160)
    {
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(line)) return Loc.T("RePKG 没有输出任何信息");

        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }

    // ————— 素材库 —————

    /// <summary>枚举已保存的壁纸包（新导入的在前）。</summary>
    public static IReadOnlyList<WallpaperPackageInfo> ListPackages()
    {
        try
        {
            if (!Directory.Exists(Paths.Wallpapers)) return [];

            var result = new List<WallpaperPackageInfo>();

            foreach (var dir in Directory.EnumerateDirectories(Paths.Wallpapers))
            {
                var id = Path.GetFileName(dir);
                var manifest = ReadManifest(dir);
                var name = string.IsNullOrWhiteSpace(manifest?.Name) ? id : manifest!.Name;
                var media = new List<WallpaperMedia>();

                if (manifest?.Media is { Count: > 0 })
                {
                    foreach (var entry in manifest.Media)
                    {
                        var relative = entry.File.Replace('\\', '/');
                        var full = Path.Combine(dir, "media", relative.Replace('/', Path.DirectorySeparatorChar));

                        if (!File.Exists(full)) continue;

                        var kind = BackgroundService.DetectKind(relative);
                        if (kind == BackgroundKind.None) continue;

                        media.Add(new WallpaperMedia(
                            $"wallpapers/{id}/media/{relative}",
                            Path.GetFileName(relative),
                            kind, id, name, entry.Size));
                    }
                }
                else
                {
                    // 兼容没有 package.json 的手工目录
                    var mediaDir = Path.Combine(dir, "media");

                    if (Directory.Exists(mediaDir))
                    {
                        foreach (var file in Directory.EnumerateFiles(mediaDir, "*", SearchOption.AllDirectories)
                                     .Where(BackgroundService.IsSupportedMedia)
                                     .Where(file => !IsPreviewFile(file)))
                        {
                            var relative = Path.GetRelativePath(mediaDir, file).Replace('\\', '/');
                            media.Add(new WallpaperMedia(
                                $"wallpapers/{id}/media/{relative}",
                                Path.GetFileName(file),
                                BackgroundService.DetectKind(file),
                                id, name, new FileInfo(file).Length));
                        }
                    }
                }

                result.Add(new WallpaperPackageInfo(
                    id, name, manifest?.OriginalFile ?? string.Empty,
                    manifest?.ImportedAt ?? Directory.GetCreationTime(dir), media));
            }

            return result.OrderByDescending(package => package.ImportedAt).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("枚举壁纸包失败：{0}", ex.Message));
            return [];
        }
    }

    /// <summary>把全部壁纸包素材摊平，给设置页的切换列表用。</summary>
    public static IReadOnlyList<WallpaperMedia> ListMedia()
        => ListPackages()
            .SelectMany(package => package.Media)
            .OrderBy(media => media.PackageName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(media => media.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>删除一个壁纸包及其全部素材。正在播放的视频被占用时返回 false。</summary>
    public static bool DeletePackage(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) ||
            packageId.Contains("..", StringComparison.Ordinal) ||
            packageId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var dir = Path.Combine(Paths.Wallpapers, packageId);

        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Log.Info(Loc.F("已删除壁纸包：{0}", packageId));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("删除壁纸包失败（可能正被占用）：{0}", ex.Message));
            return false;
        }
    }

    private static WallpaperPackageManifest? ReadManifest(string packageDir)
    {
        var path = Path.Combine(packageDir, "package.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<WallpaperPackageManifest>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取壁纸包清单失败：{0}", ex.Message));
            return null;
        }
    }

    /// <summary>在解包目录里找全部可当背景的文件，视频优先，排除预览图。</summary>
    private static IReadOnlyList<string> FindAllMedia(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(BackgroundService.IsSupportedMedia)
            .Where(file => !IsPreviewFile(file))
            .OrderBy(file => BackgroundService.DetectKind(file) == BackgroundKind.Video ? 0 : 1)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPreviewFile(string path)
        => Path.GetFileNameWithoutExtension(path).Equals("preview", StringComparison.OrdinalIgnoreCase);

    private static string BuildMediaFileName(string source, int index)
    {
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(source));
        if (string.IsNullOrWhiteSpace(stem)) stem = "media";
        if (stem.Length > 40) stem = stem[..40];

        return $"{index:00}_{stem}{Path.GetExtension(source).ToLowerInvariant()}";
    }

    /// <summary>包目录名：把包名洗干净再加 8 位摘要，避免同名包互相覆盖、也避免非法字符。</summary>
    private static string BuildPackageId(string displayName, string packagePath)
    {
        var safe = SanitizeFileName(Path.GetFileNameWithoutExtension(displayName));
        if (safe.Length > 24) safe = safe[..24];

        var length = new FileInfo(packagePath).Length;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{displayName}|{length}")))[..8];
        return $"{safe}_{hash}".ToLowerInvariant();
    }

    private static string SanitizeFileName(string raw)
        => new string(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("结束 RePKG 进程失败：{0}", ex.Message));
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        try { File.Delete(path); }
        catch (Exception ex) { Log.Warn(Loc.F("删除临时文件失败 {0}：{1}", path, ex.Message)); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("清理解包目录失败 {0}：{1}", path, ex.Message));
        }
    }
}
