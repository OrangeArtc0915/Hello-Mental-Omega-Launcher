using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Updater;

namespace HMOL.App.Pages;

/// <summary>
/// 下载页：运行库（组网组件 / 7-Zip / 樱花 Frp 引擎）与 MO 联机补丁的下载入口。
///
/// <para>
/// 只提供运行库与启动器补丁，不提供任何游戏资源。补丁会解压到当前实例的游戏根目录，
/// 安装方式与插件包一致；组网组件与 7-Zip 解压回 exe 旁的 <c>runtime\</c>。
/// 不放进包管理列表：这些是运行依赖，不是用户可挑选的插件包。
/// </para>
/// </summary>
public partial class PageDownload : LauncherPage
{
    /// <summary>本页动画：键统一带 <c>dl:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("dl:");

    /// <summary>正在下载 / 安装：期间避免重复触发。</summary>
    private bool _busy;

    /// <summary>正在拉取「下载页文件」。</summary>
    private bool _manifestLoading;

    public PageDownload()
    {
        InitializeComponent();

        _anim.Group(0, HeaderDownload);
        _anim.Group(80, BarNotice);

        RefreshRequiredFilesUi();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    public override void OnEnter()
    {
        RefreshRequiredFilesUi();
        _ = LoadManifestAsync();
        _anim.Play();
    }

    public override void OnLeave() => _anim.Stop();

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    // ————— 状态刷新 —————

    /// <summary>刷新补丁状态与各运行组件状态。</summary>
    private void RefreshRequiredFilesUi()
    {
        if (LabPatchStatus is null) return;

        var instance = InstanceManager.Current;
        var installed = MultiplayerRequiredFiles.IsPatchInstalled(instance);
        var downloaded = MultiplayerRequiredFiles.HasPatch;
        var frpcReady = MultiplayerRequiredFiles.IsFrpcReady;

        if (instance is null)
        {
            LabPatchStatus.Text = "还没有选择游戏实例：可以先下载联机必要文件；安装到游戏目录需要先创建并选择一个实例。";
            LabPatchDetail.Text = downloaded
                ? "联机补丁已下载到本机。创建 / 选择游戏实例后，再回来点「安装联机补丁」。"
                : $"将下载樱花 Frp 引擎、组网组件与 MO 联机补丁，线路：{UpdateSourceName()}。";
        }
        else if (installed)
        {
            LabPatchStatus.Text = $"当前实例已安装联机补丁（{instance.Name}），联机功能可用。";
            LabPatchDetail.Text =
                $"樱花 Frp 引擎：{(frpcReady ? "已就绪" : "未下载，点「下载联机必要文件」可补齐")}。补丁已解压到游戏根目录。";
        }
        else
        {
            LabPatchStatus.Text = $"当前实例未安装联机补丁（{instance.Name}）：联机功能已禁用，请先下载并安装。";
            LabPatchDetail.Text = downloaded
                ? "联机补丁已下载，点「安装联机补丁」解压到游戏根目录。"
                : $"将下载樱花 Frp 引擎、组网组件与 MO 联机补丁，线路：{UpdateSourceName()}。补丁会解压到游戏根目录（与插件包安装方式一致）。";
        }

        // 下载不需要游戏实例；安装才需要
        BtnDownloadRequired.IsEnabled = !_busy;
        BtnInstallPatch.IsEnabled = !_busy && instance is not null && downloaded && !installed;
        BtnDownloadRequired.Content = downloaded ? "重新下载联机必要文件" : "下载联机必要文件";

        RefreshRuntimeRows();
    }

    /// <summary>刷新组网组件 / 7-Zip 组件每一行的状态与按钮。</summary>
    private void RefreshRuntimeRows()
    {
        if (LabRtEasyTier is null) return;

        UpdateRuntimeRow(RuntimeComponent.EasyTier, LabRtEasyTier, BtnRtEasyTier);
        UpdateRuntimeRow(RuntimeComponent.N2n, LabRtN2n, BtnRtN2n);
        UpdateRuntimeRow(RuntimeComponent.Tap, LabRtTap, BtnRtTap);
        UpdateRuntimeRow(RuntimeComponent.WinIpBroadcast, LabRtWinIpBroadcast, BtnRtWinIpBroadcast);

        var sevenZip = SevenZipComponent.IsInstalled;

        LabRt7z.Text = sevenZip ? "已安装" : "未安装（7z 压缩包会退回较慢的解压实现）";
        LabRt7z.SetResourceReference(TextBlock.ForegroundProperty, sevenZip ? "Status.Success" : "Text.Tertiary");

        BtnRt7z.Content = sevenZip ? "重新下载" : "下载";
        BtnRt7z.IsEnabled = !_busy;
    }

    private void UpdateRuntimeRow(RuntimeComponent component, TextBlock status, OutlineButton button)
    {
        var installed = RuntimeComponents.IsInstalled(component);

        status.Text = installed ? "已安装" : "未安装";
        status.SetResourceReference(TextBlock.ForegroundProperty, installed ? "Status.Success" : "Text.Tertiary");

        button.Content = installed ? "重新下载" : "下载";
        button.IsEnabled = !_busy;
    }

    /// <summary>当前下载线路的一句话说明（与设置页的「更新线路」共用同一个选项）。</summary>
    private static string UpdateSourceName() => SettingsStore.Current.LauncherUpdateSource switch
    {
        LauncherUpdateSource.Gitee => "Gitee 优先，失败换 GitHub",
        LauncherUpdateSource.GitHub => "GitHub 优先，失败换 Gitee",
        _ => "自动（GitHub 优先，失败换 Gitee）"
    };

    // ————— 下载 / 安装 —————

    private void OnDownloadRequiredClick(object sender, RoutedEventArgs e)
        => _ = DownloadAndInstallRequiredAsync();

    /// <summary>下载单个组件（组网组件或 7-Zip）。Tag 即组件标识。</summary>
    private async void OnRuntimeComponentDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;

        _busy = true;
        RefreshRequiredFilesUi();

        try
        {
            if (string.Equals(tag, "7z", StringComparison.OrdinalIgnoreCase))
            {
                await RequiredFilesFlow.DownloadSevenZipAsync(OwnerWindow);
                return;
            }

            if (Enum.TryParse<RuntimeComponent>(tag, out var component))
                await RequiredFilesFlow.EnsureRuntimeAsync(OwnerWindow, component, confirm: false);
        }
        finally
        {
            _busy = false;
            RefreshRequiredFilesUi();
        }
    }

