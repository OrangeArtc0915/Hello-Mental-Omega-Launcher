using System.Windows;
using HMOL.Core.Localization;
using HMOL.Core.Packages;

namespace HMOL.App.Windows;

/// <summary>
/// 安装前预览：列出包里会落进游戏目录的文件，并把目标位置已存在的标出来。
/// 只读不写，确认后才回到包管理页走原来的安装流程。
/// </summary>
public partial class PackagePreviewWindow : Window
{
    private bool _confirmed;

    private PackagePreviewWindow()
    {
        InitializeComponent();
    }

    /// <summary>清单里的一行。</summary>
    public sealed class PreviewRow
    {
        public required string Path { get; init; }

        /// <summary>「已存在」标记；空串表示目标位置还没有这个文件。</summary>
        public required string Badge { get; init; }
    }

    /// <summary>打开预览窗口；用户点「继续安装」返回 true。</summary>
    public static bool Ask(Window? owner, PackagePreview preview)
    {
        var window = new PackagePreviewWindow { Owner = owner };

        window.Fill(preview);

        return window.ShowDialog() == true && window._confirmed;
    }

    private void Fill(PackagePreview preview)
    {
        LabTitle.Text = Loc.F("安装前预览：{0}", preview.PackageName);

        if (preview.Error is { Length: > 0 } error)
        {
            LabSummary.Text = error;
            LabEmpty.Text = Loc.T("这个包暂时列不出文件，仍可继续安装。");
            LabEmpty.Visibility = Visibility.Visible;
            ListFiles.Visibility = Visibility.Collapsed;
            return;
        }

        LabSummary.Text = preview.ExistingFiles > 0
            ? Loc.F("共 {0} 个文件，其中 {1} 个在目标位置已有同名文件（安装时按冲突策略处理）。\n目标目录：{2}",
                preview.TotalFiles, preview.ExistingFiles, preview.TargetDirectory)
            : Loc.F("共 {0} 个文件，目标位置都没有同名文件。\n目标目录：{1}",
                preview.TotalFiles, preview.TargetDirectory);

        // 有冲突的排前面，方便一眼看到会覆盖什么
        ListFiles.ItemsSource = preview.Files
            .OrderByDescending(file => file.Exists)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => new PreviewRow
            {
                Path = file.RelativePath,
                Badge = file.Exists ? Loc.T("已存在") : string.Empty
            })
            .ToList();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _confirmed = true;
        DialogResult = true;
    }
}
