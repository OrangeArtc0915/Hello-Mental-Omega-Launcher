using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Controls;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Windows;

/// <summary>
/// 首次运行配置向导。五步：欢迎 → 选目录 → 检查环境 → 创建实例 → 接下来做什么。
///
/// <para>
/// 三条红线：不打扰老用户（判定与补标记在 <see cref="MainWindow.QueueFirstRunWizard"/>）、
/// 不让用户走进死路（每步都能跳过 / 后退，目录不给自动扫描，选不到就明确说原因）、
/// 不替用户做重活（只问与记，安装 / 下载 / 改游戏文件一概不做）。
/// </para>
///
/// <para>
/// 最后一步只讲「接下来建议做什么」，尤其把「备份原版游戏」为什么必须做说透——
/// 没有这份备份，按包卸载就无从还原被覆盖的原文件，卸载只剩全量恢复一条路。
/// </para>
///
/// <para>
/// 状态只有下面几个字段，「上一步 / 下一步」的可用性全部由它们推导，不额外存状态。
/// </para>
/// </summary>
public partial class ConfigWizardWindow : Window
{
    /// <summary>当前步骤（1..5）。</summary>
    private int _step = 1;

    /// <summary>创建过程中禁止重复点击。</summary>
    private bool _busy;

    /// <summary>选中的游戏目录（已规整为完整路径）。</summary>
    private string _selectedDirectory = string.Empty;

    /// <summary>用户手工指定的主程序（为空表示按类型自动查找）。</summary>
    private string _executable = string.Empty;

    /// <summary>目录校验时解析出的主程序路径；为 null 表示没找到。</summary>
    private string? _resolvedExecutable;

    /// <summary>当前选中的游戏类型。</summary>
    private GameKind _kind = GameKind.MentalOmega;

    /// <summary>步骤指示点：圆点 + 序号 + 步骤名。</summary>
    private readonly List<(Border Dot, TextBlock Number, TextBlock Label)> _indicators = [];

    /// <summary>各步固定的顺序，不要缓存候选目录之类的东西。</summary>
    private static readonly string[] StepNames = ["欢迎", "选择目录", "检查环境", "创建实例", "接下来"];

    public ConfigWizardWindow()
    {
        InitializeComponent();

        Card.Title = "配置向导";

        BuildStepIndicator();
        UpdateKindButtons();
        SwitchStep(1);
    }

    // ————— 步骤机 —————

    private void SwitchStep(int step)
    {
        if (step is < 1 or > 5) step = 1;
        _step = step;

        StepWelcome.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepDirectory.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepEnvironment.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepCreate.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepNext.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        HideNotice();
        UpdateStepIndicator();

        BtnPrev.Visibility = step == 1 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = step == 5 ? "完成" : "下一步";

        switch (step)
        {
            case 2:
                // 每次进这一步都重新校验一遍，不缓存在上一步选过的结果
                BtnNext.IsEnabled = ValidateDirectory();
                break;
            case 3:
                RefreshEnvironment();
                BtnNext.IsEnabled = true;
                break;
            case 4:
                PrepareSummary();
                BtnNext.IsEnabled = true;
                break;
            default:
                BtnNext.IsEnabled = true;
                break;
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        switch (_step)
        {
            // 第 4 步的「下一步」不是翻页，而是真的去建实例
            case 4:
                CreateInstance();
                return;

            // 第 5 步就是「完成」
            default:
                if (_step < 4)
                {
                    SwitchStep(_step + 1);
                    return;
                }

                MarkFirstRunCompleted();
                DialogResult = true;
                return;
        }
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SwitchStep(_step - 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var choice = ChoiceWindow.Ask(this, "跳过配置向导",
            "确定要跳过吗？",
            "跳过后不会自动创建游戏实例。之后再想配置，可以到「设置 → 游戏路径 → 重新运行配置向导」，" +
            "也可以直接在「游戏实例」页新建。",
            new ChoiceOption("跳过", "skip", ButtonTone.Solid),
            new ChoiceOption("继续配置", "back"));

        if (choice != "skip") return;

        MarkFirstRunCompleted();
        DialogResult = false;
    }

    // ————— 第 2 步：选目录 —————

    private void OnDirectoryChanged(object sender, TextChangedEventArgs e) => OnDirectoryInputChanged();

    private void OnKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<GameKind>(tag, out var kind)) return;

        _kind = kind;
        UpdateKindButtons();
        OnDirectoryInputChanged();
    }

