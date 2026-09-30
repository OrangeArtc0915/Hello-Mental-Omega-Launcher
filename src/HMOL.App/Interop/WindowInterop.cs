using System.Runtime.InteropServices;

namespace HMOL.App.Interop;

/// <summary>
/// 窗口玻璃化。把 DWM 框架延伸到整个客户区后，窗口背景设为 null 的区域会透出桌面，
/// 这样就可以在窗口边缘的留白里自绘投影，做出「浮起来的圆角卡片」效果。
/// </summary>
internal static class WindowInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    public static bool IsCompositionEnabled()
    {
        try
        {
            return DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把整个窗口做成玻璃。margin 传 -1 表示铺满客户区。</summary>
    public static bool TryExtendFrameIntoClientArea(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsCompositionEnabled()) return false;

        try
        {
            var margins = new Margins { Left = -1, Top = -1, Right = -1, Bottom = -1 };
            return DwmExtendFrameIntoClientArea(handle, ref margins) == 0;
        }
        catch
        {
            return false;
        }
    }
}
