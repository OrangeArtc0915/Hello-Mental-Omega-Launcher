using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using HMOL.Core.Backup;
using HMOL.Core.IO;
using HMOL.Core.Logging;

namespace HMOL.App.Windows;

/// <summary>
/// 长耗时操作的进度窗口。用 <see cref="Window.Show"/> 非模态打开，调用方在后台线程跑业务、
/// 主线程 await 结果；打开期间禁用宿主窗口，避免用户重复点击。取消按钮只置位令牌，
/// 由 Core 在每个检查点自行回滚。
/// <para>
/// 进度里带字节明细时（<see cref="Sample"/>），额外显示实时速度与预计剩余时间；
/// 只有百分比时（<see cref="Progress"/>，例如更新下载）就只显示百分比。
/// </para>
/// </summary>
public partial class ProgressWindow : Window
{
    /// <summary>速度与剩余时间的刷新节奏。比进度回调慢，够用又不至于让数字乱跳。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>算速度用的时间窗口：取窗口内最早的一个样本当基准。</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);

    /// <summary>基准窗口短于这个长度时不发布速度，免得拿几百毫秒的抖动当结论。</summary>
    private static readonly TimeSpan MinSpeedWindow = TimeSpan.FromSeconds(1);

    /// <summary>进度历史保留多久。速度用 3 秒窗口，剩余时间用整段历史。</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(15);

    /// <summary>一段时间点上的进度快照。</summary>
    private readonly record struct Snapshot(TimeSpan At, double Fraction, long Done, long Total);

    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _indeterminate;

    private Window? _ownerWindow;
    private string _baseDetail = "正在处理…";
    private bool _closed;

    private double _fraction;

    /// <summary>最近的进度快照。速度和剩余时间都从它推出来。</summary>
    private readonly List<Snapshot> _history = [];

    private TimeSpan? _lastEstimate;
    private double _speed;
    private bool _hasSpeed;

    public ProgressWindow(string title, bool indeterminate = false, bool canCancel = true)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        _indeterminate = indeterminate;

        // 不确定进度不做动画，只留文字与取消按钮，避免用户误以为卡死
        if (indeterminate) Bar.Visibility = Visibility.Collapsed;

        // 有些操作（例如递归删除目录）根本无法中断，此时诚实地不给取消按钮
        if (!canCancel) BtnCancel.Visibility = Visibility.Collapsed;

        Progress = new Progress<double>(value => OnSample(new ProgressSample(value)));
        Sample = new Progress<ProgressSample>(OnSample);

