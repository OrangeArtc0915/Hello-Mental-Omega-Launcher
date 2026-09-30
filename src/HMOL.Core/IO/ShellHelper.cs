using System.Diagnostics;
using System.IO;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.IO;

/// <summary>调用系统外壳打开网址或目录。失败只记日志，不向上抛。</summary>
public static class ShellHelper
{
    /// <summary>
    /// 用默认浏览器打开网址。只放行 http / https：其余协议（file:、ms-*、自定义协议等）
    /// 一律拒绝并记日志，免得配置文件里被塞进恶意链接时把系统外壳当启动器用。
    /// </summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        var text = url.Trim();

        if (!SiteLinkCatalog.IsHttpUrl(text))
        {
            Log.Warn($"拒绝打开非 http/https 链接：{text}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败 {text}：{ex.Message}");
        }
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path))
            {
                Log.Warn($"目录不存在，无法打开：{path}");
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败 {path}：{ex.Message}");
        }
    }
}
