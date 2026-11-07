using System.Windows.Controls;

namespace HMOL.App.Controls;

/// <summary>
/// 内容页基类。页面进入 / 离开时由外壳调用，页面自身可在此刷新数据。
/// </summary>
public class LauncherPage : UserControl
{
    public virtual void OnEnter()
    {
    }

    public virtual void OnLeave()
    {
    }

    /// <summary>
    /// 页面被外壳彻底丢弃时调用（目前只有换语言重建页面会走到）。
    /// 页面若订阅了静态事件、启动了轮询或持有窗口，必须在这里退订 / 停止，
    /// 否则旧页面会被静态事件一直引用着，越换语言泄漏得越多。
    /// </summary>
    public virtual void OnDestroyed()
    {
    }

    /// <summary>
    /// 页面是否自己负责进入动画。默认 false，由外壳（<c>MainWindow</c>）做通用错峰淡入。
    /// 自己实现的页面要负责：动画键带本页前缀、离开时按前缀收干净（见 <see cref="Animation.PageAnimator"/>）。
    /// </summary>
    public virtual bool HandlesEnterAnimation => false;

    /// <summary>
    /// 页面内部可切换的子视图数量（比如子标签页）。默认 1，表示没有子视图。
    /// 只给自检用：折叠起来的子视图不会被 WPF 布局，不切出来就查不出重叠。
    /// </summary>
    public virtual int SubViewCount => 1;

    /// <summary>切换到第 index 个子视图。只给自检用。</summary>
    public virtual void SelectSubView(int index)
    {
    }
}
