using System.IO;
using System.Windows;
using System.Windows.Controls;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.Backup;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Packages;

namespace HMOL.App.Pages;

/// <summary>「可用包」列表里的一行。</summary>
public sealed class AvailableRowItem
{
    public AvailableRowItem(PackageEntry entry, bool isInstalled)
    {
        Entry = entry;
        IsInstalled = isInstalled;
    }

    public PackageEntry Entry { get; }

    public string Name => Entry.Name;

    public bool IsInstalled { get; }

    public string Detail
    {
        get
        {
            var size = BackupService.FormatSize(Entry.SizeBytes);
            var kind = Entry.IsDirectory ? "文件夹" : Entry.IsArchive ? "压缩包" : "文件";

            return $"{kind} · {size}";
        }
    }
}

/// <summary>「已安装」列表里的一行。包目录里找不到源文件时只提醒，不阻止卸载。</summary>
public sealed class InstalledRowItem
{
    public InstalledRowItem(string name, PackageEntry? entry)
    {
        Name = name;
        Entry = entry;
    }

    public string Name { get; }

    public PackageEntry? Entry { get; }

    public string Detail => Entry is null
        ? "包目录里已找不到源文件，仍可按记录卸载"
        : $"来源：{Entry.FullPath}";
}

/// <summary>
/// 包管理页：四类包的可用 / 已安装列表、导入与移除包、安装（含冲突处理）与卸载。
/// 安装卸载全部交给 <see cref="PackageInstaller"/>，界面只负责确认、进度与取消。
/// </summary>
public partial class PagePackages : LauncherPage
{
    /// <summary>本页动画：键统一带 <c>pkg:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("pkg:");

    private readonly OutlineButton[] _tabs;

    private PackageType _type = PackageType.Ini;
    private bool _subscribed;

    public PagePackages()
    {
        InitializeComponent();

        _tabs = [BtnTabIni, BtnTabMap, BtnTabMission, BtnTabPlugin];

        // 分段控件的文案取自 Core 的分类定义，界面里不重复写一遍
        for (var i = 0; i < _tabs.Length; i++)
            _tabs[i].Content = PackageTypes.Of(PackageTypes.All[i]).DisplayName + " 包";

        // 入场计划：页头 → 实例条与页签 → 两个列表卡（自上而下，组内 40ms 一档，总时长 < 500ms）
        _anim.Group(0, HeaderPackages);
        _anim.Group(40, PanInstanceStrip, PanTypeTabs);
        _anim.Group(120, CardAvailable, CardInstalled);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        SwitchTab(0);
    }

    public override void OnEnter()
    {
        RefreshAll();
        _anim.Play();
    }

    /// <summary>离开页面：把带 <c>pkg:</c> 前缀的动画全部收干净。</summary>
    public override void OnLeave() => _anim.Stop();

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    /// <summary>刷新本页内容（不带动画：实例变化时只换数据，不重播入场）。</summary>
    private void RefreshAll()
    {
        UpdateInstanceStrip();
        _ = RefreshAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed) return;