    private void OnDirectoryInputChanged()
    {
        // 「其它红警 Mod」没有默认可执行文件名，必须让用户指定
        PanExecutable.Visibility = _kind == GameKind.Other ? Visibility.Visible : Visibility.Collapsed;

        // 只在第 2 步实时校验，并且校验结果直接决定「下一步」能不能点
        if (_step != 2) return;

        BtnNext.IsEnabled = ValidateDirectory();
    }

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

    private void OnBrowseDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择游戏目录",
            Multiselect = false
        };

        var current = TxtGameDir.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true) TxtGameDir.Text = dialog.FolderName;
    }

    private void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏主程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var current = TxtExecutable.Text?.Trim();
        var start = !string.IsNullOrEmpty(current) ? Path.GetDirectoryName(current) : null;
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
        {
            var dir = TxtGameDir.Text?.Trim();
            start = !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
        }

        if (!string.IsNullOrEmpty(start)) dialog.InitialDirectory = start;

        if (dialog.ShowDialog(this) == true) TxtExecutable.Text = dialog.FileName;
    }

    /// <summary>
    /// 校验目录。失败时必须**写明原因**，只给一个红叉等于没说。
    /// 探测本身不做，目录一律由用户指定（自动扫描磁盘的功能已移除）。
    /// </summary>
    private bool ValidateDirectory()
    {
        var raw = TxtGameDir.Text?.Trim() ?? string.Empty;
        var normalized = GameLocator.NormalizePath(raw);
        var executable = TxtExecutable.Text?.Trim() ?? string.Empty;

        _selectedDirectory = normalized;
        _executable = executable;
        _resolvedExecutable = null;

        if (normalized.Length == 0)
        {
            SetDirectoryState("请选择游戏目录。", "Text.Tertiary");
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            SetDirectoryState($"目录不存在：{normalized}", "Status.Danger");
            return false;
        }

        if (InstanceManager.All.Any(item => GameLocator.IsSamePath(item.GameDir, normalized)))
        {
            SetDirectoryState("这个目录已经被其它实例使用了，请在「游戏实例」页查看，或换一个目录。", "Status.Danger");
            return false;
        }

        if (!GameLocator.IsGameDirectory(normalized, _kind, executable))
        {
            SetDirectoryState(
                _kind == GameKind.Other && executable.Length == 0
                    ? "「其它红警 Mod」需要先指定一个可执行文件，否则无法确认这是个游戏目录。"
                    : "这个目录下没有找到该类型的特征主程序。请确认选的是游戏安装根目录"
                      + "（里面应有 Mental_Omega 子目录或游戏主程序）。",
                "Status.Danger");
            return false;
        }

        _resolvedExecutable = GameLocator.ResolveExecutable(normalized, executable)
                              ?? GameLocator.FindExecutable(normalized, _kind);

        SetDirectoryState(
            _resolvedExecutable is null
                ? "目录有效，但没有找到游戏主程序；可以继续，启动前请确认游戏文件完整。"
                : $"已找到主程序：{Path.GetFileName(_resolvedExecutable)}",
            _resolvedExecutable is null ? "Status.Warn" : "Status.Success");

        return true;
    }

    private void SetDirectoryState(string text, string brushKey)
    {
        LabDirectoryState.Text = text;
        LabDirectoryState.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    // ————— 第 3 步：检查环境 —————

    /// <summary>
    /// 只做提醒，不作阻止条件：缺依赖也让用户继续，并明确说清楚之后去哪里补。
    /// </summary>
    private void RefreshEnvironment()
    {
        if (_resolvedExecutable is not null)
        {
            LabExecutableState.Text = $"已找到：{_resolvedExecutable}";
            LabExecutableState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
        }
        else
        {
            LabExecutableState.Text = "没有找到该类型的主程序。可以先继续，之后在实例编辑器里手动指定主程序，"
                                      + "或确认游戏文件是否完整。";
            LabExecutableState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warn");
        }

        var missing = RuntimeLocator.IsRuntimePresent
            ? RuntimeLocator.Verify(
                (RuntimeLocator.EasyTierCoreExe, "easytier-core.exe"),
                (RuntimeLocator.EasyTierCliExe, "easytier-cli.exe"))
            : RuntimeLocator.MissingRuntimeMessage;

        if (missing is null)
        {
            LabRuntimeState.Text = "组网组件已就绪，可以联机。";
            LabRuntimeState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Success");
        }
        else
        {
            LabRuntimeState.Text = $"{missing}。联机暂时用不了，但不影响单机；"
                                   + "重新解压完整发行包即可补齐 runtime 目录。";
            LabRuntimeState.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warn");
        }
    }

    // ————— 第 4 步：创建 —————

    private void OnNameChanged(object sender, TextChangedEventArgs e) => HideNotice();

    /// <summary>预填一个合理的默认名字，并把「将要创建什么」摆出来让用户核一遍。</summary>
    private void PrepareSummary()
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            var baseName = Path.GetFileName(
                _selectedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            TxtName.Text = string.IsNullOrWhiteSpace(baseName) ? "心灵终结" : baseName;
        }

        var executable = _resolvedExecutable is null
            ? "（未找到，之后可在实例编辑器里指定）"
            : Path.GetFileName(_resolvedExecutable);

        LabSummary.Text =
            $"类型：{GameKinds.DisplayName(_kind)}\n" +
            $"目录：{_selectedDirectory}\n" +
            $"主程序：{executable}";
    }

    private void CreateInstance()
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ShowNotice("请填写实例名称。", "Status.Danger");
            return;
        }

        _busy = true;
        BtnNext.IsEnabled = false;

        try
        {
            var result = InstanceManager.Add(name, _selectedDirectory, TxtNote.Text ?? string.Empty, _kind, _executable);

            if (!result.Success)
            {
                // 失败不关窗，让用户改一改再试
                ShowNotice(result.Message, "Status.Danger");
                return;
            }

            MarkFirstRunCompleted();

            // 建完不直接关窗：再走一步把「备份原版游戏」和「联机怎么用」讲清楚。
            // 这两件事新人最容易漏，而漏掉原版备份会让卸载只剩「全量恢复」一条路。
            ChoiceWindow.Info(this, "实例已创建", $"{result.Message}。接下来还有两件建议尽早做的事。");
            SwitchStep(5);
        }
        catch (Exception ex)
        {
            Log.Error("配置向导创建实例失败", ex);
            ShowNotice($"创建实例时出错：{ex.Message}", "Status.Danger");
        }
        finally
        {
            _busy = false;
            if (IsLoaded) BtnNext.IsEnabled = true;
        }
    }

    /// <summary>
    /// 只落这一个布尔标记，不存「走到第几步」之类的中间态：
    /// 用户中途强杀进程，下次启动仍算首次运行，从第 1 步重来，比从半截状态开始清楚。
    /// </summary>
    private static void MarkFirstRunCompleted()
    {
        if (SettingsStore.Current.FirstRunCompleted) return;

        SettingsStore.Current.FirstRunCompleted = true;
        SettingsStore.Save();
    }

    // ————— 提示条与步骤指示点 —————

    private void ShowNotice(string text, string brushKey)
    {
        LabNotice.Text = text;
        LabNotice.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        LabNotice.Visibility = Visibility.Visible;
    }

    private void HideNotice()
    {
        LabNotice.Text = string.Empty;
        LabNotice.Visibility = Visibility.Collapsed;
    }

    /// <summary>顶部一排「圆点 + 步骤名」，中间用短横线连起来。</summary>
    private void BuildStepIndicator()
    {
        for (var i = 0; i < StepNames.Length; i++)
        {
            if (i > 0)
            {
                var line = new Border
                {
                    Width = 26,
                    Height = 1,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                line.SetResourceReference(Border.BackgroundProperty, "Border.Default");
                PanSteps.Children.Add(line);
            }

            var number = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Child = number
            };

            var label = new TextBlock
            {
                Text = StepNames[i],
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var group = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            group.Children.Add(dot);
            group.Children.Add(label);
            PanSteps.Children.Add(group);

            _indicators.Add((dot, number, label));
        }
    }

    /// <summary>选中态一律换资源引用，主题一换整套跟着变，不写第二套颜色。</summary>
    private void UpdateStepIndicator()
    {
        for (var i = 0; i < _indicators.Count; i++)
        {
            var (dot, number, label) = _indicators[i];
            var active = i + 1 == _step;

            dot.SetResourceReference(Border.BackgroundProperty, active ? "Accent.Base" : "Border.Default");
            number.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text.OnAccent" : "Text.Secondary");
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "Accent.Base" : "Text.Tertiary");
            label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
