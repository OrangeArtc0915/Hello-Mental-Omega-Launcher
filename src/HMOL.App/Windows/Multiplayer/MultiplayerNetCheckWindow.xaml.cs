using System.Windows;
using System.Windows.Controls;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 连通性检测聚合窗（对应旧版 <c>NetCheckDialog</c>）：把 PING / TCP·UDP 测速 / NAT 检测收在一处。
/// NAT 检测就地跑（其余三项各自开窗）。
/// </summary>
public partial class MultiplayerNetCheckWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public MultiplayerNetCheckWindow()
    {
        InitializeComponent();

        Closed += (_, _) =>
        {
            try { _cts.Cancel(); }
            catch { /* 已释放 */ }

            _cts.Dispose();
        };
    }

    private void OnEntryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        switch (tag)
        {
            case "ping":
                new MultiplayerPingWindow { Owner = this }.Show();
                break;

            case "tcp":
                new MultiplayerBandwidthWindow("TCP") { Owner = this }.Show();
                break;

            case "udp":
                new MultiplayerBandwidthWindow("UDP") { Owner = this }.Show();
                break;

            case "nat":
                _ = RunNatAsync();
                break;
        }
    }

    private async Task RunNatAsync()
    {
        BtnNat.IsEnabled = false;
        LabNat.Text = "正在请求 STUN 服务器…";

        try
        {
            var type = await NetworkToolkit.NatTypeAsync(new Progress<string>(text => LabNat.Text = text), _cts.Token);
            LabNat.Text = $"NAT 类型：{type}";
        }
        catch (OperationCanceledException)
        {
            LabNat.Text = "检测已取消";
        }
        catch (Exception ex)
        {
            LabNat.Text = $"检测失败：{ex.Message}";
        }
        finally
        {
            BtnNat.IsEnabled = true;
        }
    }
}
