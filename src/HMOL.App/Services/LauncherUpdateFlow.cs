using System.Windows;
using HMOL.App.Controls;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Updater;

namespace HMOL.App.Services;

/// <summary>
/// 检查 / 安装启动器更新的界面流程：启动时的自动检查与设置页的按钮共用同一套弹窗与进度显示。
/// 弹窗一律用自绘窗口，不用 MessageBox，免得风格和主界面不一致。
/// </summary>
internal static class LauncherUpdateFlow
{
    /// <summary>检查是否有新版本，并把检查时间记进设置（设置页会显示它）。</summary>
    public static async Task<UpdateCheckResult> CheckAsync()
    {
        var result = await LauncherUpdater.CheckAsync(SettingsStore.Current.LauncherUpdateSource);

        if (result.Status != UpdateCheckStatus.Failed)
        {
            SettingsStore.Current.LastUpdateCheck = DateTime.Now;
            SettingsStore.Save();
        }

        Log.Info($"检查启动器更新：{result.Status}｜{result.Message}");
        return result;
    }

    /// <summary>
    /// 启动时的自动检查。顺序有讲究：先弹上次的失败提示（有则跳过本次检查），再看设置开关。
    /// 「没有更新」与「检查失败」都不打扰用户，只记日志。
    /// </summary>
    public static async Task StartupCheckAsync(Window owner, string? failureNote, bool silenced)
    {
        if (silenced)
        {
            if (failureNote is not null) Log.Warn("上次更新失败的提示因静默模式未弹出");
            return;
        }

        try
        {
            // 刚才失败过，立刻再弹一次提示没意义
            if (failureNote is not null)
            {
                ChoiceWindow.Ask(owner, "上次更新没有完成", failureNote,
                    "本次启动已跳过自动检查更新。",
                    new ChoiceOption("知道了", "ok", ButtonTone.Solid));

                return;
            }

            if (!SettingsStore.Current.CheckUpdateOnStartup)
            {
                Log.Info("启动时自动检查更新已关闭，跳过");
                return;
            }

            var result = await CheckAsync();

            // 检查失败要静默：网络问题不该打扰启动
            if (result.Status != UpdateCheckStatus.Available || result.Update is null) return;

            await InstallAsync(owner, result.Update);
        }
        catch (Exception ex)
        {
            Log.Warn($"启动时检查更新失败：{ex.Message}");
        }
    }

    /// <summary>确认是否更新，然后下载、交棒给替换脚本，最后退出进程让脚本完成替换。</summary>
    public static async Task InstallAsync(Window owner, LauncherUpdateInfo info)
    {
        try
        {
            // 有新版本但没找到可自动更新的文件（发布页上没挂裸 exe 也没挂 zip）：只能去发布页
            if (info.Asset is null)
            {
                ShellHelper.OpenUrl(info.ReleasePageUrl);
                return;
            }

            var choice = ChoiceWindow.Ask(owner, "发现新版本",
                $"启动器有新版本 v{info.Version}（来自 {info.Source}）。",
                "更新只会替换启动器本身：设置、缓存和你的游戏文件都不会动。\n" +
                "更新时程序会自动关闭，替换完成后重新打开。",
                new ChoiceOption($"现在更新到 v{info.Version}", "install", ButtonTone.Solid),
                new ChoiceOption("稍后", "later"),
                new ChoiceOption("打开发布页", "page"));

            Log.Info($"更新确认框返回 {choice ?? "<关闭>"}");

            if (choice == "page")
            {
                ShellHelper.OpenUrl(info.ReleasePageUrl);
                return;
            }

            if (choice != "install") return;

            var progress = ProgressWindow.Open(owner, "正在更新启动器",
                $"正在下载 v{info.Version}…", canCancel: true);

            UpdateInstallResult result;

            try
            {
                result = await LauncherUpdater.DownloadAndInstallAsync(info, progress.Progress, progress.Token);
            }
            catch (Exception ex)
            {
                Log.Error("下载启动器更新失败", ex);
                result = new UpdateInstallResult(false, $"更新失败：{ex.Message}");
            }
            finally
            {
                progress.Finish();
            }

            Log.Info($"更新结果：{(result.Success ? "已交棒给替换脚本" : "失败")}｜{result.Message}");

            if (!result.Success)
            {
                var next = ChoiceWindow.Ask(owner, "更新没有完成", result.Message,
                    "可以稍后再试，或到发布页手动下载最新版本覆盖原来的启动器。",
                    new ChoiceOption("打开发布页", "page"),
                    new ChoiceOption("知道了", "ok", ButtonTone.Solid));

                if (next == "page") ShellHelper.OpenUrl(info.ReleasePageUrl);

                return;
            }

            // 替换脚本正在等本进程退出。稍等一下让「正在更新」的状态刷出来，真正的等待由脚本负责
            await Task.Delay(600);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("更新流程异常", ex);
        }
    }
}