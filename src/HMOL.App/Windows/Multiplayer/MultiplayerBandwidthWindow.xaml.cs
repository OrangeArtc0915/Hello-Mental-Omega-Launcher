using System.Net;
using System.Windows;
using System.Windows.Controls;
using HMOL.App.Controls;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// TCP / UDP 带宽测速窗（对应旧版 <c>SpeedDialog</c>）：本机可当服务端或客户端，带取消。
/// </summary>
public partial class MultiplayerBandwidthWindow : Window
{
    private readonly string _protocol;
    private readonly CancellationTokenSource _cts = new();

    private bool _server = true;
    private int _seconds = 3;
    private bool _running;

    public MultiplayerBandwidthWindow(string protocol, string? defaultHost = null)
    {
        InitializeComponent();

        _protocol = protocol;
        Title = Loc.F("{0} 带宽测速", protocol);
        Card.Title = Title;

        if (!string.IsNullOrWhiteSpace(defaultHost)) TxtHost.Text = defaultHost;

        RefreshSelection();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        _cts.Dispose();
    }

    private void RefreshSelection()
    {
        BtnServer.Tone = _server ? ButtonTone.Solid : ButtonTone.Outline;
        BtnClient.Tone = _server ? ButtonTone.Outline : ButtonTone.Solid;

        BtnSec3.Tone = _seconds == 3 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnSec5.Tone = _seconds == 5 ? ButtonTone.Solid : ButtonTone.Outline;
        BtnSec10.Tone = _seconds == 10 ? ButtonTone.Solid : ButtonTone.Outline;

        TxtHost.IsEnabled = !_server;
    }

    private void OnRoleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        _server = tag == "server";
        RefreshSelection();
    }

    private void OnSecondsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (int.TryParse(tag, out var value)) _seconds = value;

        RefreshSelection();
    }

    private async void OnGoClick(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var host = TxtHost.Text.Trim();

        if (!_server && !IsValidIp(host))
        {
            LabStatus.Text = Loc.T("请输入正确的对方虚拟 IP。");
            return;
        }

        _running = true;
        BtnGo.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        LabStatus.Text = Loc.F("正在{0}（{1} 秒）…", ( _server ? Loc.T("等待对方连接") : Loc.F("连接 {0}", host)), _seconds);

        var progress = new Progress<string>(text => LabStatus.Text = text);

        try
        {
            var result = await RunAsync(host, progress, _cts.Token);
            var text = Loc.F("{0} 测速完成：{1} Mbps（{2} 字节 / {3} 秒）", _protocol, result.Mbps, result.Bytes, result.Seconds);
            LabStatus.Text = result.Bytes > 0 ? text : Loc.T("没有收到数据（请确认两端角色与端口是否对应）");
            Append(text);
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = Loc.T("已取消");
        }
        catch (Exception ex)
        {
            LabStatus.Text = Loc.F("测速失败：{0}", ex.Message);
            Append(Loc.F("失败：{0}", ex.Message));
        }
        finally
        {
            _running = false;
            BtnGo.IsEnabled = true;
            BtnCancel.IsEnabled = false;
        }
    }

    private Task<BandwidthResult> RunAsync(string host, IProgress<string>? progress, CancellationToken token)
    {
        var tcp = _protocol == "TCP";

        if (_server)
        {
            return tcp
                ? NetworkToolkit.TcpBandwidthServerAsync(NetworkToolkit.TcpSpeedPort, _seconds, progress, token)
                : NetworkToolkit.UdpBandwidthServerAsync(NetworkToolkit.UdpSpeedPort, _seconds, progress, token);
        }

        return tcp
            ? NetworkToolkit.TcpBandwidthClientAsync(host, NetworkToolkit.TcpSpeedPort, _seconds, progress, token)
            : NetworkToolkit.UdpBandwidthClientAsync(host, NetworkToolkit.UdpSpeedPort, _seconds, progress, token);
    }

    private static bool IsValidIp(string text)
    {
        if (!IPAddress.TryParse(text, out var address)) return false;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private void Append(string text)
    {
        var block = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Text = $"[{DateTime.Now:HH:mm:ss}] {text}"
        };

        block.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        PanResult.Children.Add(block);
        while (PanResult.Children.Count > 100) PanResult.Children.RemoveAt(0);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        LabStatus.Text = Loc.T("正在取消…");
    }
}
