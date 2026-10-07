using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Controls.Svg;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.Backup;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using Microsoft.Win32;
using HMOL.Core.Localization;

namespace HMOL.App.Pages;

/// <summary>实例卡片的数据。体积要后台算，所以只有它带变更通知。</summary>
public sealed class InstanceCardItem : INotifyPropertyChanged
{
    private string _sizeText = Loc.T("占用体积：计算中…");

    public InstanceCardItem(GameInstance instance)
    {
        Instance = instance;
        IconImage = GameIconProvider.Resolve(instance);
    }

    public GameInstance Instance { get; }

    /// <summary>卡片图标：按游戏类型取（心灵终结 / 原版 / 尤复 / 其它 Mod 的主程序图标）。</summary>
    public ImageSource? IconImage { get; }

    /// <summary>取到游戏图标时显示图片，否则退回矢量图标。</summary>
    public bool HasIconImage => IconImage is not null;

    public bool HasFallbackIcon => IconImage is null;

    public string Name => Instance.Name;

    public string GameDir => Instance.GameDir;

    public string Summary => Instance.Summary;

    public string Note => Instance.Note;

    public bool HasNote => !string.IsNullOrWhiteSpace(Instance.Note);

    public bool IsValid => Instance.IsValid;

    public bool IsCurrent => ReferenceEquals(Instance, InstanceStore.Current);