        _subscribed = true;
        InstanceStore.Changed += OnStoreChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            InstanceStore.Changed -= OnStoreChanged;
        }

        _anim.Stop();
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshAll();
        else Dispatcher.InvokeAsync(RefreshAll);
    }

    // ————— 分类切换 —————
    public override int SubViewCount => _tabs.Length;

    public override void SelectSubView(int index) => SwitchTab(index);

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!int.TryParse(tag, out var index)) return;

        SwitchTab(index);
    }

    private void SwitchTab(int index)
    {
        var clamped = Math.Clamp(index, 0, PackageTypes.All.Count - 1);

        _type = PackageTypes.All[clamped];

        for (var i = 0; i < _tabs.Length; i++)
            _tabs[i].Tone = i == clamped ? ButtonTone.Solid : ButtonTone.Outline;

        var spec = PackageTypes.Of(_type);
        LabTypeHint.Text = $"包目录：{PackageTypes.DirectoryOf(_type)}\n允许的扩展名：{string.Join("、", spec.Extensions)}";

        UpdateInstanceStrip();
        _ = RefreshAsync();
    }

    private void UpdateInstanceStrip()
    {
        if (LabInstance is null) return;

        var instance = InstanceManager.Current;

        LabInstance.Text = instance is null
            ? "当前没有游戏实例：安装 / 卸载需要先在「游戏实例」页创建一个实例并设为当前实例。"
            : $"当前实例：{instance.Name}";
        LabInstancePath.Text = instance?.GameDir ?? string.Empty;
    }

    // ————— 刷新 —————
    private async Task RefreshAsync()
    {
        if (PanAvailable is null) return;

        var type = _type;

        IReadOnlyList<PackageEntry> entries;
        try
        {
            // 列举可用包（含递归统计体积）可能很慢，放后台
            entries = await Task.Run(() => PackageLibrary.List(type));
        }
        catch (Exception ex)
        {
            Log.Error($"列举包失败：{PackageTypes.DirectoryOf(type)}", ex);
            ShowNotice($"读取包目录失败：{ex.Message}", true);
            return;
        }

        // 等待期间用户可能切了分类，结果作废
        if (type != _type) return;

        var instance = InstanceManager.Current;
        var installedNames = instance?.InstalledOf(type) ?? [];
        var installedSet = new HashSet<string>(installedNames, StringComparer.OrdinalIgnoreCase);

        PanAvailable.ItemsSource = entries
            .Select(entry => new AvailableRowItem(entry, installedSet.Contains(entry.Name)))
            .ToList();
        PanAvailableEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        PanInstalled.ItemsSource = installedNames
            .Select(name => new InstalledRowItem(name,
                entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        PanInstalledEmpty.Visibility = installedNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnManageInstanceClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.SwitchToPage(NavPages.Instances);

    private void OnOpenPackageDirClick(object sender, RoutedEventArgs e)
    {
        var directory = PackageTypes.DirectoryOf(_type);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Notify($"包目录不可用：{ex.Message}", "打开包目录", MessageBoxImage.Warning);
            return;
        }

        ShellHelper.OpenFolder(directory);
    }

    // ————— 导入 / 移除包 —————
    private async void OnImportPackageClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var type = _type;
        var pattern = string.Join(";", PackageTypes.Of(type).Extensions.Select(extension => "*" + extension));

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"导入{PackageTypes.Of(type).DisplayName}包",
            Filter = $"允许的包文件 ({pattern})|{pattern}|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var source = dialog.FileName;
        var target = Path.Combine(PackageTypes.DirectoryOf(type), Path.GetFileName(source));
        var overwrite = false;

        if (File.Exists(target))
        {
            var choice = ChoiceWindow.Ask(owner, "包目录已存在同名文件",
                $"包目录里已有：\n{target}",
                "覆盖会删除包目录里的同名文件后重新导入。",
                new ChoiceOption("覆盖", "overwrite", ButtonTone.Danger),
                new ChoiceOption("取消", "cancel"));

            if (choice != "overwrite") return;
            overwrite = true;
        }

        // 单文件复制无法中断，因此不给取消按钮
        var progress = ProgressWindow.Open(owner, "导入包", $"正在导入「{Path.GetFileName(source)}」…",
            indeterminate: true, canCancel: false);

        try
        {
            var message = string.Empty;
            var ok = await Task.Run(() => PackageLibrary.Import(type, source, overwrite, out message));

            progress.Finish();
            ShowNotice(message, !ok);

            if (!ok) Notify(message, "导入包", MessageBoxImage.Warning);
            else await RefreshAsync();
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error($"导入包失败：{source}", ex);
            ShowNotice($"导入失败：{ex.Message}", true);
        }
    }

    private void OnRemovePackageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AvailableRowItem row }) return;

        var owner = Window.GetWindow(this);
        var entry = row.Entry;
        var inUse = PackageLibrary.InUseBy(_type, entry.Name);

        var detail = inUse.Count > 0
            ? $"该包已被这些实例记为已安装：{string.Join("、", inUse)}。\n移除只删除包目录里的源文件，不会动已装进游戏目录的内容，但之后无法再用它执行安装 / 卸载。"
            : "移除只删除包目录里的源文件，不影响已装进游戏目录的内容。";

        var choice = ChoiceWindow.Ask(owner, "移除包",
            $"从包目录移除「{entry.Name}」？",
            detail,
            new ChoiceOption("移除", "remove", ButtonTone.Danger),
            new ChoiceOption("取消", "cancel"));

        if (choice != "remove") return;

        var ok = PackageLibrary.Remove(_type, entry.Name, out var message);
        ShowNotice(message, !ok);
        if (!ok) Notify(message, "移除包", MessageBoxImage.Warning);

        _ = RefreshAsync();
    }

    // ————— 安装 —————
    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AvailableRowItem row }) return;

        var instance = InstanceManager.Current;
        if (instance is null)
        {
            ShowNotice("请先在「游戏实例」页创建并选择一个实例。", true);
            return;
        }

        if (!Directory.Exists(instance.GameDir))
        {
            ShowNotice($"当前实例的游戏目录不可用：{instance.GameDir}", true);
            return;
        }

        var owner = Window.GetWindow(this);
        var type = _type;
        var entry = row.Entry;

        PackageTarget target;
        try
        {
            target = PackageInstaller.Describe(instance, type, entry.Name);
        }
        catch (Exception ex)
        {
            Log.Error($"解析安装目标失败：{entry.Name}", ex);
            ShowNotice($"解析安装目标失败：{ex.Message}", true);
            return;
        }

        var kind = entry.IsDirectory ? "文件夹" : entry.IsArchive ? "压缩包" : "文件";
        var message = $"即将安装{kind}：\n{entry.Name}\n\n到实例「{instance.Name}」：\n{target.TargetDirectory}";

        var policy = CopyConflictPolicy.Overwrite;

        if (Directory.Exists(target.TargetDirectory) && !target.MergeIntoRoot && Directory.Exists(target.SourcePath))
        {
            // 目录形式的包会落到「游戏目录\包名」，可能整体替换已有目录，先按 Core 的能力扫一遍文件级冲突
            IReadOnlyList<string> conflicts;
            try
            {
                conflicts = PackageInstaller.ScanConflicts(target.SourcePath, target.TargetDirectory);
            }
            catch (Exception ex)
            {
                Log.Warn($"扫描安装冲突失败：{target.TargetDirectory}（{ex.Message}）");
                conflicts = [];
            }

            if (conflicts.Count == 0)
            {
                var choice = ChoiceWindow.Ask(owner, "目标已存在", message,
                    $"目标目录已存在：\n{target.TargetDirectory}\n\n未检测到文件级冲突，替换会先移走原目录再铺入新内容。",
                    new ChoiceOption("替换", "overwrite", ButtonTone.Danger),
                    new ChoiceOption("取消", "cancel"));

                if (choice != "overwrite") return;
            }
            else
            {
                var sample = string.Join("\n", conflicts.Take(10).Select(item => "  • " + item));
                if (conflicts.Count > 10) sample += $"\n  … 另外 {conflicts.Count - 10} 个";

                var detail = $"{sample}\n\n" +
                             "• 覆盖全部：用新包覆盖目标目录中的冲突文件\n" +
                             "• 跳过已有：保留目标目录中的现有文件，只装入新文件\n" +
                             "• 取消：中止本次安装";

                var choice = ChoiceWindow.Ask(owner, "检测到文件冲突",
                    $"{message}\n\n与目标目录有 {conflicts.Count} 处文件冲突（最多统计 50 条）：",
                    detail,
                    new ChoiceOption("覆盖全部", "overwrite", ButtonTone.Danger),
                    new ChoiceOption("跳过已有", "skip"),
                    new ChoiceOption("取消", "cancel"));

                if (choice is null || choice == "cancel") return;

                policy = choice == "skip" ? CopyConflictPolicy.SkipExisting : CopyConflictPolicy.Overwrite;
            }
        }
        else
        {
            var choice = ChoiceWindow.Ask(owner, "确认安装", message,
                "同名文件会先备份为 .bak-<时间戳>，安装过程可取消。",
                new ChoiceOption("安装", "install", ButtonTone.Solid),
                new ChoiceOption("取消", "cancel"));

            if (choice != "install") return;
        }

        var progress = ProgressWindow.Open(owner, "安装包", $"正在安装「{entry.Name}」…");

        try
        {
            var name = entry.Name;
            var outcome = await Task.Run(() =>
                PackageInstaller.Install(instance, type, name, policy, progress.Progress, progress.Token));

            progress.Finish();

            ShowNotice(outcome.Message, !outcome.Success);
            Notify(outcome.Message, outcome.Success ? "安装完成" : "安装未完成",
                outcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error($"安装包失败：{entry.Name}", ex);
            ShowNotice($"安装失败：{ex.Message}", true);
        }
    }

    // ————— 卸载 —————
    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledRowItem row }) return;

        var instance = InstanceManager.Current;
        if (instance is null)
        {
            ShowNotice("请先在「游戏实例」页创建并选择一个实例。", true);
            return;
        }

        var owner = Window.GetWindow(this);
        var type = _type;
        var name = row.Name;

        // 单个 .map 文件不需要原版备份；其余包优先按记录选择性卸载，没有记录时只能全量恢复
        var mapFileOnly = type == PackageType.Map && !PackageTypes.IsArchiveExtension(Path.GetExtension(name));
        var hasRecord = HasInstallRecord(instance, type, name);
        var hasOriginalBackup = Directory.Exists(BackupService.OriginalBackupPath);

        var options = new List<ChoiceOption>();
        var detail = new System.Text.StringBuilder();

        if (mapFileOnly)
        {
            detail.Append("地图文件直接从 Maps\\Custom 删除，不需要原版备份。");
            options.Add(new ChoiceOption("卸载地图文件", "selective", ButtonTone.Danger));
        }
        else
        {
            if (hasRecord)
            {
                detail.Append("• 选择性卸载：只删除该包装进游戏目录的文件，被它覆盖掉的原版文件从 MO 原版备份还原。\n");

                if (hasOriginalBackup)
                    options.Add(new ChoiceOption("选择性卸载", "selective", ButtonTone.Solid));
                else
                    detail.Append("  注意：还没有 MO 原版备份，无法还原被覆盖的原版文件，只能选全量恢复。\n");
            }
            else
            {
                detail.Append("包目录里没有找到该包的精确安装记录，无法选择性卸载。\n");
            }

            detail.Append("• 全量恢复原版：清空当前实例的游戏目录，再用 MO 原版备份整体覆盖，" +
                          "实例上安装的所有包都会被移除（破坏性操作）。\n");

            if (PathGuard.IsInside(instance.GameDir, instance.InstallRecordsDirectory))
                detail.Append("  ⚠️ 该实例的安装记录就位于游戏目录内，全量恢复后会被清除。\n");

            if (hasOriginalBackup)
                options.Add(new ChoiceOption("全量恢复原版", "full", ButtonTone.Danger));
            else
                detail.Append($"  但当前没有 MO 原版备份（{BackupService.OriginalBackupPath}），请先执行「备份原版游戏」。");
        }

        options.Add(new ChoiceOption("取消", "cancel"));

        var choice = ChoiceWindow.Ask(owner, "卸载包",
            $"卸载「{name}」（{PackageTypes.Of(type).DisplayName}包）", detail.ToString(), options.ToArray());

        if (choice is null || choice == "cancel") return;

        if (choice == "full" && !hasOriginalBackup)
        {
            Notify($"未找到 MO 原版备份，无法全量恢复：\n{BackupService.OriginalBackupPath}\n\n" +
                   "请先在实例页执行「备份原版游戏」。", "卸载包", MessageBoxImage.Warning);
            return;
        }

        var progress = ProgressWindow.Open(owner, "卸载包",
            choice == "full" ? "正在从原版备份恢复…" : $"正在卸载「{name}」…");

        var success = false;
        var message = string.Empty;

        try
        {
            if (choice == "full")
            {
                // 全量恢复：Core 的 Restore 会先把现有内容搬进隔离区，失败可整体回滚
                var backupPath = BackupService.OriginalBackupPath;
                var gameDir = instance.GameDir;

                var outcome = await Task.Run(() =>
                    BackupService.Restore(backupPath, gameDir, progress.Progress, progress.Token));

                success = outcome.Success;
                message = outcome.Message;

                if (outcome.Success)
                {
                    // 游戏目录已回到原版状态，已安装列表与安装记录不再成立
                    instance.ClearInstalled();
                    InstanceStore.Save(instance);
                    InstallRecordStore.DeleteAll(instance);

                    message += $"\n\n已清空实例「{instance.Name}」的已安装记录。";
                }
            }
            else
            {
                var outcome = await Task.Run(() => PackageInstaller.Uninstall(
                    instance, type, name, allowFullRestore: false, progress.Progress, progress.Token));

                success = outcome.Success;
                message = outcome.Message;
            }
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error($"卸载包失败：{name}", ex);
            ShowNotice($"卸载失败：{ex.Message}", true);
            return;
        }

        progress.Finish();

        ShowNotice(message, !success);
        Notify(message, choice == "full" ? "全量恢复" : "卸载",
            success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        await RefreshAsync();
    }

    /// <summary>是否查得到精确安装记录（Core 会尝试「去扩展名」与「原名」两种写法）。</summary>
    private static bool HasInstallRecord(GameInstance instance, PackageType type, string packageName)
    {
        foreach (var candidate in InstallRecordStore.CandidateNames(packageName))
        {
            if (InstallRecordStore.Load(instance, type, candidate) is not null) return true;
        }

        return false;
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e)
        => BarNotice.Visibility = Visibility.Collapsed;

    // ————— 提示 —————
    private void ShowNotice(string text, bool isError)
    {
        if (BarNotice is null) return;

        LabNotice.Text = text;
        BarNotice.Visibility = Visibility.Visible;

        BarNotice.SetResourceReference(BackgroundProperty, isError ? "Status.DangerSoft" : "Accent.Faint");
        IconNotice.Icon = isError ? "lucide/triangle-alert" : "lucide/info";
        IconNotice.SetResourceReference(SvgIcon.IconBrushProperty, isError ? "Status.Danger" : "Accent.Base");
    }

    private void Notify(string message, string title, MessageBoxImage icon)
    {
        var owner = Window.GetWindow(this);

        switch (icon)
        {
            case MessageBoxImage.Error:
                ChoiceWindow.Error(owner, title, message);
                break;
            case MessageBoxImage.Warning:
                ChoiceWindow.Warn(owner, title, message);
                break;
            default:
                ChoiceWindow.Info(owner, title, message);
                break;
        }
    }
}
