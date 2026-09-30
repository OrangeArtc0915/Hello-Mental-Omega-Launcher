using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Updater;

/// <summary>检查更新走的线路偏好。</summary>
public enum LauncherUpdateSource
{
    /// <summary>自动：优先 GitHub，失败或不比当前版本新时换 Gitee。</summary>
    Auto = 0,

    GitHub = 1,
    Gitee = 2
}

/// <summary>一次检查更新的结局。</summary>
public enum UpdateCheckStatus
{
    /// <summary>两个源都没取到 —— 网络问题或仓库不可访问。启动时的自动检查遇到这个状态必须静默。</summary>
    Failed = 0,

    UpToDate = 1,

    /// <summary>线上有更新的版本。<see cref="LauncherUpdateInfo.Asset"/> 可能为空（该版本没有可自动更新的文件）。</summary>
    Available = 2
}

/// <summary>一个候选更新资产。</summary>
public sealed record UpdateAsset(string Name, string Url);

/// <summary>线上新版本的信息。<see cref="Asset"/> 为空表示只能手动去发布页下载。</summary>
public sealed record LauncherUpdateInfo(string Version, string Tag, string ReleasePageUrl, UpdateAsset? Asset,
    string Source);

/// <summary>检查更新的结果。失败不抛异常，原因放在 <see cref="Message"/> 里。</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, LauncherUpdateInfo? Update = null);

/// <summary>下载并交棒给替换脚本的结果。</summary>
public sealed record UpdateInstallResult(bool Success, string Message);

/// <summary>
/// 启动器自更新：检查新版本（GitHub / Gitee 双源）、下载到 <c>HMOL.exe.new</c>、校验、
/// 然后写一个临时 cmd 脚本由它在**本进程退出之后**完成替换。
///
/// 只替换 exe 自己，设置、缓存、用户的游戏文件一概不动。任何失败都写进返回值或日志，绝不向外抛。
/// </summary>
public static class LauncherUpdater
{
    /// <summary>能自动更新的资产必须以此前缀开头。发布页上还挂着别的文件，不卡前缀就可能把自己换成别的程序。</summary>
    public const string ExePrefix = "HMOL";

    /// <summary>请求用的 User-Agent。部分站点会按 UA 拒绝请求，写成自己的程序名便于对方排查。</summary>
    public const string UserAgent = "HMOL-Updater/" + AppInfo.Version + " (+" + AppInfo.GitHubUrl + ")";

    /// <summary>单文件自包含 exe 有上百兆，明显小于这个体积说明下到的是重定向页或错误页。</summary>
    public const long MinExecutableBytes = 5 * 1024 * 1024;

    /// <summary>旧版本备份最多换几个名字（.old / .old1 … .old4）。</summary>
    private const int MaxBackupIndex = 4;

    private static readonly Regex GitHubAssetRegex =
        new("href=\"([^\"]*/releases/download/[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ————— 路径约定（这些文件全都紧挨着 exe 放，替换是同卷 move，快且不怕目标目录只读）—————

    /// <summary>当前正在运行的这个 exe 的绝对路径。</summary>
    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return Path.GetFullPath(path);

            try
            {
                var main = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(main)) return Path.GetFullPath(main);
            }
            catch (Exception ex)
            {
                Log.Warn($"取当前进程路径失败：{ex.Message}");
            }