    public string SizeText
    {
        get => _sizeText;
        set
        {
            _sizeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>备份列表里的一行。列表每次刷新整体重建，选中态由重建时带入。</summary>
public sealed class BackupRowItem
{
    public BackupRowItem(BackupEntry entry, bool isSelected)
    {
        Entry = entry;
        IsSelected = isSelected;
    }

    public BackupEntry Entry { get; }

    public bool IsSelected { get; }

    public string Title => Entry.Kind == BackupKind.Original ? Loc.T("原版游戏（MO）") : Entry.Name;

    public string KindText => Entry.KindText;

    public string Icon => Entry.Kind == BackupKind.Original ? "lucide/file-box" : "lucide/save";

    public string Detail
    {
        get
        {
            var created = Entry.CreatedAt is null ? Loc.T("时间未知") : Entry.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm");
            var source = string.IsNullOrWhiteSpace(Entry.SourceInstance) ? string.Empty : Loc.F(" · 来源：{0}", Entry.SourceInstance);

            return Loc.F("{0} 个文件 · {1} · {2}{3}", Entry.FileCount, BackupService.FormatSize(Entry.SizeBytes), created, source);
        }
    }
}

/// <summary>
/// 实例页：实例的增删改查、导入导出、启动，以及备份 / 还原区块。
/// 所有落盘动作都走 <see cref="InstanceManager"/> 与 <see cref="BackupService"/>。
/// </summary>
public partial class PageInstances : LauncherPage
{
    /// <summary>本页动画：键统一带 <c>inst:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("inst:");

    private List<InstanceCardItem> _items = [];
    private List<BackupRowItem> _backupRows = [];
    private BackupRowItem? _selectedBackup;

    private bool _subscribed;

    public PageInstances()
    {
        InitializeComponent();

        // 入场计划：页头 → 实例列表（或空态）→ 备份卡（自上而下，组内 40ms 一档，总时长 < 500ms）
        _anim.Group(0, HeaderInstances);
        _anim.Group(40, PanInstances, CardEmpty);
        _anim.Group(120, CardBackup);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        RefreshAll();
    }

    public override void OnEnter()
    {
        RefreshAll();
        _anim.Play();
    }

    /// <summary>离开页面：把带 <c>inst:</c> 前缀的动画全部收干净。</summary>
    public override void OnLeave() => _anim.Stop();

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

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

    private void RefreshAll()
    {
        RefreshInstances();
        _ = RefreshBackupsAsync();
    }

    // ————— 实例列表 —————
    private void RefreshInstances()
    {
        if (PanInstances is null) return;

        var instances = InstanceManager.All;

        _items = instances.Select(instance => new InstanceCardItem(instance)).ToList();
        PanInstances.ItemsSource = _items;

        var empty = _items.Count == 0;
        PanInstances.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CardEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        UpdateBackupTarget();
        _ = RefreshSizesAsync();
    }

    /// <summary>占用体积要遍历游戏目录，放到后台算，算完逐个回填，避免卡住界面。</summary>
    private async Task RefreshSizesAsync()
    {
        var items = _items;

        foreach (var item in items)
        {
            if (!ReferenceEquals(items, _items)) return;

            var directory = item.GameDir;
            if (!Directory.Exists(directory))
            {
                item.SizeText = Loc.T("占用体积：—（目录不存在）");
                continue;
            }

            long size;
            try
            {
                // 走 Core 的带缓存统计：同一实例重复刷新时不会反复全量扫描
                var id = item.Instance.Id;
                size = await Task.Run(() => InstanceManager.GetSize(id));
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("统计实例体积失败：{0}（{1}）", directory, ex.Message));
                size = 0;
            }

            if (!ReferenceEquals(items, _items)) return;

            item.SizeText = Loc.F("占用体积：{0}", BackupService.FormatSize(size));
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshAll();

    // ————— 实例：增删改 —————

    private void OnNewInstanceClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InstanceEditorWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var result = InstanceManager.Add(dialog.InstanceName, dialog.GameDirectory, dialog.InstanceNote,
            dialog.Kind, dialog.Executable);

        if (!result.Success)
        {
            Notify(result.Message, Loc.T("新建实例"), MessageBoxImage.Warning);
            return;
        }

        ShowNotice(result.Message, false);
        Log.Info(result.Message);
        RefreshInstances();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        var dialog = new InstanceEditorWindow(instance) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var result = InstanceManager.Update(instance.Id, dialog.InstanceName, dialog.GameDirectory, dialog.InstanceNote,
            dialog.Kind, dialog.Executable);

        if (!result.Success)
        {
            Notify(result.Message, Loc.T("编辑实例"), MessageBoxImage.Warning);
            return;
        }

        ShowNotice(Loc.F("实例「{0}」已更新", instance.Name), false);
        RefreshAll();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        var name = TextInputWindow.Ask(
            Window.GetWindow(this),
            Loc.T("重命名实例"),
            Loc.F("为实例「{0}」输入新名称：", instance.Name),
            instance.Name,
            Loc.T("名称不能为空，也不能与其它实例重名。"),
            value => ValidateName(instance, value));

        if (name is null) return;

        var result = InstanceManager.Rename(instance.Id, name);

        if (!result.Success)
        {
            Notify(result.Message, Loc.T("重命名实例"), MessageBoxImage.Warning);
            return;
        }

        ShowNotice(result.Message, false);
        RefreshAll();
    }

    private static string? ValidateName(GameInstance instance, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Loc.T("实例名称不能为空。");

        var duplicated = InstanceManager.All.Any(item =>
            !string.Equals(item.Id, instance.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase));

        return duplicated ? Loc.F("实例名称「{0}」已存在。", value) : null;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        // 导入的实例，游戏目录就在实例数据目录里，删除实例会一并删掉游戏文件，必须提前说清
        var gameInsideInstance = PathGuard.IsInside(instance.InstanceDirectory, instance.GameDir);

        var detail = gameInsideInstance
            ? Loc.F("⚠️ 该实例的游戏目录位于实例数据目录内，其中的游戏文件会被一并删除：\n{0}", instance.GameDir)
            : Loc.F("游戏目录本身不会被删除：\n{0}\n\n会删除实例数据目录：\n{1}", instance.GameDir, instance.InstanceDirectory);

        var choice = ChoiceWindow.Ask(
            Window.GetWindow(this),
            Loc.T("删除实例"),
            Loc.F("确定删除实例「{0}」吗？此操作不可撤销。", instance.Name),
            detail,
            new ChoiceOption(Loc.T("删除实例"), "delete", ButtonTone.Danger),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "delete") return;

        var result = InstanceManager.Remove(instance.Id);

        ShowNotice(result.Message, !result.Success);
        if (!result.Success) Notify(result.Message, Loc.T("删除实例"), MessageBoxImage.Warning);

        RefreshAll();
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        var directory = InstanceManager.DirectoryOf(instance.Id);

        if (!Directory.Exists(directory))
        {
            Notify(Loc.F("实例目录不存在：\n{0}", directory), Loc.T("打开实例目录"), MessageBoxImage.Information);
            return;
        }

        ShellHelper.OpenFolder(directory);
    }

    /// <summary>卡片生成后按「是否为当前实例」决定按钮文案与配色。</summary>
    private void OnUseButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not OutlineButton button || button.Tag is not GameInstance instance) return;

        var isCurrent = ReferenceEquals(instance, InstanceStore.Current);

        button.Content = isCurrent ? Loc.T("正在使用") : Loc.T("设为当前");
        button.Tone = isCurrent ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;
        if (ReferenceEquals(instance, InstanceStore.Current)) return;

        InstanceManager.SetCurrent(instance.Id);
        ShowNotice(Loc.F("已切换当前实例为「{0}」", instance.Name), false);
        RefreshInstances();
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        if (GameSessionHub.IsRunning)
        {
            ShowNotice(Loc.T("游戏已经在运行中，请先关闭游戏。"), true);
            return;
        }

        if (!GameSessionHub.TryLaunch(instance, out var error))
        {
            ShowNotice(error ?? Loc.T("启动失败：未知原因"), true);
            Notify(error ?? Loc.T("启动失败：未知原因"), Loc.T("启动游戏"), MessageBoxImage.Warning);
            return;
        }

        ShowNotice(Loc.F("已启动实例「{0}」，游戏输出见「运行日志」页。", instance.Name), false);
        Log.Info(Loc.F("界面：已启动实例「{0}」", instance.Name));
    }

    // ————— 实例：导入 / 导出 —————

    /// <summary>
    /// 按保存对话框里选的格式补齐后缀（已关掉 SaveFileDialog 自动补后缀）：
    /// 用户手输了可导出后缀就照用，没写后缀才按所选格式补上，写了不认识的后缀则原样交给 Core 报错。
    /// </summary>
    private static string EnsureExportExtension(string fileName, int filterIndex)
    {
        if (ArchiveFormats.IsExportable(fileName)) return fileName;

        if (!string.IsNullOrEmpty(Path.GetExtension(fileName))) return fileName;

        return fileName + (filterIndex == 3 ? ".7z" : ".zip");
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GameInstance instance }) return;

