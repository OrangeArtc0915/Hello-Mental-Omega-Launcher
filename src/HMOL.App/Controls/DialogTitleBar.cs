using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HMOL.App.Controls;

/// <summary>
/// 对话框自绘标题栏上的拖动区。样式表里没法挂事件处理器，
/// 所以把「按住拖动区移动窗口」做成一个控件，供 Controls.xaml 的窗口模板使用。
/// </summary>
public class DialogTitleBar : Border
{
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (e.ClickCount != 1) return;

        var window = Window.GetWindow(this);
        if (window is null) return;

        // 鼠标可能在按下与处理之间已经抬起，DragMove 会抛异常，这里直接忽略
        try { window.DragMove(); }
        catch (InvalidOperationException) { }
    }
}