        // 没有新进度时也要能让速度衰减、剩余时间往前跑
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => RefreshRate();
        _timer.Start();
    }

    /// <summary>分数进度（0~1）。给只关心百分比的调用方用。</summary>
    public IProgress<double> Progress { get; }

    /// <summary>带字节明细的进度。给了字节就能显示实时速度。</summary>
    public IProgress<ProgressSample> Sample { get; }

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

    private void OnSample(ProgressSample sample)
    {
        if (_closed) return;

        _fraction = Math.Clamp(sample.Fraction, 0, 1);

        Record(new Snapshot(_clock.Elapsed, _fraction, sample.DoneBytes, sample.TotalBytes));

        Bar.Value = _fraction;
        LabDetail.Text = $"{_baseDetail} {(int)Math.Round(_fraction * 100)}%";

        if (!_indeterminate) UpdateSpeed();

        RefreshRate();
    }

    /// <summary>记一笔进度快照，并丢掉太旧的（列表因此不会无限增长）。</summary>
    private void Record(Snapshot snapshot)
    {
        _history.Add(snapshot);

        var cutoff = snapshot.At - HistoryWindow;
        var drop = 0;

        // 留一个比窗口更早的样本当基准，其余超期的丢掉
        while (drop + 1 < _history.Count && _history[drop + 1].At < cutoff) drop++;

        if (drop > 0) _history.RemoveRange(0, drop);
    }

    /// <summary>
    /// 用最近几秒的字节增量算速度。
    /// 窗口取 3 秒而不是「上一次回调到这一次」：7-Zip 这类外部程序开头回调很稀疏，
    /// 短窗口会把开头一大段进度算成「一瞬间完成的」，读出几百 MB/s 甚至几 GB/s 的虚高值，
    /// 之后才落到真实水平——看起来就是「开头很快、后面变慢」。
    /// </summary>
    private void UpdateSpeed()
    {
        if (_history.Count < 2) return;

        var newest = _history[^1];
        if (newest.Total <= 0) return;   // 这一段不掌握字节数，速度无从谈起

        var now = _clock.Elapsed;
        var baseIndex = _history.Count - 1;

        // 从最新往回找窗口内最早的样本；遇到换段（字节基数不同）就停
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Total != newest.Total) break;
            if (now - _history[i].At > SpeedWindow) break;

            baseIndex = i;
        }

        var span = now - _history[baseIndex].At;
        if (span < MinSpeedWindow) return;   // 窗口还太短，沿用上一次的速度

        var delta = newest.Done - _history[baseIndex].Done;
        if (delta < 0) return;

        var instant = delta / span.TotalSeconds;
        _speed = _hasSpeed ? _speed * 0.5 + instant * 0.5 : instant;
        _hasSpeed = true;
    }

    /// <summary>刷新「速度 · 预计剩余」。由计时器周期调用，所以停更时速度会归零。</summary>
    private void RefreshRate()
    {
        if (_closed || _indeterminate || _history.Count == 0)
        {
            LabRate.Visibility = Visibility.Collapsed;
            return;
        }

        // 好一会儿没有新进度了：速度不再可信，别把旧值挂在那儿骗人
        if (_hasSpeed && (_clock.Elapsed - _history[^1].At).TotalSeconds > 3)
        {
            _hasSpeed = false;
            _speed = 0;
        }

        var text = string.Empty;

        if (_hasSpeed && _speed > 1)
            text = $"{BackupService.FormatSize((long)_speed)}/s";

        var remaining = EstimateRemaining();

        if (remaining is { } span)
            text = text.Length == 0 ? $"预计剩余 {FormatDuration(span)}" : $"{text} · 预计剩余 {FormatDuration(span)}";

        if (text.Length == 0)
        {
            LabRate.Visibility = Visibility.Collapsed;
            return;
        }

        LabRate.Text = text;
        LabRate.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 按**近期**推进速度外推剩余时间，而不是用「已用时间 ÷ 已完成比例」。
    /// 后者一旦中途没有进展（例如整块解压），分母不动、时间在走，估计值会一路涨上去，
    /// 看起来像「越来越慢」；这里改成：这个窗口内没有推进就沿用上一次的估计，数字保持不动。
    /// </summary>
    private TimeSpan? EstimateRemaining()
    {
        if (_fraction < 0.01 || _fraction > 0.995) return null;
        if (_history.Count < 2) return _lastEstimate;

        var (baseAt, baseFraction, _, _) = _history[0];
        var span = _clock.Elapsed - baseAt;

        // 窗口还太短看不出速度，先用总平均顶一下；不足 1 秒干脆不显示
        if (span < MinSpeedWindow)
        {
            var elapsed = _clock.Elapsed.TotalSeconds;
            if (elapsed < 1) return _lastEstimate;

            _lastEstimate = TimeSpan.FromSeconds(elapsed * (1 - _fraction) / _fraction);
            return _lastEstimate;
        }

        var advanced = _fraction - baseFraction;

        // 这一窗口内没有任何推进：保留上一次的估计，别让它越涨越大
        if (advanced <= 0) return _lastEstimate;

        var rate = advanced / span.TotalSeconds;
        var remaining = (1 - _fraction) / rate;

        // 估到 24 小时以上基本没意义，宁可不显示
        if (remaining > 24 * 3600) return null;

        _lastEstimate = TimeSpan.FromSeconds(remaining);
        return _lastEstimate;
    }

    private static string FormatDuration(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes} 分 {span.Seconds} 秒"
                : $"{Math.Max(1, span.Seconds)} 秒";

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

            _timer.Stop();

            if (_ownerWindow is not null) _ownerWindow.IsEnabled = true;

            _cts.Dispose();
        }

        base.OnClosed(e);
    }
}
