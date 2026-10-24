using HMOL.Core.Logging;

namespace HMOL.App;

/// <summary>
/// 「关于」连点五次的小彩蛋：本次运行内有效。
/// 触发后联机页出现「联机补丁」提示；装好即消失，想再看需重新连点五次。
/// </summary>
internal static class EasterEgg
{
    /// <summary>触发所需的连点次数。</summary>
    private const int ClicksToReveal = 5;

    /// <summary>两次点击的最大间隔，超过就从 1 重新计数（「连续」点击）。</summary>
    private static readonly TimeSpan ClickWindow = TimeSpan.FromSeconds(2);

    private static int _clicks;
    private static DateTime _lastClickUtc = DateTime.MinValue;

    /// <summary>彩蛋是否已被触发（本次运行）。</summary>
    public static bool Revealed { get; private set; }

    /// <summary>补丁是否已装好；装好后提示条隐藏。</summary>
    public static bool Installed { get; private set; }

    /// <summary>已下载好的补丁路径，空串表示还没下载。</summary>
    public static string DownloadedFile { get; private set; } = string.Empty;

    /// <summary>状态变化（触发 / 下载完 / 装好），联机页据此刷新提示条。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 记一次「关于」点击。返回 true 表示这一次刚好凑满五次、彩蛋被触发。
    /// </summary>
    public static bool RegisterAboutClick()
    {
        var now = DateTime.UtcNow;

        _clicks = now - _lastClickUtc <= ClickWindow ? _clicks + 1 : 1;
        _lastClickUtc = now;

        if (_clicks < ClicksToReveal) return false;

        _clicks = 0;
        Revealed = true;
        Installed = false;
        DownloadedFile = string.Empty;

        Log.Info("绮梦是猪");
        Changed?.Invoke();

        return true;
    }

    /// <summary>补丁下载完成。</summary>
    public static void MarkDownloaded(string path)
    {
        DownloadedFile = path;
        Changed?.Invoke();
    }

    /// <summary>补丁已装进某个实例，提示条收起。</summary>
    public static void MarkInstalled()
    {
        Installed = true;
        DownloadedFile = string.Empty;
        Changed?.Invoke();
    }
}
