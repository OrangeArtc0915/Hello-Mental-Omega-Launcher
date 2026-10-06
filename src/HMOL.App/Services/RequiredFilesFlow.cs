using System.Windows;
using HMOL.App.Controls;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Services;

/// <summary>
/// 必要文件下载的界面流程：组网组件（runtime）与 7-Zip 组件的下载弹窗与进度显示。
/// 联机页、连接前的方案检查、首次启动的 7z 引导都走这里，保证提示与进度风格一致。
/// </summary>
internal static class RequiredFilesFlow
{
    /// <summary>
    /// 下载并安装单个组网组件，带二次确认与进度窗。返回是否已就绪。
    /// <paramref name="confirm"/> 为 false 时不再确认，直接下载（用于一键补齐）。
    /// </summary>
    public static async Task<bool> EnsureRuntimeAsync(Window? owner, RuntimeComponent component, bool confirm = true)
    {
        if (RuntimeComponents.IsInstalled(component)) return true;

        var name = RuntimeComponents.DisplayName(component);

        if (confirm)
        {
            var answer = ChoiceWindow.Confirm(owner, "下载组网组件",
                $"当前联机方案需要 {name}，本机还没有。",
                confirmText: "下载并安装", cancelText: "取消");

            if (!answer) return false;
        }

        var progress = ProgressWindow.Open(owner, "下载组网组件", $"正在下载 {name}…", canCancel: true);

        try
        {
            var (ok, message) = await RuntimeComponents.EnsureAsync(component,
                SettingsStore.Current.LauncherUpdateSource, progress.Progress, progress.Sample, progress.Token);

            ActivityLog.Write(LogSource.App, message, ok ? ActivityLevel.Info : ActivityLevel.Warn);

            if (!ok) ChoiceWindow.Warn(owner, "下载组网组件失败", $"{name}：{message}");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Error($"下载组网组件失败：{name}", ex);
            ChoiceWindow.Warn(owner, "下载组网组件失败", $"{name}：{ex.Message}");
            return false;
        }
        finally
        {
            progress.Finish();
        }
    }

    /// <summary>
    /// 首次打开启动器、向导出现之前补齐 7-Zip 组件。缺失时可以跳过：
    /// zip / tar / gz 走内置解压不受影响，只有 7z 会退回较慢的实现。
    /// </summary>
    public static async Task EnsureSevenZipOnStartupAsync(Window? owner)
    {
        if (SevenZipComponent.IsInstalled) return;

        var choice = ChoiceWindow.Ask(owner, "下载 7-Zip 组件",
            "启动器需要一个解压组件（7-Zip，约 1 MB），当前没有检测到。",
            "它对大多数操作不是必需的：zip / tar / gz 走内置解压；只有 7z 压缩包会退回较慢的实现。\n" +
            "现在下载会放到启动器目录的 runtime\\7zip\\，之后也能在左侧「下载」页重新下载。",
            new ChoiceOption("立即下载", "ok", ButtonTone.Solid),
            new ChoiceOption("跳过", "skip"));

        if (choice != "ok") return;

        await DownloadSevenZipAsync(owner);
    }

    /// <summary>下载并安装 7-Zip 组件（已确认过、不再弹确认）。返回是否已就绪。</summary>
    public static async Task<bool> DownloadSevenZipAsync(Window? owner)
    {
        if (SevenZipComponent.IsInstalled) return true;

        var progress = ProgressWindow.Open(owner, "下载 7-Zip 组件", "正在下载 7-Zip 组件…", canCancel: true);

        try
        {
            var (ok, message) = await SevenZipComponent.EnsureAsync(
                SettingsStore.Current.LauncherUpdateSource, progress.Progress, progress.Sample, progress.Token);

            ActivityLog.Write(LogSource.App, message, ok ? ActivityLevel.Info : ActivityLevel.Warn);

            if (!ok) ChoiceWindow.Warn(owner, "下载 7-Zip 组件失败",
                $"{message}\n\n不影响单机与 zip / tar 解压；稍后可在左侧「下载」页重试。");

            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("下载 7-Zip 组件失败", ex);
            ChoiceWindow.Warn(owner, "下载 7-Zip 组件失败", $"{ex.Message}\n\n不影响单机使用，稍后可重试。");
            return false;
        }
        finally
        {
            progress.Finish();
        }
    }
}
