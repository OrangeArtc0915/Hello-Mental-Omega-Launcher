using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMOL.App.Animation;
using HMOL.App.Controls;
using HMOL.App.Windows;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.App.Pages;

/// <summary>
/// 运行日志页。三块数据来源：
/// 「应用日志」「游戏日志」读内存环形缓冲 <see cref="ActivityLog"/>，页面订阅它以实时刷新，
/// 因此切走页面再回来历史仍在；「磁盘日志」读 <see cref="Paths.Log"/> 里落盘的文件。
/// </summary>
public partial class PageLog : LauncherPage
{
    /// <summary>单个磁盘日志文件最多渲染的行数，避免打开超大文件时卡住界面。</summary>
    private const int MaxFileLines = 2000;

    /// <summary>清理旧日志时保留最近几天的文件。</summary>
    private const int CleanupKeepDays = 7;

    /// <summary>Logger 落盘的一行：<c>[HH:mm:ss.fff] [级别] [模块] 正文</c>。</summary>
    private static readonly Regex LineRegex = new(
        @"^\[(?<time>[^\]]*)\]\s*\[(?<level>[^\]]*)\]\s*\[(?<module>[^\]]*)\]\s*(?<message>.*)$",
        RegexOptions.Compiled);

    /// <summary>本页动画：键统一带 <c>log:</c> 前缀，离开页面时一次收干净。</summary>
    private readonly PageAnimator _anim = new("log:");

    private readonly OutlineButton[] _tabs;

    /// <summary>当前视图的每一行，导出时直接用这份数据，不再重新查一遍。</summary>
    private readonly List<ViewRow> _rows = [];

    private int _tab;
    private string? _diskFile;
    private bool _subscribed;

    public PageLog()
    {
        InitializeComponent();

        _tabs = [BtnAppLog, BtnGameLog, BtnDiskLog];

        // 入场计划：页头 → 页签与操作栏 → 日志正文（自上而下，组内 40ms 一档，总时长 < 500ms）
        _anim.Group(0, HeaderLog);
        _anim.Group(40, PanLogTabs, PanLogOps);
        _anim.Group(120, CardLog);

        SwitchTab(0);
    }

    // ————— 生命周期 —————

    public override void OnEnter()
    {
        Subscribe();
        Reload();
        _anim.Play();
    }

    public override void OnLeave()
    {
        Unsubscribe();
        _anim.Stop();
    }

    /// <summary>本页自己管入场动画（见 <see cref="PageAnimator"/>）。</summary>
    public override bool HandlesEnterAnimation => true;

    /// <summary>三个数据来源对应三个子视图，只给自检用。</summary>
    public override int SubViewCount => _tabs.Length;

    public override void SelectSubView(int index) => SwitchTab(index);

