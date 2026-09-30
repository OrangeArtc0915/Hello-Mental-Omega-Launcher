using System.Windows;
using HMOL.Core.Logging;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>心灵终结 CNC 大厅延迟代码窗（对应旧版 <c>LagCodeDialog</c>）：显示并复制 <c>/framesendrate x</c>。</summary>
public partial class MultiplayerLagCodeWindow : Window
{
    private const string Command = "/framesendrate x";

    public MultiplayerLagCodeWindow()
    {
        InitializeComponent();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Command);
            LabCopied.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Log.Warn($"复制延迟指令失败：{ex.Message}");
            LabCopied.Text = "复制失败，可手动选中复制";
        }
    }
}
