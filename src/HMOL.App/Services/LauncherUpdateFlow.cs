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
    ///
    /// <para>
    /// 例外：强制更新（跨主/次版本）不看「启动时自动检查更新」这个开关 —— 旧版本本来就不该继续用，
    /// 所以照常查一次网络；查到的新版本若不是强制更新，才按开关决定提不提示。
    /// </para>
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

            var checkOnStartup = SettingsStore.Current.CheckUpdateOnStartup;

            if (!checkOnStartup) Log.Info("启动时自动检查更新已关闭，本次只查有没有强制更新");

            var result = await CheckAsync();

            // 检查失败要静默：网络问题不该打扰启动
            if (result.Status != UpdateCheckStatus.Available || result.Update is null) return;

            if (!checkOnStartup && !SemVer.IsForcedUpdate(result.Update.Version, AppInfo.Version))
            {
                Log.Info($"新版本 {result.Update.Version} 不是强制更新，而启动时检查已关闭，本次不提示");
                return;
            }

            await InstallAsync(owner, result.Update);
        }
        catch (Exception ex)
        {
            Log.Warn($"启动时检查更新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 确认是否更新，然后下载、交棒给替换脚本，最后退出进程让脚本完成替换。
    ///
    /// <para>
    /// 主版本或次版本变化（1.3.0→1.4.0、1.3.0→2.3.0）算「强制更新」：不给「稍后」，
    /// 只要没点「现在更新」，无论是打开发布页还是直接关掉确认框，都会退出程序 ——
    /// 跨了主/次版本意味着服务端接口或约定已经变了，让旧版本继续跑只会到处出错。
    /// 只有修订号变化（1.3.0→1.3.1）才交给用户自己选。
    /// </para>
    /// </summary>
    public static async Task InstallAsync(Window owner, LauncherUpdateInfo info)
    {
        try
        {
            // 用户可以在确认框里切换下载线路；换完重新检查、再弹一次，所以这里用循环而不是递归
            while (true)
            {
                var forced = SemVer.IsForcedUpdate(info.Version, AppInfo.Version);
                var canAutoInstall = info.Asset is not null;

                // 非强制 + 发布页上没有可自动更新的文件（既没挂裸 exe 也没挂 zip）：照旧不打扰，直接给发布页
                if (!canAutoInstall && !forced)
                {
                    ShellHelper.OpenUrl(info.ReleasePageUrl);
                    return;
                }

                var switchOption = new ChoiceOption($"换个线路（{SourceLabel(OtherSource())}）", "switch");

                string? choice;

                if (forced)
                {
                    // 能自动更新时主推「现在更新」；不能的话只能引导去发布页。
                    // 强制更新同样允许换线路：某个源下不动时不该把人卡死在这里。
                    ChoiceOption[] options = canAutoInstall
                        ? [new ChoiceOption($"现在更新到 v{info.Version}", "install", ButtonTone.Solid),
                           switchOption,
                           new ChoiceOption("打开发布页", "page")]
                        : [new ChoiceOption("打开发布页手动下载", "page", ButtonTone.Solid), switchOption];

                    choice = ChoiceWindow.Ask(owner, "必须更新",
                        $"启动器有新版本 v{info.Version}，本次必须更新后才能继续使用（来自 {info.Source}）。",
                        "这次更新跨了主版本或次版本，旧版本不再可用。\n" +
                        "更新只会替换启动器本身：设置、缓存和你的游戏文件都不会动。\n" +
                        "没有完成更新的话，启动器会直接退出。\n" +
                        $"当前线路（{info.Source}）下不动的话，可以点「{switchOption.Text}」换一个源再试。",
                        options);
                }
                else
                {
                    choice = ChoiceWindow.Ask(owner, "发现新版本",
                        $"启动器有新版本 v{info.Version}（来自 {info.Source}）。",
                        "更新只会替换启动器本身：设置、缓存和你的游戏文件都不会动。\n" +
                        "更新时程序会自动关闭，替换完成后重新打开。\n" +
                        $"下载不了的话，可以点「{switchOption.Text}」换一个源再试。",
                        new ChoiceOption($"现在更新到 v{info.Version}", "install", ButtonTone.Solid),
                        switchOption,
                        new ChoiceOption("稍后", "later"),
                        new ChoiceOption("打开发布页", "page"));
                }

                Log.Info($"更新确认框返回 {choice ?? "<关闭>"}（{(forced ? "强制" : "可选")}更新，来自 {info.Source}）");

                if (choice == "switch")
                {
                    var current = SettingsStore.Current.LauncherUpdateSource;
                    var next = OtherSource();

                    SettingsStore.Current.LauncherUpdateSource = next;
                    SettingsStore.Save();

                    Log.Info($"更新窗口切换线路：{current} → {next}，重新检查");

                    var again = await CheckAsync();

                    if (again.Update is null)
                    {
                        ChoiceWindow.Warn(owner, "换个线路没查到更新", again.Message,
                            "可以到「设置 → 程序更新」里手动再换一次线路，或稍后再试。");

                        // 强制更新换源后仍取不到：旧版本还是不能继续用
                        if (forced) ExitForForcedUpdate();
                        return;
                    }

                    info = again.Update;
                    continue;
                }

                if (choice == "page")
                {
                    ShellHelper.OpenUrl(info.ReleasePageUrl);

                    if (forced) ExitForForcedUpdate();
                    return;
                }

                if (choice != "install")
                {
                    // 直接关掉确认框等于拒绝更新：强制更新下不给「继续用旧版本」这条路
                    if (forced) ExitForForcedUpdate();
                    return;
                }

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
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("更新流程异常", ex);
        }
    }

    /// <summary>当前线路之外的另一条：Auto / GitHub 都换成 Gitee，Gitee 换成 GitHub。</summary>
    private static LauncherUpdateSource OtherSource()
        => SettingsStore.Current.LauncherUpdateSource == LauncherUpdateSource.Gitee
            ? LauncherUpdateSource.GitHub
            : LauncherUpdateSource.Gitee;

    private static string SourceLabel(LauncherUpdateSource source) => source switch
    {
        LauncherUpdateSource.GitHub => "GitHub 优先",
        LauncherUpdateSource.Gitee => "Gitee 优先",
        _ => "自动"
    };

    /// <summary>
    /// 强制更新没被接受时退出程序。确认框里已经写明「没有完成更新的话启动器会直接退出」，
    /// 所以这里不再多弹一次，记日志即可。
    /// </summary>
    private static void ExitForForcedUpdate()
    {
        Log.Warn("强制更新未完成，退出启动器");

        Application.Current.Shutdown();
    }
}