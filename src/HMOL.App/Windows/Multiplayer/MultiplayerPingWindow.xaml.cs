using System.Windows;
using System.Windows.Controls;
using HMOL.Core.Multiplayer;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// PING 测试窗（对应旧版 <c>PingDialog</c>）：后台跑 <see cref="NetworkToolkit.PingAsync"/>，可取消。
/// </summary>
public partial class MultiplayerPingWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    private bool _running;

    public MultiplayerPingWindow(string? defaultTarget = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(defaultTarget)) TxtTarget.Text = defaultTarget;

        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        _cts.Dispose();
    }

    private async void OnGoClick(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var host = TxtTarget.Text.Trim();
        if (host.Length == 0)
        {
            LabStatus.Text = "请输入目标 IP 或主机名。";
            return;
        }

        var count = 4;
        if (int.TryParse(TxtCount.Text.Trim(), out var parsed)) count = Math.Clamp(parsed, 1, 10);

        _running = true;
        BtnGo.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        LabStatus.Text = $"正在 PING {host} × {count} …";

        try
        {
            var result = await NetworkToolkit.PingAsync(host, count, 2.0, _cts.Token);
            var text = $"PING {host}：成功 {result.Ok}/{result.Total}，平均延迟 {result.AvgMs} ms";
            LabStatus.Text = text;
            Append(text);
        }
        catch (OperationCanceledException)
        {
            LabStatus.Text = "已取消";
            Append("已取消");
        }
        catch (Exception ex)
        {
            LabStatus.Text = $"PING 失败：{ex.Message}";
            Append($"失败：{ex.Message}");
        }
        finally
        {
            _running = false;
            BtnGo.IsEnabled = true;
            BtnCancel.IsEnabled = false;
        }
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

        LabStatus.Text = "正在取消…";
    }
}
