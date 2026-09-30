using System.Windows;
using HMOL.App.Controls;
using HMOL.App.Services;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 服务端模式窗（对应旧版 <c>ServerDialog</c>）：本机自建 n2n supernode，
/// 启动后显示公网地址供队友填入节点栏。节点进程由 <see cref="MultiplayerHub"/> 持有。
/// </summary>
public partial class MultiplayerSupernodeWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    private bool _starting;

    public MultiplayerSupernodeWindow()
    {
        InitializeComponent();

        var settings = MultiplayerSettingsStore.Current;

        TxtPort.Text = settings.SupernodePort.ToString();
        TxtPool.Text = string.IsNullOrWhiteSpace(settings.SupernodeIpPool)
            ? NodeCatalog.N2nIpPool
            : settings.SupernodeIpPool;

        RefreshState();

        Closed += (_, _) =>
        {
            try { _cts.Cancel(); }
            catch { /* 已释放 */ }

            _cts.Dispose();
        };
    }

    private void RefreshState()
    {
        var server = MultiplayerHub.Server;

        if (server is { IsRunning: true })
        {
            LabStatus.Text = "运行中";

            _ = ShowAddressAsync();
            BtnToggle.Content = "停止服务";
            BtnToggle.Tone = ButtonTone.Danger;
        }
        else
        {
            LabStatus.Text = "未启动";
            BarAddress.Visibility = Visibility.Collapsed;
            BtnToggle.Content = "启动服务";
            BtnToggle.Tone = ButtonTone.Solid;
        }
    }

    private async Task ShowAddressAsync()
    {
        var server = MultiplayerHub.Server;
        if (server is null) return;

        try
        {
            var address = await server.PublicAddressAsync(_cts.Token);
            LabAddress.Text = $"公网地址：{address}（队友节点栏填这个）";
            BarAddress.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LabAddress.Text = $"取公网地址失败：{ex.Message}";
            BarAddress.Visibility = Visibility.Visible;
        }
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_starting) return;

        var server = MultiplayerHub.Server;

        if (server is { IsRunning: true })
        {
            BtnToggle.IsEnabled = false;
            LabStatus.Text = "正在停止…";

            await MultiplayerHub.StopServerAsync();

            BtnToggle.IsEnabled = true;
            RefreshState();
            return;
        }

        if (!int.TryParse(TxtPort.Text.Trim(), out var port) || port < 1024 || port > 65535)
        {
            LabStatus.Text = "端口必须是 1024 - 65535 之间的数字";
            return;
        }

        var pool = TxtPool.Text.Trim();
        if (string.IsNullOrWhiteSpace(pool)) pool = NodeCatalog.N2nIpPool;

        _starting = true;
        BtnToggle.IsEnabled = false;
        LabStatus.Text = "正在启动…";

        try
        {
            var result = await MultiplayerHub.StartServerAsync(port, pool, _cts.Token);

            if (!result.Ok)
            {
                LabStatus.Text = $"启动失败：{result.Message}";
                return;
            }

            MultiplayerSettingsStore.Update(settings =>
            {
                settings.SupernodePort = port;
                settings.SupernodeIpPool = pool;
            });

            RefreshState();
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            LabStatus.Text = $"启动失败：{ex.Message}";
        }
        finally
        {
            _starting = false;
            BtnToggle.IsEnabled = true;
        }
    }
}
