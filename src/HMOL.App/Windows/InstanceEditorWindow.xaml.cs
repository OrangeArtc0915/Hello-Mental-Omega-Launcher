using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Controls;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.Localization;

namespace HMOL.App.Windows;

/// <summary>
/// 新建 / 编辑实例对话框。目录与重名校验一律走 Core
/// （<see cref="GameLocator.IsGameDirectory"/> 与 <see cref="InstanceManager.All"/>），
/// 校验不过时在窗口里直接说明原因，不让用户提交后才失败。
/// 可以给同一个目录里的多个主程序各起一个版本名，启动游戏时按名字选版本。
/// </summary>
public partial class InstanceEditorWindow : Window
{
    /// <summary>编辑已有实例时它的 Id；新建为 null。</summary>
    private readonly string? _editingId;

    /// <summary>当前选中的游戏类型。</summary>
    private GameKind _kind = GameKind.MentalOmega;

    /// <summary>界面上的版本行（名字输入框 + 可执行文件），启动游戏时按名字选版本。</summary>
    private readonly List<VersionRow> _versionRows = [];

    /// <summary>true = 多版本模式（显示版本列表）；false = 单版本模式（只显示主程序输入框）。</summary>
    private bool _versionMode;

    /// <summary>一行版本：一个可执行文件 + 用户给它起的名字（存在 NameBox 里）。</summary>
    private sealed record VersionRow(TextBox NameBox, string Executable);

    public InstanceEditorWindow(GameInstance? instance = null)
    {
        InitializeComponent();

        _editingId = instance?.Id;
        var isEdit = instance is not null;

        Title = isEdit ? Loc.T("编辑实例") : Loc.T("新建实例");
        Card.Title = Title;
        BtnSave.Content = isEdit ? Loc.T("保存") : Loc.T("创建");

        if (instance is not null)
        {
            TxtName.Text = instance.Name;
            TxtGameDir.Text = instance.GameDir;
            TxtNote.Text = instance.Note;
            _kind = instance.Kind;

            if (instance.Versions.Count > 1)
            {
                // 已有的多版本实例：直接进多版本模式，按 Versions 逐行填充
                _versionMode = true;

                foreach (var item in instance.Versions)
                    AddVersionRow(item.Executable, item.Name);
            }
            else
            {
                // 新建 / 只有 0~1 个版本：单版本模式，主程序输入框填已配置的 exe
                TxtExe.Text = instance.Versions.Count == 1
                    ? instance.Versions[0].Executable
                    : instance.Executable;
            }
        }

        RebuildVersionRows();
        ApplyVersionMode();
        UpdateKindButtons();
        RefreshSharedDir();
        Validate();
    }

    /// <summary>用户填写的实例名称。</summary>
    public string InstanceName { get; private set; } = string.Empty;

    /// <summary>用户选定的游戏目录（已规整为完整路径）。</summary>
    public string GameDirectory { get; private set; } = string.Empty;

    /// <summary>用户填写的备注。</summary>
    public string InstanceNote { get; private set; } = string.Empty;

    /// <summary>用户选定的游戏类型。</summary>
    public GameKind Kind => _kind;

    /// <summary>用户指定的主程序（可空，空表示按类型自动探测）；配了版本时取第一个版本。</summary>
    public string Executable { get; private set; } = string.Empty;

