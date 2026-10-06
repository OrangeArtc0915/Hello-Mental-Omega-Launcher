using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Updater;

namespace HMOL.Core.IO;

/// <summary>
/// 7-Zip 命令行组件（<c>runtime\7zip\7za.exe</c>）的下载与安装。
///
/// <para>
/// 它是启动器自己的解压依赖：首次打开启动器时提示下载，
/// 放回 exe 旁的 <c>runtime\7zip\</c>，与 <see cref="SevenZipTool"/> 的定位规则一致。
/// 组件缺失时 7z 会退回 SharpCompress 慢速实现（zip / tar / gz 不受影响），所以下载可以跳过。
/// </para>
/// </summary>
public static class SevenZipComponent
{
    /// <summary>发行版附件里的压缩包名。</summary>
    public const string ArchiveName = "7zip.zip";

    /// <summary>下载物存放目录。</summary>
    public static string DownloadDirectory => Path.Combine(Paths.Data, "Required");

    /// <summary>压缩包下载到本机后的路径。</summary>
    public static string ArchivePath => Path.Combine(DownloadDirectory, ArchiveName);

    /// <summary>安装目录：exe 旁 <c>runtime\7zip\</c>。</summary>
    public static string InstallDirectory => Path.Combine(RuntimeLocator.RuntimeRoot, "7zip");

    /// <summary>7za.exe 的绝对路径。</summary>
    public static string ExePath => Path.Combine(InstallDirectory, "7za.exe");

    /// <summary>7-Zip 组件是否已就绪。</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(ExePath); }
            catch { return false; }
        }
    }

    /// <summary>确保 7-Zip 组件就绪：已安装直接返回；否则下载（双线路）后解压。</summary>
    public static async Task<(bool Ok, string Message)> EnsureAsync(LauncherUpdateSource preferred,
        IProgress<double>? downloadProgress = null, IProgress<ProgressSample>? extractProgress = null,
        CancellationToken token = default)
    {
        if (IsInstalled) return (true, "7-Zip 组件已就绪");

        var download = await RequiredAssetDownloader.DownloadAsync(
            ArchiveName, ArchivePath, preferred, downloadProgress, token).ConfigureAwait(false);

        if (!download.Success) return (false, download.Message);

        var (ok, message) = RequiredAssetDownloader.ExtractInto(ArchivePath, InstallDirectory, extractProgress, token);

        if (!ok) return (false, $"解压 7-Zip 组件失败：{message}");
        if (!IsInstalled) return (false, "解压完成但未找到 7za.exe，请确认压缩包内容。");

        // 之前可能已经缓存了「找不到 7za」的结果，装好后必须让它重新解析
        SevenZipTool.ResetCache();

        Log.Info($"7-Zip 组件已安装：{ExePath}");
        return (true, "7-Zip 组件已就绪");
    }
}
