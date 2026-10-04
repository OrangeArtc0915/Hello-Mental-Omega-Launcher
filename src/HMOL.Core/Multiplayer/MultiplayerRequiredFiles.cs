using System.IO;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.Logging;
using HMOL.Core.Packages;
using HMOL.Core.Updater;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// MO 联机补丁的下载、安装与状态查询。
///
/// <para>
/// 补丁是「解压到游戏根目录」的资源包，但<b>不</b>进包管理器列表——它不属于用户可挑选的插件包，
/// 而是联机的开关。所以下载物统一放在 <c>Data\MultiplayerRequired\</c>
/// （不在 <see cref="Paths.Packages"/> 下，天然不会出现在包列表）。
/// </para>
///
/// <para>
/// 下载走双线路（GitHub / Gitee），顺序与启动器自更新共用同一份偏好，
/// 具体由 <see cref="RequiredAssetDownloader"/> 负责。
/// </para>
/// </summary>
public static class MultiplayerRequiredFiles
{
    /// <summary>仓库里的压缩包名（同时是下载到本机后的文件名）。</summary>
    private const string PatchArchiveName = "MO-补丁-联机.zip";

    /// <summary>下载物存放目录：<c>Data\MultiplayerRequired</c>。刻意不放在 packages 目录，避免进包列表。</summary>
    public static string DownloadDirectory => Path.Combine(Paths.Data, "MultiplayerRequired");

    /// <summary>补丁下载到本机后的路径。</summary>
    public static string PatchPath => Path.Combine(DownloadDirectory, PatchArchiveName);

    /// <summary>判断补丁是否已装到游戏目录的标志文件（相对游戏根目录）。</summary>
    public const string InstalledMarkerRelative = "Resources/MoLanRelay.dll";

    /// <summary>补丁压缩包是否已经下载到本机。</summary>
    public static bool HasPatch
    {
        get
        {
            try { return File.Exists(PatchPath); }
            catch { return false; }
        }
    }

    /// <summary>樱花FRP 引擎（frpc）是否已就绪。</summary>
    public static bool IsFrpcReady
    {
        get
        {
            try { return File.Exists(SakuraFrpcRunner.FrpcPath); }
            catch { return false; }
        }
    }

    /// <summary>某个实例的游戏目录里是否已装上联机补丁。</summary>
    public static bool IsPatchInstalled(GameInstance? instance)
    {
        if (instance is null) return false;

        var root = instance.GameDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;

        try { return File.Exists(Path.Combine(root, "Resources", "MoLanRelay.dll")); }
        catch { return false; }
    }

    /// <summary>
    /// 确保补丁已下载到本机（双线路兜底；已存在有效压缩包时跳过）。
    /// </summary>
    public static Task<DownloadResult> EnsurePatchAsync(LauncherUpdateSource preferred =
        LauncherUpdateSource.Auto, IProgress<double>? progress = null, CancellationToken token = default)
        => RequiredAssetDownloader.DownloadAsync(PatchArchiveName, PatchPath, preferred, progress, token);

    /// <summary>
    /// 把已下载的补丁解压到实例游戏根目录（安装方式与插件包一致）。
    /// 返回是否成功与给用户看的说明。
    /// </summary>
    public static (bool Ok, string Message) InstallPatch(GameInstance? instance,
        IProgress<HMOL.Core.IO.ProgressSample>? progress = null, CancellationToken token = default)
    {
        if (instance is null) return (false, "未选择游戏实例");

        if (string.IsNullOrWhiteSpace(instance.GameDir) || !Directory.Exists(instance.GameDir))
            return (false, $"当前实例的游戏目录不可用：{instance.GameDir}");

        if (!File.Exists(PatchPath)) return (false, "还没有下载联机补丁，请先下载");

        Log.Info($"正在安装联机补丁：{PatchPath} → {instance.GameDir}");

        if (!ArchiveExtractor.TryExtract(PatchPath, instance.GameDir, out var error, progress, token))
            return (false, $"安装联机补丁失败：{error}");

        if (!IsPatchInstalled(instance))
            return (false, "已解压，但游戏目录里没找到补丁文件（Resources\\MoLanRelay.dll），请确认补丁包内容。");

        Log.Info($"联机补丁安装完成：{instance.Name}");
        return (true, "联机补丁已安装到游戏目录，联机功能已启用。");
    }
}
