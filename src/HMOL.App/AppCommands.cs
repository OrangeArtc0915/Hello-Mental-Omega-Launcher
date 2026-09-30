using System.Windows.Input;

namespace HMOL.App.Controls;

/// <summary>
/// 应用级命令。窗口外壳模板（见 Controls.xaml）里的自绘关闭按钮绑定到这里，
/// 因为样式表里没法挂事件处理器。命令的绑定在 App 启动时统一注册。
/// </summary>
public static class AppCommands
{
    /// <summary>关闭当前窗口。</summary>
    public static readonly RoutedUICommand CloseWindow = new("关闭窗口", nameof(CloseWindow), typeof(AppCommands));
}