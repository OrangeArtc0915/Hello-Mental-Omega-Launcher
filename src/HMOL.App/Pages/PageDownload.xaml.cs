using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
using HMOL.Core.Packages;
using HMOL.Core.Updater;
using HMOL.Core.Localization;

namespace HMOL.App.Pages;

/// <summary>
/// 下载页：按仓库 survive 分支的 download.json 自动生成分组与条目（组网组件 / 补丁 / 运行库官网入口…）。
/// 页面不预置任何条目：加 / 改 / 删条目、调整分组或地址都只改那份配置，不必发新版本。
///
/// <para>
/// 每条的去向由配置里的 target 决定：runtime（解压回 exe 旁的 <c>runtime\</c>）、plugin（放进「插件」包）、
/// instance（选实例解压到游戏目录并打开包内 exe）、不填则存到下载目录。
/// 只提供运行库与补丁，不提供任何游戏资源。
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

    /// <summary>最近一次拉到的条目。重新渲染分组时用它，不必再联网。</summary>
    private IReadOnlyList<ManifestEntry> _entries = [];

    /// <summary>展开着的分组名。重渲染时保持展开状态，不折回去。</summary>
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    public PageDownload()
    {
        InitializeComponent();

        _anim.Group(0, HeaderDownload);
        _anim.Group(80, BarNotice);

        RefreshDownloadDirectory();
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    public override void OnEnter()
    {
        RefreshDownloadDirectory();
        _ = LoadManifestAsync();
        _anim.Play();
    }

    public override void OnLeave() => _anim.Stop();

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    // ————— 下载目录 —————

    /// <summary>「更多下载」文件的保存目录：设置里选了就用它，否则用默认下载目录。</summary>
    private static string DownloadTargetDirectory()
    {
        var custom = SettingsStore.Current.DownloadDirectory;
        return string.IsNullOrWhiteSpace(custom) ? Paths.Downloads : custom;
    }

    private void RefreshDownloadDirectory()
    {
        if (LabDownloadDir is null) return;

        var directory = DownloadTargetDirectory();
        LabDownloadDir.Text = directory;
        LabDownloadDir.ToolTip = directory;
    }

    /// <summary>让用户挑一个目录保存「更多下载」里的文件；补丁与组件缓存不受影响。</summary>
    private void OnChangeDownloadDirClick(object sender, RoutedEventArgs e)
    {
        var current = DownloadTargetDirectory();

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("选择下载目录"),
            Multiselect = false
        };

        if (Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(OwnerWindow) != true) return;

        var selected = dialog.FolderName?.Trim();
        if (string.IsNullOrWhiteSpace(selected)) return;

        try
        {
            Directory.CreateDirectory(selected);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("下载目录不可用：{0}", ex.Message), isError: true);
            return;
        }

        SettingsStore.Current.DownloadDirectory = selected;
        SettingsStore.Save();

        RefreshDownloadDirectory();
        ShowNotice(Loc.F("下载目录已改为：{0}", selected));
    }

    private void OnOpenDownloadDirClick(object sender, RoutedEventArgs e)
    {
        var directory = DownloadTargetDirectory();

        try
        {
            Directory.CreateDirectory(directory);
            ShellHelper.OpenFolder(directory);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("打开下载目录失败：{0}", ex.Message), isError: true);
        }
    }

    // ————— 更多下载（survive 分支的 download.json） —————

    private void OnManifestRefreshClick(object sender, RoutedEventArgs e) => _ = LoadManifestAsync();

    /// <summary>拉取并渲染「下载页文件」。失败只在提示条里提示，不打断页面。</summary>
    private async Task LoadManifestAsync()
    {
        if (_manifestLoading) return;
        _manifestLoading = true;

        LabManifestHint.Text = Loc.T("正在获取下载列表…");
        BtnManifestRefresh.IsEnabled = false;

        try
        {
            var (items, error) = await DownloadManifest.FetchAsync(SettingsStore.Current.LauncherUpdateSource);

            if (error is not null)
            {
                _entries = [];
                PanGroups.Children.Clear();
                LabManifestHint.Text = Loc.F("无法获取下载列表：{0}", error);
                return;
            }

            BuildGroups(items);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("获取下载列表失败"), ex);
            _entries = [];
            PanGroups.Children.Clear();
            LabManifestHint.Text = Loc.F("无法获取下载列表：{0}", ex.Message);
        }
        finally
        {
            BtnManifestRefresh.IsEnabled = true;
            _manifestLoading = false;
        }
    }

    /// <summary>按最近一次拉到的条目重渲染分组（安装状态变了时用，不必再联网）。</summary>
    private void RebuildGroups()
    {
        if (_entries.Count > 0) BuildGroups(_entries);
    }

    /// <summary>
    /// 把条目按各自声明的 group 分组，每组渲染成一张默认收起的卡片。
    /// 页面不预置任何条目：加 / 改 / 删条目、调整分组或地址都只改 download.json，不必发新版本。
    /// </summary>
    private void BuildGroups(IReadOnlyList<ManifestEntry> items)
    {
        _entries = items;

        // 运行时组件（target=runtime）单独在顶部「运行环境」区块里，不参与下面的分组列表
        var runtimeItems = items.Where(entry => entry.IsRuntimeTarget).ToList();
        var others = items.Where(entry => !entry.IsRuntimeTarget).ToList();

        BuildRuntimeSection(runtimeItems);

        PanGroups.Children.Clear();

        if (others.Count == 0)
        {
            LabManifestHint.Text = runtimeItems.Count > 0
                ? Loc.T("没有其它可下载条目。")
                : Loc.T("下载列表暂无条目：在仓库 survive 分支的 download.json 里加一条即可")
                  + Loc.T("（{\"group\":\"分组\",\"name\":\"名称\",\"url\":\"下载地址\"}）。");
            return;
        }

        LabManifestHint.Text = Loc.F("共 {0} 项，来自 survive 分支的 download.json。", others.Count);

        foreach (var group in others.GroupBy(GroupNameOf))
        {
            PanGroups.Children.Add(BuildGroupCard(group.Key, group.ToList()));
        }
    }

    /// <summary>顶部「运行环境」区块：列出运行时组件与状态，并提供「一键补全」。</summary>
    private void BuildRuntimeSection(IReadOnlyList<ManifestEntry> runtimeItems)
    {
        PanRuntimeItems.Children.Clear();

        if (runtimeItems.Count == 0)
        {
            CardRuntime.Visibility = Visibility.Collapsed;
            return;
        }

        CardRuntime.Visibility = Visibility.Visible;

        var missing = runtimeItems.Count(entry => !IsRuntimeInstalled(entry));

        LabRuntimeHint.Text = missing == 0
            ? Loc.F("共 {0} 个运行时组件，都已就绪。", runtimeItems.Count)
            : Loc.F("共 {0} 个运行时组件，还缺 {1} 个；点「一键补全」按需下载并安装。", runtimeItems.Count, missing);

        BtnCompleteRuntime.Content = missing == 0 ? Loc.T("全部重装") : Loc.F("一键补全（{0}）", missing);
        BtnCompleteRuntime.IsEnabled = !_busy;

        for (var index = 0; index < runtimeItems.Count; index++)
        {
            var row = BuildEntryRow(runtimeItems[index]);
            row.Margin = new Thickness(0, index == 0 ? 0 : 10, 0, 0);
            PanRuntimeItems.Children.Add(row);
        }

        UpdateRuntimeToggleText(PanRuntimeItems.Visibility == Visibility.Visible);
    }

    private void OnToggleRuntimeClick(object sender, RoutedEventArgs e)
    {
        var expanded = PanRuntimeItems.Visibility != Visibility.Visible;

        PanRuntimeItems.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateRuntimeToggleText(expanded);
    }

    /// <summary>折叠按钮的文案跟着展开状态与条数走。</summary>
    private void UpdateRuntimeToggleText(bool expanded)
    {
        var count = PanRuntimeItems.Children.Count;
        var suffix = count > 0 ? Loc.F("（{0} 项）", count) : string.Empty;

        BtnToggleRuntime.Content = expanded ? Loc.F("收起组件列表{0}", suffix) : Loc.F("展开组件列表{0}", suffix);
    }

    /// <summary>条目属于哪一组：优先用它自己声明的 group，没写就按 target 归类。</summary>
    private static string GroupNameOf(ManifestEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Group)) return entry.Group.Trim();

        if (entry.IsRuntimeTarget) return Loc.T("组网与运行组件");
        if (entry.IsInstanceTarget) return Loc.T("游戏补丁");
        if (entry.IsLinkOnly) return Loc.T("运行库（官网下载）");
        if (entry.IsPluginTarget) return Loc.T("插件包");

        return Loc.T("其它下载");
    }

    /// <summary>一组 = 一张卡片：默认收起，点一下展开；展开状态在会话内保留。</summary>
    private SurfaceCard BuildGroupCard(string title, IReadOnlyList<ManifestEntry> entries)
    {
        var expanded = _expandedGroups.Contains(title);
        var rows = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed };

        var toggle = new OutlineButton
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = GroupToggleText(title, entries.Count, expanded)
        };

        toggle.Click += (_, _) =>
        {
            var next = rows.Visibility != Visibility.Visible;

            rows.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = GroupToggleText(title, entries.Count, next);

            if (next) _expandedGroups.Add(title);
            else _expandedGroups.Remove(title);
        };

        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(rows);

        for (var index = 0; index < entries.Count; index++)
        {
            var row = BuildEntryRow(entries[index]);
            row.Margin = new Thickness(0, index == 0 ? 12 : 10, 0, 0);
            rows.Children.Add(row);
        }

        return new SurfaceCard
        {
            Title = title,
            UseShadow = false,
            HasHoverEffect = false,
            Margin = new Thickness(0, 12, 0, 0),
            Content = panel
        };
    }

    private static string GroupToggleText(string title, int count, bool expanded)
        => expanded ? Loc.F("收起「{0}」（{1} 项）", title, count) : Loc.F("展开「{0}」（{1} 项）", title, count);

    /// <summary>一条目一行：名称 + 说明 + 状态（runtime 组件）+ 下载 / 打开链接。</summary>
    private FrameworkElement BuildEntryRow(ManifestEntry entry)
    {
        var panel = new StackPanel { ToolTip = entry.Urls.Count > 0 ? string.Join("\n", entry.Urls) : null };

        var name = new TextBlock { Text = entry.Name, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        panel.Children.Add(name);

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
            panel.Children.Add(note);
        }

        // 纯官网条目：不托管文件，只给一条可点击的官网地址
        if (entry.IsLinkOnly)
        {
            var link = new TextBlock
            {
                Text = entry.Homepage,
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                Cursor = Cursors.Hand,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = Loc.T("点击用浏览器打开官网")
            };
            link.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Base");
            link.MouseLeftButtonUp += (_, _) => OpenManifestEntry(entry);
            panel.Children.Add(link);

            return panel;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        if (entry.IsRuntimeTarget)
        {
            var installed = IsRuntimeInstalled(entry);

            var status = new TextBlock
            {
                Text = installed ? Loc.T("已安装") : Loc.T("未安装"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            status.SetResourceReference(TextBlock.ForegroundProperty, installed ? "Status.Success" : "Text.Tertiary");

            actions.Children.Add(status);
        }

        actions.Children.Add(BuildDownloadButton(entry));

        if (entry.Urls.Count > 0)
        {
            var open = new OutlineButton
            {
                Content = Loc.T("打开链接"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            open.Click += (_, _) => OpenManifestEntry(entry);
            actions.Children.Add(open);
        }

        panel.Children.Add(actions);

        return panel;
    }

    /// <summary>下载按钮：runtime 组件已装过时显示「重新下载」。</summary>
    private OutlineButton BuildDownloadButton(ManifestEntry entry)
    {
        var installed = entry.IsRuntimeTarget && IsRuntimeInstalled(entry);

        var button = new OutlineButton
        {
            Content = entry.Urls.Count > 1 ? Loc.F("下载（{0} 卷）", entry.Urls.Count) : installed ? Loc.T("重新下载") : Loc.T("下载"),
            Tone = ButtonTone.Solid,
            VerticalAlignment = VerticalAlignment.Center
        };

        button.Click += async (_, _) => await DownloadManifestEntryAsync(entry);
        return button;
    }

    /// <summary>runtime 类条目的安装目录：exe 旁 runtime\&lt;folder&gt;\。</summary>
    private static string RuntimeInstallDirectory(ManifestEntry entry)
        => Path.Combine(RuntimeLocator.RuntimeRoot, entry.Folder.Trim());

    /// <summary>runtime 类条目装没装：看 marker 文件在不在。</summary>
    private static bool IsRuntimeInstalled(ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder) || string.IsNullOrWhiteSpace(entry.Marker)) return false;

        try { return File.Exists(Path.Combine(RuntimeInstallDirectory(entry), entry.Marker.Trim())); }
        catch { return false; }
    }

    private void OpenManifestEntry(ManifestEntry entry)
    {
        try
        {
            ShellHelper.OpenUrl(entry.EffectiveUrl);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.F("打开链接失败：{0}", ex.Message), isError: true);
        }
    }

    /// <summary>下载条目。按条目声明的去向分流：实例安装 / runtime 解压 / 插件包 / 下载目录。</summary>
    private async Task DownloadManifestEntryAsync(ManifestEntry entry)
    {
        if (_busy) return;

        // 纯官网条目没有下载按钮，不该走到这里
        if (entry.IsLinkOnly || entry.Urls.Count == 0) return;

        // 「黑屏补丁」这类条目：下载后让用户选实例、装到其游戏目录并打开包内 exe
        if (entry.IsInstanceTarget)
        {
            await DownloadAndInstallToInstanceAsync(entry);
            return;
        }

        // 组网组件这类：解压到 exe 旁 runtime\<folder>\
        if (entry.IsRuntimeTarget)
        {
            await DownloadRuntimeEntryAsync(entry);
            return;
        }

        _busy = true;

        // 「插件」类条目直接存进插件包目录，可在「包管理 → 插件」里安装
        var directory = entry.IsPluginTarget
            ? PackageTypes.DirectoryOf(PackageType.Plugin)
            : DownloadTargetDirectory();

        var target = Path.Combine(directory, ManifestFileName(entry));
        var total = entry.Urls.Count;
        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("下载"), Loc.F("正在下载 {0}…", entry.Name), canCancel: true);

        try
        {
            Directory.CreateDirectory(directory);

            if (total <= 1)
            {
                var single = await ResumableDownloader.DownloadAsync(
                    entry.PrimaryUrl, target, progress.Progress, progress.Token);

                if (single.Success) ShowNotice(DescribeManifestDownload(entry, single.FilePath ?? target));
                else ShowNotice(Loc.F("下载失败：{0}", single.Message), isError: true);

                return;
            }

            // 多分卷：逐卷下到临时文件，再按顺序合并
            var parts = new List<string>();

            for (var index = 0; index < total; index++)
            {
                progress.SetDetail(Loc.F("正在下载 {0}（第 {1}/{2} 卷）…", entry.Name, index + 1, total));

                var partPath = $"{target}.part{index + 1:000}";
                var result = await ResumableDownloader.DownloadAsync(
                    entry.Urls[index], partPath, progress.Progress, progress.Token);

                if (!result.Success)
                {
                    ShowNotice(Loc.F("第 {0}/{1} 卷下载失败：{2}", index + 1, total, result.Message), isError: true);
                    return;
                }

                parts.Add(partPath);
            }

            progress.SetDetail(Loc.F("正在合并 {0} 个分卷…", total));

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

            ShowNotice(entry.IsPluginTarget
                ? Loc.F("已放进插件包：{0}（{1} 个分卷已合并，可在「包管理 → 插件」里安装）", target, total)
                : Loc.F("已下载并合并 {0} 个分卷：{1}", total, target));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("下载条目失败：{0}", entry.Name), ex);
            ShowNotice(Loc.F("下载失败：{0}", ex.Message), isError: true);
        }
        finally
        {
            progress.Finish();
            _busy = false;
        }
    }

    /// <summary>下载并解压一个 runtime 组件到 exe 旁的 runtime\&lt;folder&gt;\，按 marker 复核是否装好。</summary>
    private async Task DownloadRuntimeEntryAsync(ManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Folder) || string.IsNullOrWhiteSpace(entry.Marker))
        {
            ShowNotice(Loc.F("{0} 的配置不完整（缺 folder 或 marker），无法自动安装。", entry.Name), isError: true);
            return;
        }

        if (entry.Urls.Count > 1)
        {
            ShowNotice(Loc.F("{0} 是多分卷条目，暂不支持自动安装，请点「打开链接」手动下载。", entry.Name), isError: true);
            return;
        }

        _busy = true;
        RebuildGroups();

        var archive = Path.Combine(Paths.Cache, ManifestFileName(entry));
        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("下载组件"), Loc.F("正在下载 {0}…", entry.Name), canCancel: true);

        try
        {
            var download = await ResumableDownloader.DownloadAsync(
                entry.PrimaryUrl, archive, progress.Progress, progress.Token);

            if (!download.Success)
            {
                ShowNotice(Loc.F("下载失败：{0}", download.Message), isError: true);
                return;
            }

            progress.SetDetail(Loc.F("正在解压到 runtime\\{0}\\…", entry.Folder));

            var directory = RuntimeInstallDirectory(entry);

            var (ok, message) = await Task.Run(
                () => RequiredAssetDownloader.ExtractInto(archive, directory, progress.Sample, progress.Token),
                progress.Token);

            if (!ok)
            {
                ShowNotice(Loc.F("安装失败：{0}", message), isError: true);
                return;
            }

            if (!IsRuntimeInstalled(entry))
            {
                ShowNotice(Loc.F("{0} 解压完成，但没找到标志文件 {1}，请确认压缩包内容。", entry.Name, entry.Marker), isError: true);
                return;
            }

            // 7-Zip 组件的定位结果有缓存，装完要让下一次查找重新找
            if (string.Equals(entry.Folder.Trim(), "7zip", StringComparison.OrdinalIgnoreCase))
                SevenZipTool.ResetCache();

            ShowNotice(Loc.F("{0} 已安装到 runtime\\{1}\\。", entry.Name, entry.Folder));
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.T("下载已取消。"), isError: true);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("安装组件失败：{0}", entry.Name), ex);
            ShowNotice(Loc.F("安装失败：{0}", ex.Message), isError: true);
        }
        finally
        {
            progress.Finish();
            _busy = false;
            RebuildGroups();

            try { if (File.Exists(archive)) File.Delete(archive); }
            catch { /* 清不掉也无妨 */ }
        }
    }

    private void OnCompleteRuntimeClick(object sender, RoutedEventArgs e) => _ = CompleteRuntimeAsync();

    /// <summary>
    /// 一键补全：把当前缺的运行时组件逐个下载并解压到 runtime\&lt;folder&gt;\，已装的跳过。
    /// 组件清单与地址都由 download.json 决定，这里只照单执行。
    /// </summary>
    private async Task CompleteRuntimeAsync()
    {
        if (_busy) return;

        var runtimeItems = _entries.Where(entry => entry.IsRuntimeTarget).ToList();
        var pending = runtimeItems.Where(entry => !IsRuntimeInstalled(entry)).ToList();

        if (pending.Count == 0)
        {
            ShowNotice(Loc.T("运行时组件都已就绪，无需补全。"));
            return;
        }

        _busy = true;
        RebuildGroups();

        var issues = new List<string>();
        var installed = 0;
        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("一键补全运行环境"), Loc.T("正在准备…"), canCancel: true);

        try
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var entry = pending[index];

                if (string.IsNullOrWhiteSpace(entry.Folder) || string.IsNullOrWhiteSpace(entry.Marker))
                {
                    issues.Add(Loc.F("{0}：缺 folder 或 marker", entry.Name));
                    continue;
                }

                if (entry.Urls.Count > 1)
                {
                    issues.Add(Loc.F("{0}：多分卷条目暂不支持自动安装", entry.Name));
                    continue;
                }

                progress.SetDetail(Loc.F("正在下载 {0}（{1}/{2}）…", entry.Name, index + 1, pending.Count));

                var archive = Path.Combine(Paths.Cache, ManifestFileName(entry));

                var download = await ResumableDownloader.DownloadAsync(
                    entry.PrimaryUrl, archive, progress.Progress, progress.Token);

                if (!download.Success)
                {
                    issues.Add($"{entry.Name}：{download.Message}");
                    continue;
                }

                progress.SetDetail(Loc.F("正在解压 {0}…", entry.Name));

                var (ok, message) = await Task.Run(
                    () => RequiredAssetDownloader.ExtractInto(archive, RuntimeInstallDirectory(entry), progress.Sample, progress.Token),
                    progress.Token);

                try { if (File.Exists(archive)) File.Delete(archive); }
                catch { /* 清不掉也无妨 */ }

                if (!ok) { issues.Add($"{entry.Name}：{message}"); continue; }

                if (!IsRuntimeInstalled(entry))
                {
                    issues.Add(Loc.F("{0}：解压完成但没找到 {1}", entry.Name, entry.Marker));
                    continue;
                }

                installed++;
            }
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.F("补全已取消（已完成 {0} 个）。", installed), isError: true);
            return;
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("一键补全运行环境失败"), ex);
            ShowNotice(Loc.F("补全失败：{0}", ex.Message), isError: true);
            return;
        }
        finally
        {
            progress.Finish();
            _busy = false;
            RebuildGroups();
        }

        // 7-Zip 组件的定位结果有缓存，补全后让下一次查找重新找
        SevenZipTool.ResetCache();

        if (issues.Count == 0) ShowNotice(Loc.F("运行环境已补全（{0} 个组件）。", installed));
        else ShowNotice(Loc.F("补全完成：成功 {0} 个，失败 {1} 个 —— {2}", installed, issues.Count, string.Join(Loc.T("；"), issues)), isError: true);
    }

    /// <summary>下载成功后的提示：进插件包的条目说明它能在「包管理 → 插件」里安装。</summary>
    private static string DescribeManifestDownload(ManifestEntry entry, string path)
        => entry.IsPluginTarget
            ? Loc.F("已放进插件包：{0}（可在「包管理 → 插件」里安装）", path)
            : Loc.F("已下载：{0}", path);

    /// <summary>
    /// 「黑屏补丁」这类条目：下载到缓存，让用户选一个游戏实例，把压缩包解压到它的游戏根目录，
    /// 装完自动打开包内的 exe（如 cnc-ddraw 自带的 cnc-ddraw config.exe）。
    /// </summary>
    private async Task DownloadAndInstallToInstanceAsync(ManifestEntry entry)
    {
        var instances = InstanceStore.All;

        if (instances.Count == 0)
        {
            ShowNotice(Loc.T("还没有游戏实例：请先在「游戏实例」页创建实例，再回来安装。"), isError: true);
            return;
        }

        if (entry.Urls.Count > 1)
        {
            ShowNotice(Loc.F("{0} 是多分卷条目，暂不支持直接安装，请点「打开链接」手动下载。", entry.Name), isError: true);
            return;
        }

        var items = instances
            .Select(instance => new PickItem(instance.Id, instance.Name,
                string.IsNullOrWhiteSpace(instance.GameDir) ? Loc.T("（未设置游戏目录）") : instance.GameDir))
            .ToList();

        var picked = PickWindow.Pick(OwnerWindow, entry.Name, Loc.T("选择要安装到哪个游戏实例："), items, PickMode.Single);

        if (picked is null || picked.Count == 0) return;

        var target = instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, picked[0], StringComparison.OrdinalIgnoreCase));

        if (target is null) return;

        if (string.IsNullOrWhiteSpace(target.GameDir) || !Directory.Exists(target.GameDir))
        {
            ShowNotice(Loc.F("实例「{0}」的游戏目录不可用：{1}", target.Name, target.GameDir), isError: true);
            return;
        }

        _busy = true;
        RebuildGroups();

        var archive = Path.Combine(Paths.Cache, ManifestFileName(entry));
        var progress = ProgressWindow.Open(OwnerWindow, Loc.T("安装到实例"), Loc.F("正在下载 {0}…", entry.Name), canCancel: true);

        try
        {
            var download = await ResumableDownloader.DownloadAsync(
                entry.PrimaryUrl, archive, progress.Progress, progress.Token);

            if (!download.Success)
            {
                ShowNotice(Loc.F("下载失败：{0}", download.Message), isError: true);
                return;
            }

            progress.SetDetail(Loc.F("正在解压到「{0}」的游戏目录…", target.Name));

            var (ok, message) = await Task.Run(
                () => RequiredAssetDownloader.ExtractInto(archive, target.GameDir, progress.Sample, progress.Token),
                progress.Token);

            if (!ok)
            {
                ShowNotice(Loc.F("安装失败：{0}", message), isError: true);
                return;
            }

            ShowNotice(Loc.F("{0} 已安装到「{1}」，正在打开它自带的程序。", entry.Name, target.Name));

            LaunchArchiveExe(archive, target.GameDir);
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.T("安装已取消。"), isError: true);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("安装到实例失败：{0}", entry.Name), ex);
            ShowNotice(Loc.F("安装失败：{0}", ex.Message), isError: true);
        }
        finally
        {
            progress.Finish();
            _busy = false;
            RebuildGroups();

            try { if (File.Exists(archive)) File.Delete(archive); }
            catch { /* 清不掉也无妨 */ }
        }
    }

    /// <summary>装完打开压缩包里的 exe（补丁自带的程序，如 cnc-ddraw config.exe）。</summary>
    private static void LaunchArchiveExe(string archivePath, string gameDirectory)
    {
        try
        {
            var relative = ArchiveExtractor.ListEntries(archivePath, maxCount: 1000)
                .FirstOrDefault(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            if (relative is null) return;

            var path = Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                Log.Warn(Loc.F("补丁内的 exe 没找到：{0}", path));
                return;
            }

            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? gameDirectory
            });

            Log.Info(Loc.F("已打开补丁内的程序：{0}", path));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("打开补丁内的 exe 失败：{0}", ex.Message));
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
