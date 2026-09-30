using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using HMOL.Core.Logging;

namespace HMOL.App.Interop;

/// <summary>
/// 系统托盘图标。用 P/Invoke 的 <c>Shell_NotifyIcon</c> 自己实现，不引第三方托盘包，
/// 对应旧版 <c>tray.py</c>：隐藏消息窗口 + 图标 + 左键恢复 / 右键菜单。
///
/// <para>
/// 消息窗口用 WPF 的 <see cref="HwndSource"/> 建（父窗口设为 HWND_MESSAGE，即消息专用窗口），
/// 因此回调直接落在创建它的 UI 线程上，不需要旧版那样的跨线程命令队列。
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息。</summary>
    private const int WmTray = 0x8000 + 1;

    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;

    /// <summary>HWND_MESSAGE：消息专用窗口的父句柄。</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>IDI_APPLICATION，取不到程序图标时兜底。</summary>
    private static readonly IntPtr IdiApplication = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string exeFileName, int iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    private readonly HwndSource? _source;
    private readonly NotifyIconData _data;
    private readonly bool _ownsIcon;

    private bool _disposed;

    /// <summary>左键单击（恢复窗口）。</summary>
    public event Action? LeftClick;

    /// <summary>右键单击，参数是光标位置的屏幕坐标（设备像素）。</summary>
    public event Action<Point>? ContextMenuRequested;

    public TrayIcon(string tooltip)
    {
        HwndSource? source = null;

        try
        {
            var parameters = new HwndSourceParameters("HMOLTray")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ParentWindow = HwndMessage
            };

            source = new HwndSource(parameters);
            source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Log.Warn($"创建托盘消息窗口失败：{ex.Message}");

            try { source?.Dispose(); }
            catch { /* 忽略 */ }

            IsAvailable = false;
            _data = default;
            return;
        }

        _source = source;
        var handle = source.Handle;
        var (icon, owned) = LoadAppIcon();
        _ownsIcon = owned;

        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = icon,
            szTip = Truncate(tooltip, 127),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        if (!Shell_NotifyIcon(NimAdd, ref _data))
        {
            Log.Warn("系统托盘图标注册失败（可能被系统策略禁用）");
            _data = default;
            IsAvailable = false;
        }
    }

    /// <summary>是否有可用的托盘图标（注册失败时为 false，调用方应退回「最小化到任务栏」）。</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>更新悬停提示。</summary>
    public void SetTooltip(string tooltip)
    {
        if (_disposed || !IsAvailable) return;

        try
        {
            var data = _data;
            data.uFlags = NifTip;
            data.szTip = Truncate(tooltip, 127);
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch (Exception ex)
        {
            Log.Warn($"更新托盘提示失败：{ex.Message}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTray) return IntPtr.Zero;

        var mouseMessage = lParam.ToInt32() & 0xFFFF;

        switch (mouseMessage)
        {
            case WmLButtonUp:
                handled = true;
                Invoke(LeftClick);
                break;

            case WmRButtonUp:
                handled = true;

                if (GetCursorPos(out var point))
                    Invoke(ContextMenuRequested, new Point(point.X, point.Y));

                break;
        }

        return IntPtr.Zero;
    }

    private static void Invoke(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { Log.Warn($"托盘回调异常：{ex.Message}"); }
    }

    private static void Invoke(Action<Point>? handler, Point point)
    {
        try { handler?.Invoke(point); }
        catch (Exception ex) { Log.Warn($"托盘回调异常：{ex.Message}"); }
    }

    /// <summary>取程序图标：优先从自身 exe 里抠（app.manifest 已设 ApplicationIcon），失败退回系统默认图标。</summary>
    private static (IntPtr Icon, bool Owned) LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(exe))
            {
                var icon = ExtractIcon(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero && icon != new IntPtr(1)) return (icon, true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"提取程序图标失败：{ex.Message}");
        }

        try { return (LoadIcon(IntPtr.Zero, IdiApplication), false); }
        catch { return (IntPtr.Zero, false); }
    }

    private static string Truncate(string? text, int maxLength)
    {
        var value = text ?? string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_data.hWnd != IntPtr.Zero)
        {
            try
            {
                var data = _data;
                data.uFlags = 0;
                Shell_NotifyIcon(NimDelete, ref data);
            }
            catch (Exception ex)
            {
                Log.Warn($"移除托盘图标失败：{ex.Message}");
            }
        }

        if (_ownsIcon && _data.hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_data.hIcon); }
            catch { /* 忽略 */ }
        }

        try { _source?.Dispose(); }
        catch { /* 忽略 */ }
    }
}