            return Path.Combine(AppContext.BaseDirectory, ExePrefix + ".exe");
        }
    }

    /// <summary>下载好的新版本，等替换脚本搬走。</summary>
    public static string StagedPath => ExecutablePath + ".new";

    /// <summary>用整包 zip 更新时的中间文件，解出 exe 后立刻删。</summary>
    public static string StagedZipPath => StagedPath + ".zip";

    /// <summary>替换失败的标记文件（内容是 ASCII 标记词），下次启动读走即删。</summary>
    public static string FailureNotePath => ExecutablePath + ".update-failed.txt";

    // ————— 进程与残留 —————

    /// <summary>
    /// 是否有另一个同一份 exe 的进程在跑。Windows 不让覆盖运行中的 exe，
    /// 不提前拦住的话用户会白等一百多兆的下载。
    /// </summary>
    public static int? OtherInstanceRunning()
    {
        var target = ExecutablePath;
        var processName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrWhiteSpace(processName)) return null;

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;

                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { continue; }

                    if (!string.IsNullOrWhiteSpace(path) && SamePath(path, target)) return process.Id;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举同名进程失败：{ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 清掉上次留下的 .new / .new.zip / .old / .old1…。清理失败只记日志：
    /// 这些文件常常被杀软占着，过几次启动就清掉了。
    /// </summary>
    public static void CleanupStaleFiles()
    {
        var target = ExecutablePath;
        if (string.IsNullOrWhiteSpace(target)) return;

        TryDeleteFile(StagedPath);
        TryDeleteFile(StagedPath + ".part");
        TryDeleteFile(StagedZipPath);
        TryDeleteFile(StagedZipPath + ".part");

        for (var index = 0; index <= MaxBackupIndex; index++) TryDeleteFile(BackupPathOf(target, index));
    }

    /// <summary>
    /// 读走并删除上次替换失败留下的标记，翻译成中文。没有标记时返回 null。
    /// 调用方拿到非空结果时应跳过本次自动检查（刚失败过，立刻再弹一次没意义）。
    /// </summary>
    public static string? TakePendingFailureNote()
    {
        var note = FailureNotePath;
        if (string.IsNullOrWhiteSpace(note) || !File.Exists(note)) return null;

        string marker;
        try
        {
            marker = File.ReadAllText(note).Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取更新失败标记失败：{ex.Message}");
            return null;
        }

        TryDeleteFile(note);
        Log.Warn($"上次自动更新失败，标记为「{marker}」");

        return TranslateFailureMarker(marker);
    }

    // ————— 检查更新 —————

    /// <summary>
    /// 检查有没有新版本。按 <paramref name="preferred"/> 指定的线路优先，另一个源兜底；
    /// 两个都取不到就返回 <see cref="UpdateCheckStatus.Failed"/>，**不抛异常**。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(LauncherUpdateSource preferred = LauncherUpdateSource.Auto,
        CancellationToken token = default)
    {
        var order = preferred == LauncherUpdateSource.Gitee
            ? new[] { LauncherUpdateSource.Gitee, LauncherUpdateSource.GitHub }
            : new[] { LauncherUpdateSource.GitHub, LauncherUpdateSource.Gitee };

        var errors = new List<string>();
        var reachedAny = false;

        foreach (var source in order)
        {
            var probe = source == LauncherUpdateSource.Gitee
                ? await ProbeGiteeAsync(token).ConfigureAwait(false)
                : await ProbeGitHubAsync(token).ConfigureAwait(false);

            var name = SourceName(source);

            if (!probe.Reachable)
            {
                errors.Add($"{name}：{probe.Error}");
                Log.Warn($"从 {name} 获取更新信息失败（{probe.Error}），换另一个线路试试");
                continue;
            }

            reachedAny = true;

            // 该源还没有发布任何版本，换另一个源看看
            if (string.IsNullOrWhiteSpace(probe.Tag)) continue;

            var version = SemVer.Normalize(probe.Tag);

            if (!SemVer.IsNewer(version, AppInfo.Version))
            {
                Log.Info($"{name} 上最新版本为 {version}，不比当前 {AppInfo.Version} 新");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate,
                    $"当前已是最新版本（{AppInfo.VersionDisplay}）。");
            }

            var asset = PickAsset(probe.Assets);

            if (asset is null)
            {
                // 有新版本，但没找到能自动更新的文件：让用户去发布页，这不属于检查失败
                Log.Warn($"{name} 的 {probe.Tag} 里没有以 {ExePrefix} 开头的 exe / zip 资产");
                return new UpdateCheckResult(UpdateCheckStatus.Available,
                    $"发现新版本 {version}，但这个版本没有可自动更新的文件，请到发布页手动下载。",
                    new LauncherUpdateInfo(version, probe.Tag, probe.ReleasePageUrl, null, name));
            }

            Log.Info($"{name} 上发现新版本 {version}，候选资产 {asset.Name}");
            return new UpdateCheckResult(UpdateCheckStatus.Available,
                $"发现新版本 {version}（来自 {name}）。",
                new LauncherUpdateInfo(version, probe.Tag, probe.ReleasePageUrl, asset, name));
        }

        // 两个源都联系上了，但都没有发布版本
        if (reachedAny)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate,
                $"当前已是最新版本（{AppInfo.VersionDisplay}）。");

        foreach (var error in errors) Log.Warn($"检查更新失败：{error}");

        return new UpdateCheckResult(UpdateCheckStatus.Failed,
            "无法获取更新信息，请检查网络或改用其它线路。详细原因见运行日志。");
    }

    /// <summary>从候选资产里挑一个。先找裸 exe，再退一步用整包 zip；两者都必须以约定前缀开头。</summary>
    public static UpdateAsset? PickAsset(IReadOnlyList<UpdateAsset> assets)
        => assets.FirstOrDefault(asset => IsCandidate(asset.Name, ".exe"))
           ?? assets.FirstOrDefault(asset => IsCandidate(asset.Name, ".zip"));

    private static bool IsCandidate(string name, string extension)
        => !string.IsNullOrWhiteSpace(name)
           && name.StartsWith(ExePrefix, StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string SourceName(LauncherUpdateSource source)
        => source == LauncherUpdateSource.Gitee ? "Gitee" : "GitHub";

    /// <summary>
    /// GitHub 走网页而不走 API：未认证的 API 请求只有 60 次/小时，很容易被限流。
    /// 两步：先取 releases/latest 的 302 拿到 tag，再抓 expanded_assets 片段抠资产。
    /// </summary>
    private static async Task<SourceProbe> ProbeGitHubAsync(CancellationToken token)
    {
        var repo = RepoPathOf(AppInfo.GitHubUrl);
        if (repo is null) return SourceProbe.Unreachable("仓库地址无法解析");

        var location = await HttpDownloader
            .GetRedirectLocationAsync($"https://github.com/{repo}/releases/latest", UserAgent, token)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(location)) return SourceProbe.Unreachable("没有取到跳转地址");

        var tag = ExtractTag(location);

        // 有 release 时跳到 .../releases/tag/v1.0.0；一个 release 都没有时只跳到 .../releases，
        // 这是正常状态，不是错误
        if (string.IsNullOrWhiteSpace(tag))
        {
            Log.Info($"GitHub 上还没有发布版本（releases/latest 跳到 {location}）");
            return SourceProbe.Released(null, AppInfo.GitHubReleasesUrl, []);
        }

        var page = $"{AppInfo.GitHubReleasesUrl}/tag/{tag}";
        var html = await HttpDownloader
            .GetStringAsync($"https://github.com/{repo}/releases/expanded_assets/{tag}", UserAgent, token)
            .ConfigureAwait(false);

        // 资产页读不到就当作这个源不可用，交给另一个源兜底
        if (html is null) return SourceProbe.Unreachable($"资产列表页读取失败（{tag}）");

        return SourceProbe.Released(tag, page, ParseGitHubAssets(html));
    }

    /// <summary>Gitee 用官方 API，但要查两次：releases/latest 里的 assets 是自动生成的源码包，不是人上传的附件。</summary>
    private static async Task<SourceProbe> ProbeGiteeAsync(CancellationToken token)
    {
        var repo = RepoPathOf(AppInfo.GiteeUrl);
        if (repo is null) return SourceProbe.Unreachable("仓库地址无法解析");

        var json = await HttpDownloader
            .GetStringAsync($"https://gitee.com/api/v5/repos/{repo}/releases/latest", UserAgent, token)
            .ConfigureAwait(false);

        // 仓库还没有发布版本时这个接口返回 404，和网络不通在结果上无法区分
        if (json is null) return SourceProbe.Unreachable("接口不可用（仓库可能还没有发布版本，或网络不通）");

        string? tag;
        string? id;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return SourceProbe.Unreachable("返回内容不是预期格式");

            tag = ReadString(root, "tag_name");
            id = ReadScalar(root, "id");
        }
        catch (Exception ex)
        {
            return SourceProbe.Unreachable($"返回内容解析失败（{ex.Message}）");
        }

        if (string.IsNullOrWhiteSpace(tag)) return SourceProbe.Unreachable("返回内容里没有 tag_name");
        if (string.IsNullOrWhiteSpace(id)) return SourceProbe.Unreachable("返回内容里没有发布 id");

        var page = $"{AppInfo.GiteeReleasesUrl}/tag/{tag}";
        var filesJson = await HttpDownloader
            .GetStringAsync($"https://gitee.com/api/v5/repos/{repo}/releases/{id}/attach_files", UserAgent, token)
            .ConfigureAwait(false);

        if (filesJson is null) return SourceProbe.Unreachable($"附件列表读取失败（{tag}）");

        try
        {
            using var document = JsonDocument.Parse(filesJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Array) return SourceProbe.Unreachable("附件列表不是预期格式");

            var assets = new List<UpdateAsset>();

            foreach (var item in root.EnumerateArray())
            {
                var name = ReadString(item, "name");
                var url = ReadString(item, "browser_download_url");

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    assets.Add(new UpdateAsset(name!, url!));
            }

            return SourceProbe.Released(tag, page, assets);
        }
        catch (Exception ex)
        {
            return SourceProbe.Unreachable($"附件列表解析失败（{ex.Message}）");
        }
    }

    /// <summary>从形如 <c>https://github.com/o/r/releases/tag/v1.0.0</c> 的地址里切出 tag。</summary>
    private static string? ExtractTag(string location)
    {
        const string marker = "/tag/";

        var index = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var tag = location[(index + marker.Length)..];

        var stop = tag.IndexOfAny(['?', '#', '/']);
        if (stop >= 0) tag = tag[..stop];

        return string.IsNullOrWhiteSpace(tag) ? null : Uri.UnescapeDataString(tag);
    }

    /// <summary>从 expanded_assets 片段里抠资产。片段里的 href 是根相对路径，必须补成绝对地址。</summary>
    private static List<UpdateAsset> ParseGitHubAssets(string html)
    {
        var assets = new List<UpdateAsset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in GitHubAssetRegex.Matches(html))
        {
            var href = match.Groups[1].Value;
            if (href.Length == 0 || !seen.Add(href)) continue;

            var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : "https://github.com" + (href.StartsWith('/') ? href : "/" + href);

            var name = Uri.UnescapeDataString(href.TrimEnd('/').Split('/')[^1]);
            if (string.IsNullOrWhiteSpace(name)) continue;

            assets.Add(new UpdateAsset(name, url));
        }

        return assets;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadScalar(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64().ToString(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };
    }

    // ————— 下载并交棒给替换脚本 —————

    /// <summary>
    /// 下载新版本、校验，然后启动替换脚本。返回成功后调用方应稍等片刻再退出进程 ——
    /// 真正的等待与替换由脚本负责。
    /// </summary>
    public static async Task<UpdateInstallResult> DownloadAndInstallAsync(LauncherUpdateInfo info,
        IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (info.Asset is null)
            return new UpdateInstallResult(false, "这个版本没有可自动更新的文件，请到发布页手动下载。");

        var target = ExecutablePath;
        if (string.IsNullOrWhiteSpace(target))
            return new UpdateInstallResult(false, "无法确定当前程序的位置，请到发布页手动下载。");

        var other = OtherInstanceRunning();
        if (other is not null)
            return new UpdateInstallResult(false,
                $"检测到另一个启动器进程（PID {other}）还在运行，Windows 不允许覆盖正在运行的程序。请先把它关掉再更新。");

        // 残留存在时「下载成功」可能只是假象，先清干净
        CleanupStaleFiles();

        if (info.Asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var zipResult = await ResumableDownloader
                .DownloadAsync(info.Asset.Url, StagedZipPath, progress, token).ConfigureAwait(false);

            if (!zipResult.Success) return new UpdateInstallResult(false, $"下载失败：{zipResult.Message}");

            var extracted = TryExtractExecutable(StagedZipPath, StagedPath, out var extractError);
            TryDeleteFile(StagedZipPath);

            if (!extracted) return new UpdateInstallResult(false, $"从压缩包里取出启动器失败：{extractError}");
        }
        else
        {
            var result = await ResumableDownloader
                .DownloadAsync(info.Asset.Url, StagedPath, progress, token).ConfigureAwait(false);

            if (!result.Success) return new UpdateInstallResult(false, $"下载失败：{result.Message}");
        }

        if (!ValidateStaged(out var invalid)) return new UpdateInstallResult(false, invalid);

        string scriptPath;

        try
        {
            scriptPath = WriteInstallScript(target);
        }
        catch (Exception ex)
        {
            Log.Error("生成替换脚本失败", ex);
            return new UpdateInstallResult(false, $"生成替换脚本失败：{ex.Message}");
        }

        if (!StartScript(scriptPath, out var startError))
            return new UpdateInstallResult(false, $"无法启动替换脚本：{startError}");

        Log.Info($"已交棒给替换脚本 {scriptPath}，准备退出进程");
        return new UpdateInstallResult(true, $"已开始更新到 {info.Version}，程序会自动重启。");
    }

    /// <summary>校验两条：体积不能明显偏小，文件头必须是 PE 可执行文件的 MZ。</summary>
    private static bool ValidateStaged(out string reason)
    {
        reason = string.Empty;

        try
        {
            var file = new FileInfo(StagedPath);

            if (!file.Exists)
            {
                reason = "下载到的文件不见了，已中止更新。";
                return false;
            }

            if (file.Length < MinExecutableBytes)
            {
                reason = $"下载到的文件只有 {ResumableDownloader.FormatSize(file.Length)}，不像是启动器（可能是错误页），已中止更新。";
                return false;
            }

            using var stream = File.OpenRead(StagedPath);
            var header = new byte[2];

            if (stream.Read(header, 0, 2) < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                reason = "下载到的文件不是可执行文件（缺少 MZ 文件头），已中止更新。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = $"校验下载文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>从 zip 里取出前缀匹配的 exe 到目标路径。</summary>
    private static bool TryExtractExecutable(string zipPath, string destinationPath, out string error)
    {
        error = string.Empty;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var entry = archive.Entries.FirstOrDefault(item =>
                IsCandidate(Path.GetFileName(item.FullName), ".exe"));

            if (entry is null)
            {
                error = $"压缩包里没有以 {ExePrefix} 开头的 exe";
                return false;
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
            Log.Info($"已从压缩包取出 {entry.FullName}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 生成替换脚本。用 cmd 是因为它是系统自带的，一定存在 —— 不用为了更新额外带一个 updater 程序
    /// （用户可能只拷走了主程序）。
    /// </summary>
    private static string WriteInstallScript(string target)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{ExePrefix}-update-{Environment.ProcessId}.cmd");
        var text = BuildScriptText(Environment.ProcessId, target, StagedPath, PickBackupPath(target), FailureNotePath);

        // 首行 chcp 65001 + 以 UTF-8 无 BOM 写盘：脚本里要写 exe 的绝对路径，
        // 用户目录可能含中文，纯 ASCII 会把路径写坏；标记词保持 ASCII，中文文案放在程序里翻译。
        File.WriteAllText(scriptPath, text, new UTF8Encoding(false));
        return scriptPath;
    }

    /// <summary>
    /// 挑一个还能用的备份文件名（.old / .old1 … .old4）。.old 可能被残留进程或杀软占着，
    /// 死磕一个名字会让整个替换卡住。
    /// </summary>
    private static string PickBackupPath(string target)
    {
        var last = BackupPathOf(target, 0);

        for (var index = 1; index <= MaxBackupIndex; index++)
        {
            last = BackupPathOf(target, index);
            if (!File.Exists(last)) return last;
        }

        // 全都被占了就继续用最后一个：脚本在搬之前会先把它删掉
        return last;
    }

    private static string BackupPathOf(string target, int index) => index == 0 ? target + ".old" : target + ".old" + index;

    private static string BuildScriptText(int pid, string target, string staged, string backup, string note) => $"""
        @echo off
        chcp 65001 >nul
        setlocal enabledelayedexpansion
        set "TARGET={target}"
        set "STAGED={staged}"
        set "BACKUP={backup}"
        set "NOTE={note}"
        set /a WAIT=0

        :wait
        tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
        if errorlevel 1 goto ready
        set /a WAIT+=1
        if !WAIT! GEQ 90 goto timeout
        ping -n 2 127.0.0.1 >nul
        goto wait

        :timeout
        > "%NOTE%" echo timeout
        del /f /q "%~f0" >nul 2>&1
        exit /b 2

        :ready
        set /a TRY=0

        :swap
        set /a TRY+=1
        attrib -r -h -s "%TARGET%" >nul 2>&1
        attrib -r -h -s "%STAGED%" >nul 2>&1
        attrib -r -h -s "%BACKUP%" >nul 2>&1
        del /f /q "%BACKUP%" >nul 2>&1
        if exist "%BACKUP%" goto retry
        move /y "%TARGET%" "%BACKUP%" >nul 2>&1
        if exist "%TARGET%" goto retry
        move /y "%STAGED%" "%TARGET%" >nul 2>&1
        if exist "%STAGED%" goto rollback
        goto done

        :rollback
        if exist "%TARGET%" goto retry
        move /y "%BACKUP%" "%TARGET%" >nul 2>&1
        if not exist "%TARGET%" goto broken
        goto retry

        :retry
        if !TRY! LSS 5 (
          ping -n 2 127.0.0.1 >nul
          goto swap
        )

        > "%NOTE%" echo locked
        if exist "%TARGET%" start "" "%TARGET%"
        del /f /q "%~f0" >nul 2>&1
        exit /b 1

        :broken
        > "%NOTE%" echo broken
        del /f /q "%~f0" >nul 2>&1
        exit /b 3

        :done
        start "" "%TARGET%"
        ping -n 4 127.0.0.1 >nul
        del /f /q "%BACKUP%" >nul 2>&1
        del /f /q "%~f0" >nul 2>&1
        exit /b 0
        """;

    /// <summary>无窗口启动替换脚本。它跑完自己会删掉自己。</summary>
    private static bool StartScript(string scriptPath, out string error)
    {
        error = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("启动替换脚本失败", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把脚本写的 ASCII 标记翻译成给用户看的中文。</summary>
    private static string TranslateFailureMarker(string marker) => marker.ToLowerInvariant() switch
    {
        "locked" => "上一次自动更新没能替换文件：启动器被其它程序占着（常见是杀毒软件正在扫描）。"
                    + "旧版本已经还原，可以重启电脑后再试一次。",
        "timeout" => "上一次自动更新等待超时：启动器在 90 秒内没有退出，本次没有改动任何文件。"
                     + "可以重启电脑后再试一次。",
        "broken" => "上一次自动更新失败，旧版本也没能还原。请到发布页手动下载最新版本，覆盖原来的启动器。",
        _ => $"上一次自动更新失败（标记：{marker}）。请到发布页手动下载最新版本。"
    };

    /// <summary>从仓库地址里取「owner/repo」。</summary>
    private static string? RepoPathOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var text = url.Trim().TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text[..^4];

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : null;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理文件失败：{path}（{ex.Message}）");
        }
    }

    /// <summary>一个源的探测结果。<see cref="Reachable"/> 为 false 表示这个源用不了，应换另一个源。</summary>
    private sealed record SourceProbe(bool Reachable, string? Tag, string ReleasePageUrl,
        IReadOnlyList<UpdateAsset> Assets, string? Error)
    {
        public static SourceProbe Unreachable(string error) => new(false, null, string.Empty, [], error);

        /// <summary>联系上了：<paramref name="tag"/> 为空表示该源还没有发布版本。</summary>
        public static SourceProbe Released(string? tag, string releasePageUrl, IReadOnlyList<UpdateAsset> assets)
            => new(true, tag, releasePageUrl, assets, null);
    }
}