        var owner = Window.GetWindow(this);
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("导出实例"),
            FileName = $"{PathGuard.SanitizeFileName(instance.Name)}.zip",
            DefaultExt = ".zip",
            // 后缀由下面的 EnsureExportExtension 按所选格式决定；开着自动补后缀会把 7z 补成 .zip
            AddExtension = false,
            Filter = Loc.F("HMOL 实例包 ({0})|{1}|", ArchiveFormats.ExportPickPattern, ArchiveFormats.ExportPickPattern) +
                     Loc.T("ZIP 压缩包 (*.zip)|*.zip|7z 压缩包 (*.7z)|*.7z")
        };

        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var target = EnsureExportExtension(dialog.FileName, dialog.FilterIndex);

        // Core 不会覆盖已存在的文件，这里先让用户明确选择再删，避免导出到一半才报错
        if (File.Exists(target))
        {
            var choice = ChoiceWindow.Ask(owner, Loc.T("文件已存在"),
                Loc.F("目标文件已存在：\n{0}", target),
                Loc.T("覆盖会先删除该文件，然后重新导出。"),
                new ChoiceOption(Loc.T("覆盖"), "overwrite", ButtonTone.Danger),
                new ChoiceOption(Loc.T("取消"), "cancel"));

            if (choice != "overwrite") return;

            try
            {
                File.Delete(target);
            }
            catch (Exception ex)
            {
                Notify(Loc.F("无法删除已有文件：{0}", ex.Message), Loc.T("导出实例"), MessageBoxImage.Warning);
                return;
            }
        }

        // 压缩力度让用户定：GB 级实例上 Deflate 的 CPU 开销能差好几倍，而包大小通常只差几个百分点
        var levelChoice = ChoiceWindow.Ask(owner, Loc.T("导出压缩级别"),
            Loc.T("选择导出时的压缩力度。"),
            Loc.T("「快速」耗时明显更短，包会略大几个百分点——游戏资源大多本身已压过，实际差距通常很小；") +
            Loc.T("「最小」更省空间，但在大实例上会慢不少。"),
            new ChoiceOption(Loc.T("快速"), "fast", ButtonTone.Solid),
            new ChoiceOption(Loc.T("最小"), "small"));

        if (levelChoice is null) return;

        var level = levelChoice == "fast" ? CompressionLevel.Fastest : CompressionLevel.Optimal;

        var progress = ProgressWindow.Open(owner, Loc.T("导出实例"), Loc.F("正在导出「{0}」…", instance.Name));

        try
        {
            var id = instance.Id;
            var result = await Task.Run(() =>
                InstanceManager.Export(id, target, level, progress.Sample, progress.Token));

            progress.Finish();

            if (result.Success) Notify(result.Message, Loc.T("导出完成"), MessageBoxImage.Information);
            ShowNotice(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.F("导出实例失败：{0}", instance.Name), ex);
            ShowNotice(Loc.F("导出失败：{0}", ex.Message), true);
        }
    }

    private async void OnImportInstanceClick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);

        var dialog = new OpenFileDialog
        {
            Title = Loc.T("导入实例"),
            Filter = Loc.F("HMOL 实例包 ({0})|{1}|所有文件 (*.*)|*.*", ArchiveFormats.PickPattern, ArchiveFormats.PickPattern),
            CheckFileExists = true,
            Multiselect = false
        };

        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        var source = dialog.FileName;

        var choice = ChoiceWindow.Ask(owner, Loc.T("导入实例"),
            Loc.F("将从此文件导入一个新实例：\n{0}", source),
            Loc.T("导入会解压其中的游戏文件并复制到数据目录，可能耗时较久；同名实例会自动改名。"),
            new ChoiceOption(Loc.T("开始导入"), "import", ButtonTone.Solid),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "import") return;

        var progress = ProgressWindow.Open(owner, Loc.T("导入实例"), Loc.T("正在解压并复制游戏文件…"));

        try
        {
            var (result, instanceId) = await Task.Run(() =>
                InstanceManager.Import(source, progress.Sample, progress.Token));

            progress.Finish();

            if (result.Success)
            {
                if (instanceId is not null) InstanceManager.SetCurrent(instanceId);

                Notify(result.Message, Loc.T("导入完成"), MessageBoxImage.Information);
                RefreshAll();
            }
            else
            {
                Notify(result.Message, Loc.T("导入失败"), MessageBoxImage.Warning);
            }

            ShowNotice(result.Message, !result.Success);
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.F("导入实例失败：{0}", source), ex);
            ShowNotice(Loc.F("导入失败：{0}", ex.Message), true);
        }
    }

    // ————— 备份与还原 —————

    private void UpdateBackupTarget()
    {
        if (LabBackupTarget is null) return;

        var instance = InstanceManager.Current;
        var ready = instance is not null;

        LabBackupTarget.Text = ready
            ? Loc.F("备份对象 = 当前实例「{0}」\n{1}", instance!.Name, instance.GameDir)
            : Loc.T("还没有当前实例：请先把某个实例设为当前实例，再创建备份或还原。");

        BtnBackup.IsEnabled = ready;
        BtnBackupOriginal.IsEnabled = ready;
        BtnRestore.IsEnabled = ready && _selectedBackup is not null;
        BtnDeleteBackup.IsEnabled = _selectedBackup is not null;
    }

    private async Task RefreshBackupsAsync()
    {
        if (PanBackups is null) return;

        var selectedPath = _selectedBackup?.Entry.Path;

        IReadOnlyList<BackupEntry> entries;
        try
        {
            // 统计每个备份的文件数与体积要遍历目录，放后台
            entries = await Task.Run(BackupService.List);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("枚举备份失败"), ex);
            ShowNotice(Loc.F("读取备份列表失败：{0}", ex.Message), true);
            return;
        }

        _backupRows = entries
            .Select(entry => new BackupRowItem(entry, string.Equals(entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (_selectedBackup is not null && _backupRows.All(row => !row.IsSelected)) _selectedBackup = null;

        PanBackups.ItemsSource = _backupRows;
        LabBackupEmpty.Visibility = _backupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateBackupTarget();
    }

    private void OnRefreshBackupsClick(object sender, RoutedEventArgs e) => _ = RefreshBackupsAsync();

    /// <summary>备份行悬停：底色由样式触发，这里补一点轻微右移（与主页的列表行同一套反馈）。</summary>
    private void OnBackupRowEnter(object sender, MouseEventArgs e) => _anim.HoverShift(sender as FrameworkElement, 3);

    private void OnBackupRowLeave(object sender, MouseEventArgs e) => _anim.HoverShift(sender as FrameworkElement, 0);

    private void OnBackupRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BackupRowItem row }) return;

        _selectedBackup = ReferenceEquals(row, _selectedBackup) ? null : row;

        var selectedPath = _selectedBackup?.Entry.Path;
        PanBackups.ItemsSource = _backupRows
            .Select(item => new BackupRowItem(item.Entry,
                string.Equals(item.Entry.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        UpdateBackupTarget();
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        var instance = InstanceManager.Current;
        if (instance is null) return;

        var owner = Window.GetWindow(this);
        var defaultName = $"{instance.Name}_{DateTime.Now:yyyyMMdd_HHmmss}";

        var name = TextInputWindow.Ask(owner, Loc.T("创建备份"),
            Loc.F("为当前实例「{0}」创建备份：", instance.Name),
            defaultName,
            Loc.T("备份名称不能使用 MO / mo / Mo / mO / MO.mo.mO / 原版 / 原版游戏 等保留名，最多 100 字符。"),
            value => BackupService.IsValidName(value, out var error) ? null : error);

        if (name is null) return;

        var overwrite = false;
        var target = BackupService.PathOf(name);

        if (Directory.Exists(target))
        {
            var choice = ChoiceWindow.Ask(owner, Loc.T("备份已存在"),
                Loc.F("已存在同名备份：\n{0}", target),
                Loc.T("覆盖会删除原备份后重新备份，原备份不可恢复。"),
                new ChoiceOption(Loc.T("覆盖"), "overwrite", ButtonTone.Danger),
                new ChoiceOption(Loc.T("取消"), "cancel"));

            if (choice != "overwrite") return;
            overwrite = true;
        }

        var progress = ProgressWindow.Open(owner, Loc.T("备份游戏"), Loc.F("正在备份「{0}」…", instance.Name));

        try
        {
            var packages = instance.InstalledPackages
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToList());

            var outcome = await Task.Run(() => BackupService.Backup(
                instance.GameDir, name, instance.Id, instance.Name, packages, overwrite,
                progress.Sample, progress.Token));

            progress.Finish();

            if (outcome.Success) Notify(outcome.Message, Loc.T("备份完成"), MessageBoxImage.Information);
            ShowNotice(outcome.Message, !outcome.Success);

            await RefreshBackupsAsync();
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.F("备份失败：{0}", name), ex);
            ShowNotice(Loc.F("备份失败：{0}", ex.Message), true);
        }
    }

    private async void OnBackupOriginalClick(object sender, RoutedEventArgs e)
    {
        var instance = InstanceManager.Current;
        if (instance is null) return;

        var owner = Window.GetWindow(this);
        var overwrite = false;

        if (Directory.Exists(BackupService.OriginalBackupPath))
        {
            var existing = ChoiceWindow.Ask(owner, Loc.T("原版备份已存在"),
                Loc.F("已存在原版游戏备份：\n{0}", BackupService.OriginalBackupPath),
                Loc.T("原版备份是按包卸载、恢复原版状态的必要条件；覆盖会删除现有原版备份。"),
                new ChoiceOption(Loc.T("覆盖"), "overwrite", ButtonTone.Danger),
                new ChoiceOption(Loc.T("取消"), "cancel"));

            if (existing != "overwrite") return;
            overwrite = true;
        }

        var choice = ChoiceWindow.Ask(owner, Loc.T("备份原版游戏"),
            Loc.F("将把当前实例的游戏目录备份为原版游戏：\n{0}", instance.GameDir),
            Loc.T("请确认该实例当前是原版状态（未安装任何插件包）。原版备份固定放在 backup\\MO，名称不可改。"),
            new ChoiceOption(Loc.T("开始备份"), "backup", ButtonTone.Solid),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "backup") return;

        var progress = ProgressWindow.Open(owner, Loc.T("备份原版游戏"), Loc.T("正在复制原版游戏文件…"));

        try
        {
            var gameDir = instance.GameDir;
            var outcome = await Task.Run(() =>
                BackupService.BackupOriginal(gameDir, overwrite, progress.Sample, progress.Token));

            progress.Finish();

            if (outcome.Success) Notify(outcome.Message, Loc.T("原版备份完成"), MessageBoxImage.Information);
            ShowNotice(outcome.Message, !outcome.Success);

            await RefreshBackupsAsync();
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.T("备份原版游戏失败"), ex);
            ShowNotice(Loc.F("备份原版游戏失败：{0}", ex.Message), true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var instance = InstanceManager.Current;
        var backup = _selectedBackup;

        if (instance is null || backup is null) return;

        var owner = Window.GetWindow(this);

        // 导入出来的实例，游戏目录就是实例数据目录，安装记录也在里面，替换后会被一并清掉
        var recordsInsideGameDir = PathGuard.IsInside(instance.GameDir, instance.InstallRecordsDirectory);

        var detail = Loc.T("目标目录中的所有现有文件与子目录都会被替换，此操作不可撤销。") +
                     (recordsInsideGameDir
                         ? Loc.T("\n\n⚠️ 该实例的安装记录就位于游戏目录内，还原后会被清除。")
                         : string.Empty);

        var choice = ChoiceWindow.Ask(owner, Loc.T("⚠️ 还原确认"),
            Loc.F("将把备份「{0}」还原到当前实例「{1}」：\n{2}", backup.Title, instance.Name, instance.GameDir),
            detail,
            new ChoiceOption(Loc.T("确认还原"), "restore", ButtonTone.Danger),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "restore") return;

        var progress = ProgressWindow.Open(owner, Loc.T("还原备份"), Loc.F("正在从「{0}」还原…", backup.Title));

        try
        {
            var source = backup.Entry.Path;
            var target = instance.GameDir;

            var outcome = await Task.Run(() =>
                BackupService.Restore(source, target, progress.Sample, progress.Token));

            progress.Finish();

            if (outcome.Success) Notify(outcome.Message, Loc.T("还原完成"), MessageBoxImage.Information);
            ShowNotice(outcome.Message, !outcome.Success);

            RefreshInstances();
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.F("还原备份失败：{0}", backup.Entry.Path), ex);
            ShowNotice(Loc.F("还原失败：{0}", ex.Message), true);
        }
    }

    private async void OnDeleteBackupClick(object sender, RoutedEventArgs e)
    {
        var backup = _selectedBackup;
        if (backup is null) return;

        var owner = Window.GetWindow(this);

        var detail = backup.Entry.Kind == BackupKind.Original
            ? Loc.T("原版备份被删除后，按包卸载与「恢复原版状态」都不可用，需要重新执行「备份原版游戏」。")
            : Loc.T("备份目录会被整个删除，备份里的游戏文件无法找回。");

        var choice = ChoiceWindow.Ask(owner, Loc.T("删除备份"),
            Loc.F("确定删除备份「{0}」吗？", backup.Title),
            detail,
            new ChoiceOption(Loc.T("删除备份"), "delete", ButtonTone.Danger),
            new ChoiceOption(Loc.T("取消"), "cancel"));

        if (choice != "delete") return;

        // 目录删除本身无法中断，因此这个操作不给「取消」按钮，避免给出无效的取消承诺
        var progress = ProgressWindow.Open(owner, Loc.T("删除备份"), Loc.F("正在删除「{0}」…", backup.Title), canCancel: false);

        try
        {
            var directory = backup.Entry.Path;

            await Task.Run(() =>
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            });

            progress.Finish();
            ShowNotice(Loc.F("备份「{0}」已删除", backup.Title), false);
        }
        catch (Exception ex)
        {
            progress.Finish();
            Log.Error(Loc.F("删除备份失败：{0}", backup.Entry.Path), ex);
            Notify(Loc.F("删除备份失败：{0}", ex.Message), Loc.T("删除备份"), MessageBoxImage.Warning);
        }

        _selectedBackup = null;
        await RefreshBackupsAsync();
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