    /// <summary>
    /// 下载樱花 Frp 引擎、全部组网组件与联机补丁；有可用实例时顺带把补丁解压到游戏根目录。
    /// 没有实例也能下载（只下载、不安装）。
    /// </summary>
    private async Task DownloadAndInstallRequiredAsync()
    {
        if (_busy) return;

        var instance = InstanceManager.Current;
        var canInstall = instance is not null
                         && !string.IsNullOrWhiteSpace(instance.GameDir)
                         && Directory.Exists(instance.GameDir);

        _busy = true;
        RefreshRequiredFilesUi();

        var progress = ProgressWindow.Open(OwnerWindow, "联机必要文件", "正在准备…", canCancel: true);

        var issues = new List<string>();
        var patchError = string.Empty;
        var downloaded = false;

        try
        {
            // 1) 樱花 Frp 引擎
            if (!MultiplayerRequiredFiles.IsFrpcReady)
            {
                progress.SetDetail("正在下载樱花 Frp 引擎…");

                var frpc = await MultiplayerHub.SakuraFrpc.EnsureFrpcAsync(progress.Token);

                if (!frpc.Ok) issues.Add($"樱花 Frp 引擎：{frpc.Message}");
            }

            // 2) 全部组网组件（runtime 不再随包分发）
            foreach (var component in RuntimeComponents.All)
            {
                if (RuntimeComponents.IsInstalled(component)) continue;

                progress.SetDetail($"正在下载 {RuntimeComponents.DisplayName(component)}…");

                var (ok, message) = await RuntimeComponents.EnsureAsync(component,
                    SettingsStore.Current.LauncherUpdateSource, progress.Progress, progress.Sample, progress.Token);

                if (!ok) issues.Add($"{RuntimeComponents.DisplayName(component)}：{message}");
            }

            // 3) 联机补丁：先下载（已下载会跳过）
            progress.SetDetail("正在下载 MO 联机补丁…");

            var download = await MultiplayerRequiredFiles.EnsurePatchAsync(
                SettingsStore.Current.LauncherUpdateSource, progress.Progress, progress.Token);

            downloaded = download.Success;

            if (!download.Success)
            {
                patchError = download.Message;
            }
            else if (canInstall && !MultiplayerRequiredFiles.IsPatchInstalled(instance))
            {
                // 4) 有可用实例才解压安装
                progress.SetDetail("正在安装联机补丁到游戏目录…");

                var (ok, message) = MultiplayerRequiredFiles.InstallPatch(instance, progress.Sample, progress.Token);
                if (!ok) patchError = message;
            }
        }
        catch (OperationCanceledException)
        {
            patchError = "操作已取消";
        }
        catch (Exception ex)
        {
            Log.Error("下载联机必要文件失败", ex);
            patchError = ex.Message;
        }
        finally
        {
            progress.Finish();
            _busy = false;
        }

        RefreshRequiredFilesUi();

        var extra = issues.Count == 0 ? string.Empty : $"（未就绪：{string.Join("；", issues)}）";

        if (MultiplayerRequiredFiles.IsPatchInstalled(instance))
        {
            ShowNotice($"联机必要文件已就绪，联机功能已启用{extra}");
        }
        else if (downloaded && !canInstall)
        {
            ShowNotice($"联机必要文件已下载。创建并选择游戏实例后，可回来点「安装联机补丁」。{extra}");
        }
        else
        {
            ShowNotice($"联机补丁未安装：{patchError}", isError: true);
        }
    }

