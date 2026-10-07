using System.Windows;
using System.Windows.Controls;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>节点测速列表里的一行。</summary>
public sealed record NodeSpeedRow(string Node, string Latency);

/// <summary>
/// 节点测速窗（对应旧版 <c>NodeSpeedDialog</c>）：后台逐个测延迟，双击 / 应用选中即切换节点。
/// </summary>
public partial class MultiplayerNodeSpeedWindow : Window
{
    private readonly NetworkEngineKind _kind;
    private readonly Action<string> _onPick;
    private readonly List<string> _nodes;
    private readonly Dictionary<string, string> _latency = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();

    private bool _running;

    public MultiplayerNodeSpeedWindow(NetworkEngineKind kind, string currentNode, Action<string> onPick)
    {
        InitializeComponent();

        _kind = kind;
        _onPick = onPick;

        _nodes = (kind == NetworkEngineKind.N2n ? NodeCatalog.N2nNodeValues() : NodeCatalog.EasyTierNodeValues()).ToList();

        LabHint.Text = Loc.F("当前方案：{0} · ", NetworkEngineFactory.DisplayName(kind)) + LabHint.Text;

        foreach (var node in _nodes) _latency[node] = Loc.T("测速中…");

        Render();

        if (!string.IsNullOrWhiteSpace(currentNode))
        {
            var row = ListNodes.Items.OfType<NodeSpeedRow>().FirstOrDefault(item => item.Node == currentNode);
            if (row is not null) ListNodes.SelectedItem = row;
        }

        Loaded += (_, _) => RunAll();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _cts.Cancel(); }
        catch { /* 已释放 */ }

        _cts.Dispose();
    }

    private void Render()
    {
        var selected = (ListNodes.SelectedItem as NodeSpeedRow)?.Node;

        var rows = _nodes.Select(node => new NodeSpeedRow(node, _latency.TryGetValue(node, out var value) ? value : "-")).ToList();
        ListNodes.ItemsSource = rows;

        if (selected is not null)
        {
            var match = rows.FirstOrDefault(row => row.Node == selected);
            if (match is not null) ListNodes.SelectedItem = match;
        }
    }

    private void OnRetestClick(object sender, RoutedEventArgs e) => RunAll();

    private void RunAll()
    {
        if (_running || _cts.IsCancellationRequested) return;

        _running = true;
        BtnRetest.IsEnabled = false;
        LabStatus.Text = Loc.T("测速中…");

        foreach (var node in _nodes) _latency[node] = Loc.T("测速中…");
        Render();

        _ = MeasureAllAsync();
    }

    private async Task MeasureAllAsync()
    {
        var token = _cts.Token;

        try
        {
            foreach (var node in _nodes)
            {
                if (token.IsCancellationRequested) return;

                var milliseconds = await MeasureAsync(node, token);
                _latency[node] = milliseconds is null ? Loc.T("不可达") : $"{milliseconds} ms";

                await Dispatcher.InvokeAsync(Render);
            }

            LabStatus.Text = Loc.F("测速完成：{0} 个节点", _nodes.Count);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭，忽略
        }
        catch (Exception ex)
        {
            LabStatus.Text = Loc.F("测速失败：{0}", ex.Message);
        }
        finally
        {
            _running = false;

            if (!token.IsCancellationRequested) await Dispatcher.InvokeAsync(() => BtnRetest.IsEnabled = true);
        }
    }

    private async Task<int?> MeasureAsync(string node, CancellationToken token)
    {
        var (host, port) = NodeCatalog.NodeHostPort(node);
        if (string.IsNullOrWhiteSpace(host)) return null;

        // EasyTier 节点优先用 TCP 建连耗时（更接近真实游戏连接体验）
        if (_kind == NetworkEngineKind.EasyTier && port is not null)
        {
            var latency = await NetworkToolkit.TcpLatencyAsync(host, port, 3.0, token);
            if (latency is not null) return latency;
        }

        var ping = await NetworkToolkit.PingAsync(host, 3, 2.0, token);
        return ping.Ok > 0 ? (int)ping.AvgMs : null;
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Apply();

    private void OnApplyClick(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (ListNodes.SelectedItem is not NodeSpeedRow row) return;

        _onPick(row.Node);
        LabStatus.Text = Loc.F("已应用节点：{0}", row.Node);

        Close();
    }
}
