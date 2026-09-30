using System.Windows;
using HMOL.Core.Logging;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 分享文本窗（对应旧版 <c>ShareDialog</c> 的文本部分）：展示并复制组网分享文本。
/// 旧版的二维码矩阵按本轮范围裁剪，不做。
/// </summary>
public partial class MultiplayerShareWindow : Window
{
    public MultiplayerShareWindow(string shareText)
    {
        InitializeComponent();

        TxtShare.Text = shareText;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtShare.Text);
            LabCopied.Text = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            Log.Warn($"复制分享文本失败：{ex.Message}");
            LabCopied.Text = "复制失败，可手动选中复制";
        }
    }
}