    /// <summary>用户配置的版本列表（空表示按类型自动探测，否则启动游戏时按名字选版本）。</summary>
    public IReadOnlyList<InstanceVersion> Versions { get; private set; } = [];

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnGameDirChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnExecutableChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<GameKind>(tag, out var kind)) return;

        _kind = kind;
        UpdateKindButtons();
        Validate();
    }

    /// <summary>选中的类型用实心按钮，其余用描边，代替下拉框。</summary>
    private void UpdateKindButtons()
    {
        if (PanKinds is null) return;

        foreach (var child in PanKinds.Children)
        {
            if (child is not OutlineButton button || button.Tag is not string tag) continue;
            if (!Enum.TryParse<GameKind>(tag, out var kind)) continue;

            button.Tone = kind == _kind ? ButtonTone.Solid : ButtonTone.Outline;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("选择游戏目录"),
            Multiselect = false
        };

        var current = TxtGameDir.Text?.Trim();

        // 已有路径时把浏览窗定位过去；路径不存在就忽略，交给默认位置
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true) TxtGameDir.Text = dialog.FolderName;
    }

    /// <summary>单版本模式：选一个主程序（不填也能按类型自动探测）。</summary>
    private void OnBrowseExeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择游戏主程序"),
            Filter = Loc.T("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        var current = TxtGameDir.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true) TxtExe.Text = dialog.FileName;
    }

    /// <summary>按 <see cref="_versionMode"/> 统一刷新单版本 / 多版本两块的显隐。</summary>
    private void ApplyVersionMode()
    {
        if (PanSingleExe is null || PanVersionSection is null) return;

        PanSingleExe.Visibility = _versionMode ? Visibility.Collapsed : Visibility.Visible;
        PanVersionSection.Visibility = _versionMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 切到多版本模式：列表为空时先拿当前主程序生成第一行（空则不加），
    /// 随后直接弹出多选对话框，让用户一次选中 2 个 exe。
    /// </summary>
    private void OnEnableVersionsClick(object sender, RoutedEventArgs e)
    {
        _versionMode = true;

        if (_versionRows.Count == 0)
        {
            var current = TxtExe.Text?.Trim();
            if (!string.IsNullOrEmpty(current))
                AddVersionRow(current, Path.GetFileNameWithoutExtension(current));
        }

        RebuildVersionRows();
        ApplyVersionMode();
        Validate();

        OnAddVersionsClick(sender, e);
    }

    /// <summary>切回单版本模式，把第一个版本的 exe 填进主程序输入框（版本列表保留，便于再切回来）。</summary>
    private void OnBackToSingleClick(object sender, RoutedEventArgs e)
    {
        _versionMode = false;

        if (_versionRows.Count > 0) TxtExe.Text = _versionRows[0].Executable;

        ApplyVersionMode();
        Validate();
    }

    /// <summary>一次选中多个 exe 各加一行版本（同一个文件夹里有两个版本时就选 2 个）。</summary>
    private void OnAddVersionsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择游戏主程序"),
            Filter = Loc.T("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = true
        };

        var current = TxtGameDir.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
            AddVersionRow(file, Path.GetFileNameWithoutExtension(file));

        RebuildVersionRows();
        Validate();
    }

    /// <summary>把游戏目录里还没加入列表的 exe 一次性加进来（每条名单 = 去扩展名的文件名）。</summary>
    private void OnAddDetectedClick(object sender, RoutedEventArgs e)
    {
        var directory = GameLocator.NormalizePath(TxtGameDir.Text);
        var added = false;

        foreach (var file in ExecutablesIn(directory))
        {
            var fileName = Path.GetFileName(file);
            if (_versionRows.Any(row => string.Equals(Path.GetFileName(row.Executable), fileName, StringComparison.OrdinalIgnoreCase)))
                continue;

            AddVersionRow(file, Path.GetFileNameWithoutExtension(file));
            added = true;
        }

        if (!added) return;

        RebuildVersionRows();
        Validate();
    }

    /// <summary>加一行版本数据（控件由 <see cref="RebuildVersionRows"/> 统一生成）。</summary>
    private void AddVersionRow(string executable, string? name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(executable) : name!.Trim();
        _versionRows.Add(new VersionRow(new TextBox { Text = label }, executable));
    }

    /// <summary>
    /// 按 <see cref="_versionRows"/> 整表重建版本行：每行 = 名称输入框 / exe 文件名 / 移除按钮。
    /// 重建会换掉 TextBox，所以先把当前输入的文字取出来，避免用户改过的名字被重置。
    /// </summary>
    private void RebuildVersionRows()
    {
        if (PanVersions is null) return;

        var names = _versionRows.Select(row => row.NameBox.Text).ToList();

        PanVersions.Children.Clear();

        for (var i = 0; i < _versionRows.Count; i++)
        {
            var executable = _versionRows[i].Executable;

            var grid = new Grid { Margin = new Thickness(0, 0, 0, i < _versionRows.Count - 1 ? 6 : 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var (frame, nameBox) = MakeVersionNameBox(names[i]);
            Grid.SetColumn(frame, 0);

            var exeLabel = new TextBlock
            {
                Text = Path.GetFileName(executable),
                FontSize = 12,
                Margin = new Thickness(10, 0, 10, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = executable
            };
            exeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text.Tertiary");
            Grid.SetColumn(exeLabel, 1);

            var row = new VersionRow(nameBox, executable);

            var remove = new RoundIconButton
            {
                Icon = "lucide/x",
                IconSize = 13,
                Tone = ButtonTone.Plain,
                Tag = row,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            remove.Click += OnRemoveVersionClick;
            Grid.SetColumn(remove, 2);

            grid.Children.Add(frame);
            grid.Children.Add(exeLabel);
            grid.Children.Add(remove);

            _versionRows[i] = row;
            PanVersions.Children.Add(grid);
        }
    }

    /// <summary>造一个与「实例名称」同款的「边框 + 无框输入框」，用作版本名称。</summary>
    private (Border Frame, TextBox Box) MakeVersionNameBox(string? text)
    {
        var box = new TextBox
        {
            Text = text ?? string.Empty,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = Loc.T("版本名称")
        };

        box.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "Text.Primary");
        box.TextChanged += (_, _) => Validate();

        var frame = new Border
        {
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = TryFindResource("Radius.Button") is CornerRadius radius ? radius : new CornerRadius(8),
            Child = box
        };

        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

        return (frame, box);
    }

    private void OnRemoveVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: VersionRow row }) return;

        _versionRows.RemoveAll(item => ReferenceEquals(item, row));
        RebuildVersionRows();

        // 多版本模式下把版本行删光了，自动切回单版本模式
        if (_versionMode && _versionRows.Count == 0)
        {
            _versionMode = false;
            ApplyVersionMode();
        }

        Validate();
    }

    /// <summary>目录里的 exe 列表；目录读不了（权限等）就当没有。</summary>
    private static string[] ExecutablesIn(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.exe") : [];
        }
        catch
        {
            return [];
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        InstanceName = TxtName.Text.Trim();
        GameDirectory = GameLocator.NormalizePath(TxtGameDir.Text);
        InstanceNote = TxtNote.Text?.Trim() ?? string.Empty;

        if (_versionMode)
        {
            Versions = [.. _versionRows.Select(row => new InstanceVersion
            {
                Name = row.NameBox.Text?.Trim() ?? string.Empty,
                Executable = row.Executable
            })];

            Executable = Versions.Count > 0 ? Versions[0].Executable : TxtExe.Text?.Trim() ?? string.Empty;
        }
        else
        {
            Versions = [];
            Executable = TxtExe.Text?.Trim() ?? string.Empty;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>「同文件夹多实例」开关的文案与状态。</summary>
    private void RefreshSharedDir()
    {
        if (BtnSharedDir is null) return;

        var enabled = SettingsStore.Current.AllowSharedGameDir;

        BtnSharedDir.Content = enabled ? Loc.T("已开启") : Loc.T("已关闭");
        BtnSharedDir.Tone = enabled ? ButtonTone.Solid : ButtonTone.Outline;
    }

    /// <summary>
    /// 就地切换「同文件夹多实例」并重新校验：用户多半是被「这个目录已经被占用」的提示引到这里来的，
    /// 让他不用先跑去设置页。
    /// </summary>
    private void OnToggleSharedDirClick(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.AllowSharedGameDir = !SettingsStore.Current.AllowSharedGameDir;
        SettingsStore.Save();

        RefreshSharedDir();
        Validate();
    }

    /// <summary>校验名称、目录与版本行，结果写进小字并决定「保存」是否可用。</summary>
    private bool Validate()
    {
        if (BtnSave is null || LabDirState is null) return false;

        var name = TxtName.Text?.Trim() ?? string.Empty;
        var directory = TxtGameDir.Text?.Trim() ?? string.Empty;
        var normalized = GameLocator.NormalizePath(directory);

        // 单版本模式看主程序输入框；多版本模式看第一行版本
        var singleExecutable = TxtExe.Text?.Trim();
        if (string.IsNullOrEmpty(singleExecutable)) singleExecutable = null;
        var firstVersionExecutable = _versionRows.Count > 0 ? _versionRows[0].Executable : null;
        var exeForDirectory = _versionMode ? firstVersionExecutable : singleExecutable;

        if (name.Length == 0)
        {
            Fail(Loc.T("请填写实例名称。"));
            return false;
        }

        var duplicatedName = InstanceManager.All.Any(item =>
            !string.Equals(item.Id, _editingId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        if (duplicatedName)
        {
            Fail(Loc.F("实例名称「{0}」已存在，请换一个。", name));
            return false;
        }

        if (directory.Length == 0)
        {
            Fail(Loc.T("请选择游戏目录。"));
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            Fail(Loc.F("目录不存在：{0}", normalized));
            return false;
        }

        // 多版本模式：每一行的主程序都要能解析到
        if (_versionMode)
        {
            foreach (var row in _versionRows)
            {
                if (GameLocator.ResolveExecutable(normalized, row.Executable) is null)
                {
                    Fail(Loc.F("指定的主程序不存在：{0}", Path.GetFileName(row.Executable)));
                    return false;
                }
            }
        }

        if (!GameLocator.IsGameDirectory(normalized, _kind, exeForDirectory))
        {
            Fail(_kind == GameKind.Other && string.IsNullOrEmpty(exeForDirectory)
                ? Loc.T("「其它红警 Mod」需要先指定一个可执行文件。")
                : Loc.T("该目录不是有效的游戏目录（需含该类型的特征主程序，或手动指定主程序）。"));
            return false;
        }

        var duplicatedPath = InstanceManager.All.Any(item =>
            !string.Equals(item.Id, _editingId, StringComparison.OrdinalIgnoreCase) &&
            GameLocator.IsSamePath(item.GameDir, normalized));

        if (duplicatedPath && !SettingsStore.Current.AllowSharedGameDir)
        {
            Fail(Loc.T("该游戏目录已被其他实例使用。") +
                 Loc.T("要让两个实例共用这个目录（例如两套包组合），把下面的「同文件夹多实例」打开即可；只是想加同一个文件夹里的另一个版本，请用上面的「添加版本」。"));
            return false;
        }

        var resolved = exeForDirectory is not null
            ? GameLocator.ResolveExecutable(normalized, exeForDirectory)
            : GameLocator.FindExecutable(normalized, _kind);

        string state;
        string tone;

        if (_versionMode && _versionRows.Count >= 2)
        {
            state = Loc.F("已找到 {0} 个版本：{1}", _versionRows.Count,
                string.Join("、", _versionRows.Select(RowDisplayName)));
            tone = "Status.Success";
        }
        else if (resolved is null)
        {
            state = Loc.T("目录有效，但未找到游戏主程序，启动前请确认游戏文件完整。");
            tone = "Status.Warn";
        }
        else
        {
            state = Loc.F("已找到主程序：{0}", Path.GetFileName(resolved));
            tone = "Status.Success";
        }

        // 目录已被别的实例占用（开关开着才会走到这里）：讲清共用关系，免得以为互不影响
        if (duplicatedPath)
        {
            state += "\n" + Loc.T("该目录已有其它实例：两者共用同一游戏目录与备份，但各自的「已安装包」记录相互独立。");
            tone = "Status.Warn";
        }

        LabDirState.Text = state;
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty, tone);

        // 「加入目录里的主程序」只在多版本模式下判断显隐
        BtnAddDetected.Visibility = _versionMode && ShouldShowDetected(normalized) ? Visibility.Visible : Visibility.Collapsed;

        BtnSave.IsEnabled = true;
        return true;
    }

    /// <summary>版本行的小字名字：用户起的名字，没起就用可执行文件名。</summary>
    private static string RowDisplayName(VersionRow row)
    {
        var name = row.NameBox.Text?.Trim();
        return string.IsNullOrEmpty(name) ? Path.GetFileName(row.Executable) : name;
    }

    /// <summary>目录里有 2 个以上 exe，且其中有还没加入列表的，「加入目录里的主程序」按钮才值得显示。</summary>
    private bool ShouldShowDetected(string directory)
    {
        var files = ExecutablesIn(directory);
        if (files.Length < 2) return false;

        return files.Any(file => !_versionRows.Any(row =>
            string.Equals(Path.GetFileName(row.Executable), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)));
    }

    private void Fail(string message)
    {
        LabDirState.Text = message;
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
        BtnSave.IsEnabled = false;
    }
}
