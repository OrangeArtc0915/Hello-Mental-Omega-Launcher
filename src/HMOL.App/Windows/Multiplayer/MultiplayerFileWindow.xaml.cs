using System.IO;
using System.Windows;
using System.Windows.Controls;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>文件传输窗里的一台接收方。</summary>
public sealed record FilePeerRow(string Name, string Ip);

/// <summary>
/// 文件传输窗（对应旧版 <c>FileTransferDialog</c>）：选接收方 → 选文件 → 发送，带进度与取消。
/// 接收侧由 <see cref="MultiplayerHub.Transfer"/> 常驻监听。
/// </summary>
public partial class MultiplayerFileWindow : Window
{
    private readonly MultiplayerSession _session;
    private readonly string _selfName;
    private readonly CancellationTokenSource _cts = new();

    private string _path = string.Empty;
    private bool _sending;

    public MultiplayerFileWindow(MultiplayerSession session, string selfName)
    {
        InitializeComponent();

        _session = session;
        _selfName = selfName;

        _session.PeersChanged += OnPeersChanged;
        Closed += OnClosed;

        RefreshPeers();
    }

    private void OnPeersChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshPeers);
            return;
        }

        RefreshPeers();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _session.PeersChanged -= OnPeersChanged;

        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        _cts.Dispose();
    }

    private void RefreshPeers()
    {
        var selected = (ListPeers.SelectedItem as FilePeerRow)?.Ip;

        var rows = _session.Peers
            .Select(peer => new FilePeerRow(string.IsNullOrWhiteSpace(peer.Name) ? peer.Ip : peer.Name, peer.Ip))
            .ToList();

        ListPeers.ItemsSource = rows;

        if (selected is not null)
        {
            var match = rows.FirstOrDefault(row => row.Ip == selected);
            if (match is not null) ListPeers.SelectedItem = match;
        }

        if (rows.Count == 0) AppendLog(Loc.T("暂无在线对端（请先连接组网并确认队友已加入）"));
    }

    private void AppendLog(string text)
    {
        var block = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Text = $"[{DateTime.Now:HH:mm:ss}] {text}"
        };

        block.SetResourceReference(TextBlock.ForegroundProperty,
            text.StartsWith("失败", StringComparison.Ordinal) || text.Contains("拒绝", StringComparison.Ordinal)
                ? "Status.Danger"
                : "Text.Secondary");

        PanLog.Children.Add(block);
        while (PanLog.Children.Count > 200) PanLog.Children.RemoveAt(0);

        LabStatus.Text = text;
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要发送的文件"),
            Filter = Loc.T("所有文件 (*.*)|*.*")
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length <= 0)
            {
                LabFile.Text = Loc.T("空文件无法发送");
                _path = string.Empty;
                return;
            }

            if (info.Length > FileTransfer.MaxFileSize)
            {
                LabFile.Text = Loc.F("{0} 超过 500MB 上限，请压缩后再试", info.Name);
                _path = string.Empty;
                return;
            }

            _path = dialog.FileName;
            LabFile.Text = $"{info.Name}（{FileTransfer.FormatSize(info.Length)}）";
        }
        catch (Exception ex)
        {
            _path = string.Empty;
            LabFile.Text = Loc.F("文件不可访问：{0}", ex.Message);
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_sending) return;

        if (ListPeers.SelectedItem is not FilePeerRow peer)
        {
            ChoiceWindow.Info(this, Loc.T("文件传输"), Loc.T("请先在列表里选择接收方。"));
            return;
        }

        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
        {
            ChoiceWindow.Info(this, Loc.T("文件传输"), Loc.T("请先选择要发送的文件。"));
            return;
        }

        var size = new FileInfo(_path).Length;

        var answer = ChoiceWindow.Confirm(this, Loc.T("确认发送"),
            Loc.F("发送前请先压缩文件，可大幅提升传输速度。\n\n发送给：{0}（{1}）\n", peer.Name, peer.Ip) +
            Loc.F("文件：{0}（{1}）\n\n确定发送？", Path.GetFileName(_path), FileTransfer.FormatSize(size)),
            confirmText: Loc.T("发送"));

        if (!answer) return;

        _sending = true;
        BtnSend.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        Bar.Value = 0;
        AppendLog(Loc.F("开始发送 → {0}：{1}", peer.Ip, Path.GetFileName(_path)));

        var progress = new Progress<FileSendProgress>(value => Bar.Value = value.Fraction);

        try
        {
            var result = await MultiplayerHub.Transfer.SendAsync(peer.Ip, _path, _selfName, progress, _cts.Token);
            AppendLog(result.Ok ? Loc.F("成功：{0}", result.Message) : Loc.F("失败：{0}", result.Message));
        }
        catch (OperationCanceledException)
        {
            AppendLog(Loc.T("已取消发送"));
        }
        catch (Exception ex)
        {
            AppendLog(Loc.F("失败：{0}", ex.Message));
        }
        finally
        {
            _sending = false;
            BtnSend.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            Bar.Value = 0;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        LabStatus.Text = Loc.T("正在取消…");
    }
}