    // ————— 数据来源切换 —————

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var index)) SwitchTab(index);
    }

    private void SwitchTab(int index)
    {
        _tab = Math.Clamp(index, 0, _tabs.Length - 1);

        for (var i = 0; i < _tabs.Length; i++)
            _tabs[i].Tone = i == _tab ? ButtonTone.Solid : ButtonTone.Outline;

        Reload();
    }

    private LogSource CurrentSource => _tab == 1 ? LogSource.Game : LogSource.App;

    // ————— 内存日志订阅 —————

    private void Subscribe()
    {
        if (_subscribed) return;

        _subscribed = true;
        ActivityLog.Appended += OnAppended;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;

        _subscribed = false;
        ActivityLog.Appended -= OnAppended;
    }

    /// <summary>日志可能来自后台线程（如进程输出），必须切回 UI 线程再动控件。</summary>
    private void OnAppended(ActivityLine line)
    {
        if (Dispatcher.CheckAccess()) AppendLive(line);
        else Dispatcher.InvokeAsync(() => AppendLive(line));
    }

    private void AppendLive(ActivityLine line)
    {
        // 磁盘日志是静态快照，不跟着内存日志滚动
        if (_tab == 2 || line.Source != CurrentSource) return;

        if (_rows.Count >= ActivityLog.Capacity) _rows.RemoveAt(0);
        _rows.Add(ToRow(line));

        PanLines.Children.Add(BuildLineBlock(_rows[^1]));
        while (PanLines.Children.Count > ActivityLog.Capacity) PanLines.Children.RemoveAt(0);

        RefreshEmptyHint();
        RefreshStatus();

        // ScrollToEnd 内部按无限偏移排队，布局完成后会被夹到末尾，无需额外 UpdateLayout
        ScrollLog.ScrollToEnd();
    }

    // ————— 刷新与渲染 —————

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        _rows.Clear();

        if (_tab == 2)
        {
            LoadDiskRows();
        }
        else
        {
            foreach (var line in ActivityLog.Snapshot())
            {
                if (line.Source == CurrentSource) _rows.Add(ToRow(line));
            }
        }

        RefreshFileList();
        Render();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 只清空当前视图，不动内存缓冲：下次刷新或新日志进来时内容会回来
        _rows.Clear();
        Render();
    }

    private void Render()
    {
        PanLines.Children.Clear();

        foreach (var row in _rows) PanLines.Children.Add(BuildLineBlock(row));

        RefreshEmptyHint();
        RefreshStatus();
        ScrollLog.ScrollToEnd();
    }

    private static TextBlock BuildLineBlock(ViewRow row)
    {
        var block = new TextBlock
        {
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Text = $"{row.Time}  [{row.Level}] {row.Text}"
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, row.BrushKey);

        return block;
    }

    private void RefreshEmptyHint()
    {
        var hasRows = PanLines.Children.Count > 0;

        // 空状态整块（图标块 + 说明 + 引导按钮）一起显隐
        PanLogEmpty.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        ScrollLog.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;

        if (hasRows) return;

        LabEmpty.Text = _tab switch
        {
            2 => Loc.T("日志目录里还没有文件。"),
            1 => Loc.T("还没有游戏日志。"),
            _ => Loc.T("还没有日志。")
        };
    }

    private void RefreshStatus()
    {
        if (LabStatus is null) return;

        var scope = _tab switch
        {
            2 => _diskFile is null ? Loc.T("磁盘日志") : Loc.F("磁盘日志 · {0}", Path.GetFileName(_diskFile)),
            1 => Loc.T("游戏日志"),
            _ => Loc.T("应用日志")
        };

        LabStatus.Text = Loc.F("共 {0} 行 · {1}", _rows.Count, scope);
    }

    // ————— 磁盘日志 —————

    private static List<FileInfo> LogFiles()
    {
        try
        {
            return new DirectoryInfo(Paths.Log)
                .GetFiles("*.log")
                .OrderByDescending(file => file.LastWriteTime)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取日志目录失败：{0}", ex.Message));
            return [];
        }
    }

    /// <summary>列出日志文件，选中项用实心按钮表示；点击按钮切换到对应文件。</summary>
    private void RefreshFileList()
    {
        var isDisk = _tab == 2;
        ScrollFiles.Visibility = isDisk ? Visibility.Visible : Visibility.Collapsed;

        PanFiles.Children.Clear();
        if (!isDisk) return;

        var files = LogFiles();
        if (files.Count == 0) return;

        foreach (var file in files)
        {
            var selected = _diskFile is not null &&
                           string.Equals(file.FullName, _diskFile, StringComparison.OrdinalIgnoreCase);

            var button = new OutlineButton
            {
                Content = file.Name,
                Tag = file.FullName,
                Tone = selected ? ButtonTone.Solid : ButtonTone.Outline,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 5, 10, 5),
                FontSize = 11.5,
                ToolTip = $"{file.FullName}\n{file.LastWriteTime:yyyy-MM-dd HH:mm:ss}"
            };
            button.Click += OnPickLogFileClick;

            PanFiles.Children.Add(button);
        }
    }

    private void OnPickLogFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;

        _diskFile = path;
        Reload();
    }

    private void LoadDiskRows()
    {
        _rows.Clear();

        var files = LogFiles();

        // 之前选中的文件被清理掉、或还没选过时，退回最新的一份
        if (_diskFile is null || files.All(file => !string.Equals(file.FullName, _diskFile, StringComparison.OrdinalIgnoreCase)))
            _diskFile = files.Count > 0 ? files[0].FullName : null;

        if (_diskFile is null) return;

        try
        {
            var lines = File.ReadAllLines(_diskFile);
            var date = DateOfFile(_diskFile);

            foreach (var line in lines.Skip(Math.Max(0, lines.Length - MaxFileLines)))
                _rows.Add(ParseDiskLine(line, date));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("读取日志文件失败 {0}：{1}", _diskFile, ex.Message));
        }
    }

    private static ViewRow ParseDiskLine(string raw, string date)
    {
        var match = LineRegex.Match(raw);

        if (!match.Success)
            return new ViewRow(date, Loc.T("磁盘"), "INFO", raw, "Text.Secondary");

        var time = match.Groups["time"].Value;
        if (date.Length > 0) time = $"{date} {time}";

        var level = match.Groups["level"].Value.ToUpperInvariant();
        var text = $"[{match.Groups["module"].Value}] {match.Groups["message"].Value}";

        return new ViewRow(time, Loc.T("磁盘"), level, text, BrushKeyOf(level));
    }

    /// <summary>从 <c>HMOL-2026-9-23-192828382.log</c> 里取出日期部分，用于补全每行的时间。</summary>
    private static string DateOfFile(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        return parts.Length >= 4 ? $"{parts[1]}-{parts[2]}-{parts[3]}" : string.Empty;
    }

    // ————— 导出与清理 —————

    private void OnExportTxtClick(object sender, RoutedEventArgs e) => Export(".txt");

    private void OnExportCsvClick(object sender, RoutedEventArgs e) => Export(".csv");

    private void Export(string extension)
    {
        if (_rows.Count == 0)
        {
            Notify(Loc.T("当前视图里没有可导出的日志。"), Loc.T("导出日志"), MessageBoxImage.Information);
            return;
        }

        var isCsv = extension == ".csv";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("导出日志"),
            FileName = $"HMOL_log_{DateTime.Now:yyyyMMdd_HHmmss}{extension}",
            Filter = isCsv ? Loc.T("CSV 文件 (*.csv)|*.csv") : Loc.T("文本文件 (*.txt)|*.txt")
        };

        var owner = Window.GetWindow(this);
        var confirmed = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (confirmed != true) return;

        try
        {
            // 带 BOM 写出，Excel 打开 CSV 才不会把中文识别成乱码
            File.WriteAllText(dialog.FileName, isCsv ? BuildCsv() : BuildTxt(), new UTF8Encoding(true));

            Log.Info(Loc.F("已导出 {0} 行日志到 {1}", _rows.Count, dialog.FileName));
            Notify(Loc.F("已导出 {0} 行到：\n{1}", _rows.Count, dialog.FileName), Loc.T("导出日志"), MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导出日志失败：{0}", ex.Message), ex);
            Notify(Loc.F("导出失败：{0}", ex.Message), Loc.T("导出日志"), MessageBoxImage.Warning);
        }
    }

    private string BuildTxt()
    {
        var builder = new StringBuilder();
        foreach (var row in _rows)
            builder.AppendLine($"[{row.Time}] [{row.Level}] [{row.Source}] {row.Text}");

        return builder.ToString();
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Loc.T("时间,级别,来源,内容"));

        foreach (var row in _rows)
            builder.AppendLine($"{Csv(row.Time)},{Csv(row.Level)},{Csv(row.Source)},{Csv(row.Text)}");

        return builder.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e) => ShellHelper.OpenFolder(Paths.Log);

    private void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        var question = Loc.F("将删除 {0} 天前的日志文件。\n当前日志目录：{1}\n\n确定继续？", CleanupKeepDays, Paths.Log);

        var owner = Window.GetWindow(this);
        var reply = ChoiceWindow.Confirm(owner, Loc.T("清理旧日志"), question, confirmText: Loc.T("清理"), danger: true);

        if (!reply) return;

        var removed = CleanupOldFiles();
        Notify(Loc.F("已清理 {0} 个旧日志文件。", removed), Loc.T("清理旧日志"), MessageBoxImage.Information);

        if (_tab == 2) Reload();
    }

    /// <summary>删除超过保留期的日志文件。正在写入的那份必须跳过，否则会把当前会话的日志删掉。</summary>
    private static int CleanupOldFiles()
    {
        var removed = 0;
        var threshold = DateTime.Now.AddDays(-CleanupKeepDays);
        var currentFile = Log.Current?.CurrentFile;

        foreach (var file in LogFiles())
        {
            if (file.LastWriteTime >= threshold) continue;
            if (string.Equals(file.FullName, currentFile, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                file.Delete();
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warn(Loc.F("删除旧日志失败 {0}：{1}", file.Name, ex.Message));
            }
        }

        return removed;
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

    private static string BrushKeyOf(string level) => level switch
    {
        "ERROR" or "FATAL" => "Status.Danger",
        "WARN" or "WARNING" => "Status.Warn",
        _ => "Text.Secondary"
    };

    private static ViewRow ToRow(ActivityLine line) => new(
        line.Time.ToString("HH:mm:ss"),
        line.Source == LogSource.Game ? Loc.T("游戏") : Loc.T("应用"),
        line.Level.ToString().ToUpperInvariant(),
        line.Text,
        line.Level switch
        {
            ActivityLevel.Error => "Status.Danger",
            ActivityLevel.Warn => "Status.Warn",
            _ => "Text.Secondary"
        });

    /// <summary>当前视图里的一行。<see cref="BrushKey"/> 是语义颜色键，不写死具体颜色。</summary>
    private sealed record ViewRow(string Time, string Source, string Level, string Text, string BrushKey);
}
