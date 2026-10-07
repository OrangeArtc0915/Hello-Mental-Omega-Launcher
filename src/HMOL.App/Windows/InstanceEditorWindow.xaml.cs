using System.IO;
using System.Windows;
using System.Windows.Controls;
using HMOL.App.Controls;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.Localization;

namespace HMOL.App.Windows;

/// <summary>
/// 新建 / 编辑实例对话框。目录与重名校验一律走 Core
/// （<see cref="GameLocator.IsGameDirectory"/> 与 <see cref="InstanceManager.All"/>），
/// 校验不过时在窗口里直接说明原因，不让用户提交后才失败。
/// 指定了主程序后目录不再要求符合心灵终结的识别规则，其它红警 Mod 也能建实例。
/// </summary>
public partial class InstanceEditorWindow : Window
{
    /// <summary>编辑已有实例时它的 Id；新建为 null。</summary>
    private readonly string? _editingId;

    /// <summary>当前选中的游戏类型。</summary>
    private GameKind _kind = GameKind.MentalOmega;

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
            TxtExe.Text = instance.Executable;
        }

        UpdateKindButtons();
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

    /// <summary>用户指定的主程序（可空，空表示按类型自动探测）。</summary>
    public string Executable { get; private set; } = string.Empty;

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

    private void OnBrowseExeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择游戏主程序"),
            Filter = Loc.T("可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false
        };

        // 优先定位到主程序所在目录，其次定位到游戏目录
        var current = TxtExe.Text?.Trim();
        var startDirectory = !string.IsNullOrEmpty(current) ? Path.GetDirectoryName(current) : null;
        if (string.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            var gameDir = TxtGameDir.Text?.Trim();
            startDirectory = !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir) ? gameDir : null;
        }

        if (!string.IsNullOrEmpty(startDirectory)) dialog.InitialDirectory = startDirectory;

        if (dialog.ShowDialog(this) == true) TxtExe.Text = dialog.FileName;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        InstanceName = TxtName.Text.Trim();
        GameDirectory = GameLocator.NormalizePath(TxtGameDir.Text);
        InstanceNote = TxtNote.Text?.Trim() ?? string.Empty;
        Executable = TxtExe.Text?.Trim() ?? string.Empty;

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>校验名称、目录与主程序，结果写进小字并决定「保存」是否可用。</summary>
    private bool Validate()
    {
        if (BtnSave is null || LabDirState is null) return false;

        var name = TxtName.Text?.Trim() ?? string.Empty;
        var directory = TxtGameDir.Text?.Trim() ?? string.Empty;
        var executable = TxtExe.Text?.Trim() ?? string.Empty;
        var normalized = GameLocator.NormalizePath(directory);

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

        if (executable.Length > 0 && GameLocator.ResolveExecutable(normalized, executable) is null)
        {
            Fail(Loc.F("指定的主程序不存在：{0}", executable));
            return false;
        }

        if (!GameLocator.IsGameDirectory(normalized, _kind, executable))
        {
            Fail(_kind == GameKind.Other && executable.Length == 0
                ? Loc.T("「其它红警 Mod」需要先指定一个可执行文件。")
                : Loc.T("该目录不是有效的游戏目录（需含该类型的特征主程序，或手动指定主程序）。"));
            return false;
        }

        var duplicatedPath = InstanceManager.All.Any(item =>
            !string.Equals(item.Id, _editingId, StringComparison.OrdinalIgnoreCase) &&
            GameLocator.IsSamePath(item.GameDir, normalized));

        if (duplicatedPath)
        {
            Fail(Loc.T("该游戏目录已被其他实例使用。"));
            return false;
        }

        var resolved = GameLocator.ResolveExecutable(normalized, executable)
                       ?? GameLocator.FindExecutable(normalized, _kind);

        LabDirState.Text = resolved is null
            ? Loc.T("目录有效，但未找到游戏主程序，启动前请确认游戏文件完整。")
            : Loc.F("已找到主程序：{0}", Path.GetFileName(resolved));
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty,
            resolved is null ? "Status.Warn" : "Status.Success");

        BtnSave.IsEnabled = true;
        return true;
    }

    private void Fail(string message)
    {
        LabDirState.Text = message;
        LabDirState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Danger");
        BtnSave.IsEnabled = false;
    }
}