    /// <summary>把已下载的补丁单独解压安装（重新安装 / 之前只下载没装时用）。</summary>
    private async void OnInstallPatchClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var instance = InstanceManager.Current;

        if (instance is null)
        {
            ShowNotice("请先在「游戏实例」页创建并选择一个游戏实例。", isError: true);
            return;
        }

        if (!MultiplayerRequiredFiles.HasPatch)
        {
            ShowNotice("还没有下载联机补丁，请先点「下载联机必要文件」。", isError: true);
            return;
        }

        if (MultiplayerRequiredFiles.IsPatchInstalled(instance))
        {
            ShowNotice("联机补丁已安装，无需重复安装。");
            return;
        }

        var answer = ChoiceWindow.Confirm(OwnerWindow, "安装联机补丁",
            $"将把联机补丁解压到当前实例的游戏根目录：\n{instance.GameDir}",
            confirmText: "安装", cancelText: "取消");

        if (!answer) return;

        _busy = true;
        RefreshRequiredFilesUi();

        var progress = ProgressWindow.Open(OwnerWindow, "安装联机补丁", "正在解压到游戏目录…", canCancel: true);

        try
        {
            var result = await Task.Run(
                () => MultiplayerRequiredFiles.InstallPatch(instance, progress.Sample, progress.Token), progress.Token);

            ShowNotice(result.Message, !result.Ok);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("安装已取消。", isError: true);
        }
        catch (Exception ex)
        {
            Log.Error("安装联机补丁失败", ex);
            ShowNotice($"安装失败：{ex.Message}", isError: true);
        }
        finally
        {
            progress.Finish();
            _busy = false;
            RefreshRequiredFilesUi();
        }
    }

    // ————— 更多下载（survive 分支的 download.json） —————

    private void OnManifestRefreshClick(object sender, RoutedEventArgs e) => _ = LoadManifestAsync();

    /// <summary>拉取并渲染「下载页文件」。失败只在卡片里提示，不打断页面。</summary>
    private async Task LoadManifestAsync()
    {
        if (_manifestLoading) return;
        _manifestLoading = true;

        LabManifestHint.Text = "正在获取下载列表…";
        BtnManifestRefresh.IsEnabled = false;

        try
        {
            var (items, error) = await DownloadManifest.FetchAsync(SettingsStore.Current.LauncherUpdateSource);

            if (error is not null)
            {
                PanManifestItems.Children.Clear();
                LabManifestHint.Text = $"无法获取下载列表：{error}";
                return;
            }

            BuildManifestItems(items);
        }
        catch (Exception ex)
        {
            Log.Error("获取下载列表失败", ex);
            PanManifestItems.Children.Clear();
            LabManifestHint.Text = $"无法获取下载列表：{ex.Message}";
        }
        finally
        {
            BtnManifestRefresh.IsEnabled = true;
            _manifestLoading = false;
        }
    }

    /// <summary>把「下载页文件」的条目渲染成一行行「名称 + 打开链接 + 下载」。</summary>
    private void BuildManifestItems(IReadOnlyList<ManifestEntry> items)
    {
        PanManifestItems.Children.Clear();

        if (items.Count == 0)
        {
            LabManifestHint.Text = "下载列表暂无条目：在仓库 survive 分支的 download.json 里按 "
                                   + "{\"name\":\"名称\",\"url\":\"下载地址\",\"note\":\"说明（可选）\"} 添加即可。";
            return;
        }

        LabManifestHint.Text = $"共 {items.Count} 项，来自 survive 分支的 download.json。";

        foreach (var entry in items)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, ToolTip = string.Join("\n", entry.Urls) };

            var name = new TextBlock
            {
                Text = entry.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            text.Children.Add(name);

            if (entry.Note.Length > 0)
            {
                var note = new TextBlock
                {
                    Text = entry.Note,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
                text.Children.Add(note);
            }

            var open = new OutlineButton
            {
                Content = "打开链接",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            open.Click += (_, _) => OpenManifestEntry(entry);

            var download = new OutlineButton
            {
                Content = entry.Urls.Count > 1 ? $"下载（{entry.Urls.Count} 卷）" : "下载",
                Tone = ButtonTone.Solid,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            download.Click += async (_, _) => await DownloadManifestEntryAsync(entry);

            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(text, 0);
            Grid.SetColumn(open, 1);
            Grid.SetColumn(download, 2);

            row.Children.Add(text);
            row.Children.Add(open);
            row.Children.Add(download);

            PanManifestItems.Children.Add(row);
        }
    }

    private void OpenManifestEntry(ManifestEntry entry)
    {
        try
        {
            ShellHelper.OpenUrl(entry.PrimaryUrl);
        }
        catch (Exception ex)
        {
            ShowNotice($"打开链接失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>下载「下载页文件」里的条目到本机 Downloads 目录；多分卷会按顺序合并成一个文件。</summary>
    private async Task DownloadManifestEntryAsync(ManifestEntry entry)
    {
        if (_busy) return;

        _busy = true;

        var target = Path.Combine(Paths.Downloads, ManifestFileName(entry));
        var total = entry.Urls.Count;
        var progress = ProgressWindow.Open(OwnerWindow, "下载", $"正在下载 {entry.Name}…", canCancel: true);

        try
        {
            Directory.CreateDirectory(Paths.Downloads);

            if (total <= 1)
            {
                var single = await ResumableDownloader.DownloadAsync(
                    entry.PrimaryUrl, target, progress.Progress, progress.Token);

                if (single.Success) ShowNotice($"已下载：{single.FilePath}");
                else ShowNotice($"下载失败：{single.Message}", isError: true);

                return;
            }

            // 多分卷：逐卷下到临时文件，再按顺序合并
            var parts = new List<string>();

            for (var index = 0; index < total; index++)
            {
                progress.SetDetail($"正在下载 {entry.Name}（第 {index + 1}/{total} 卷）…");

                var partPath = $"{target}.part{index + 1:000}";
                var result = await ResumableDownloader.DownloadAsync(
                    entry.Urls[index], partPath, progress.Progress, progress.Token);

                if (!result.Success)
                {
                    ShowNotice($"第 {index + 1}/{total} 卷下载失败：{result.Message}", isError: true);
                    return;
                }

                parts.Add(partPath);
            }

            progress.SetDetail($"正在合并 {total} 个分卷…");

            await using (var output = File.Create(target))
            {
                foreach (var part in parts)
                {
                    await using var input = File.OpenRead(part);
                    await input.CopyToAsync(output, progress.Token);
                }
            }

            foreach (var part in parts)
            {
                try { File.Delete(part); }
                catch { /* 清不掉也无妨 */ }
            }

            ShowNotice($"已下载并合并 {total} 个分卷：{target}");
        }
        catch (Exception ex)
        {
            Log.Error($"下载条目失败：{entry.Name}", ex);
            ShowNotice($"下载失败：{ex.Message}", isError: true);
        }
        finally
        {
            progress.Finish();
            _busy = false;
        }
    }

    /// <summary>从首个链接末段推出保存文件名；多分卷时去掉分卷号，推不出就用条目名兜底。</summary>
    private static string ManifestFileName(ManifestEntry entry)
    {
        var name = FileNameFromUrl(entry.PrimaryUrl);

        if (entry.Urls.Count > 1) name = Regex.Replace(name, @"\.\d{3}$", string.Empty);

        if (name.Length == 0) name = PathGuard.SanitizeFileName(entry.Name);

        return name.Length > 0 ? name : "download.bin";
    }

    private static string FileNameFromUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return PathGuard.SanitizeFileName(Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)));
        }
        catch
        {
            // 解析不了就返回空，由调用方兜底
        }

        return string.Empty;
    }

    // ————— 提示条 —————

    private void ShowNotice(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        ActivityLog.Write(LogSource.App, message, isError ? ActivityLevel.Warn : ActivityLevel.Info);

        BarNotice.Visibility = Visibility.Visible;
        LabNotice.Text = message;

        BarNotice.SetResourceReference(Border.BackgroundProperty, isError ? "Status.DangerSoft" : "Accent.Faint");
        IconNotice.Icon = isError ? "lucide/triangle-alert" : "lucide/info";
        IconNotice.SetResourceReference(SvgIcon.IconBrushProperty, isError ? "Status.Danger" : "Accent.Base");
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => BarNotice.Visibility = Visibility.Collapsed;
}
