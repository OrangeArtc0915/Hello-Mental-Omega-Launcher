using System.Windows;
using HMOL.Core.Logging;

namespace HMOL.App.Windows;

/// <summary>
/// 长耗时操作的进度窗口。用 <see cref="Window.Show"/> 非模态打开，调用方在后台线程跑业务、
/// 主线程 await 结果；打开期间禁用宿主窗口，避免用户重复点击。取消按钮只置位令牌，
/// 由 Core 在每个检查点自行回滚。
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    private Window? _ownerWindow;
    private string _baseDetail = "正在处理…";
    private bool _closed;

    public ProgressWindow(string title, bool indeterminate = false, bool canCancel = true)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        // 不确定进度不做动画，只留文字与取消按钮，避免用户误以为卡死
        if (indeterminate) Bar.Visibility = Visibility.Collapsed;

        // 有些操作（例如递归删除目录）根本无法中断，此时诚实地不给取消按钮
        if (!canCancel) BtnCancel.Visibility = Visibility.Collapsed;

        Progress = new Progress<double>(OnProgress);
    }

    /// <summary>进度回调（0~1）。</summary>
    public IProgress<double> Progress { get; }

    /// <summary>取消令牌，交给 Core。</summary>
    public CancellationToken Token => _cts.Token;

    public bool CancelRequested { get; private set; }

    /// <summary>打开进度窗口并接管宿主窗口的交互。</summary>
    public static ProgressWindow Open(Window? owner, string title, string detail, bool indeterminate = false,
        bool canCancel = true)
    {
        var window = new ProgressWindow(title, indeterminate, canCancel) { Owner = owner };

        window.Show();
        window.SetDetail(detail);

        return window;
    }

    /// <summary>更新说明文字（后续的百分比会追加在后面）。</summary>
    public void SetDetail(string detail)
    {
        _baseDetail = detail;

        if (Dispatcher.CheckAccess()) LabDetail.Text = detail;
        else Dispatcher.InvokeAsync(() => LabDetail.Text = detail);
    }

    /// <summary>操作结束后复位宿主窗口并关闭自己。可在任意线程调用，重复调用无副作用。</summary>
    public void Finish()
    {
        // 用户可能已经从标题栏关掉了进度窗，或调用方重复收尾，这里一律忽略
        if (_closed) return;

        if (Dispatcher.CheckAccess()) Close();
        else Dispatcher.InvokeAsync(Close);
    }

    private void OnProgress(double value)
    {
        var clamped = Math.Clamp(value, 0d, 1d);

        Bar.Value = clamped;
        LabDetail.Text = $"{_baseDetail} {(int)Math.Round(clamped * 100)}%";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (CancelRequested) return;

        CancelRequested = true;
        BtnCancel.IsEnabled = false;
        BtnCancel.Content = "正在取消…";
        LabDetail.Text = "正在取消，请稍候…";

        try { _cts.Cancel(); }
        catch (Exception ex) { Log.Warn($"取消操作失败：{ex.Message}"); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _ownerWindow = Owner;
        if (_ownerWindow is not null) _ownerWindow.IsEnabled = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 用户也可能从标题栏直接关掉进度窗，这里只做一次收尾
        if (!_closed)
        {
            _closed = true;

            if (_ownerWindow is not null) _ownerWindow.IsEnabled = true;

            _cts.Dispose();
        }

        base.OnClosed(e);
    }
}
