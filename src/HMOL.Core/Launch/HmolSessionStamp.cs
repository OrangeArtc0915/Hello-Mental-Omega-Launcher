using System.Text;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.Instances;
using HMOL.Core.Localization;
using HMOL.Core.Logging;

namespace HMOL.Core.Launch;

/// <summary>
/// 启动游戏前，把「本次启动是谁、装了哪些包」写到游戏目录里的
/// <c>HMOL.session.txt</c>。
///
/// <para>
/// 联机补丁（MoLanRelay）在局域网大厅点「开始游戏」时会读这个文件，
/// 把 HMOL 版本与实例已装包清单通过大厅消息发给对端，双方核对后再开局。
/// 文件放在客户端主程序旁边，补丁用 <c>AppDomain.BaseDirectory</c> 就能找到；
/// 不通过 HMOL 启动时没有这个文件，补丁会提示「未检测到 HMOL 会话信息」并跳过核对。
/// </para>
/// </summary>
public static class HmolSessionStamp
{
    /// <summary>补丁读取的固定文件名。</summary>
    public const string FileName = "HMOL.session.txt";

    /// <summary>
    /// 写入会话信息。失败只记日志，不影响启动本身。
    /// </summary>
    /// <param name="instance">本次启动的实例。</param>
    /// <param name="executable">本次选中的主程序（可能为空，表示按类型自动探测）。</param>
    public static void Write(GameInstance instance, string? executable)
    {
        try
        {
            var directories = ResolveDirectories(instance, executable);
            if (directories.Count == 0)
            {
                Log.Warn(Loc.T("找不到游戏目录，联机前检查会提示未检测到 HMOL"));
                return;
            }

            var packages = instance.InstalledPackages.Values
                .SelectMany(list => list)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(Sanitize)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var text = new StringBuilder();
            text.Append("HMOL=").AppendLine(AppInfo.Version);
            foreach (var package in packages)
                text.Append("PACKAGE=").AppendLine(package);

            var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            foreach (var directory in directories)
                File.WriteAllText(Path.Combine(directory, FileName), text.ToString(), content);

            Log.Info(Loc.F("已写入联机会话信息：{0}（HMOL {1}，已装包 {2} 个）",
                string.Join("、", directories.Select(item => Path.Combine(item, FileName))),
                AppInfo.Version, packages.Count));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("写入联机会话信息失败（联机前检查会提示未检测到 HMOL）：{0}", ex.Message));
        }
    }

    /// <summary>
    /// 要写会话标记的目录。至少要覆盖「实际运行客户端的那一层」：
    /// MO 启动器 <c>MentalOmegaClient.exe</c> 在游戏根目录，而真正的 CnCNet 客户端是
    /// <c>Resources\clientdx.exe</c>，中继 DLL 也在 <c>Resources\</c> 里，
    /// 它读文件用的是 <c>AppDomain.BaseDirectory</c>（= Resources）。所以两处都要写。
    /// </summary>
    private static List<string> ResolveDirectories(GameInstance instance, string? executable)
    {
        var directories = new List<string>();

        var resolved = GameLocator.ResolveExecutable(instance.GameDir, executable)
                       ?? (string.IsNullOrWhiteSpace(executable) ? instance.ExecutablePath : null);

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            var directory = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) directories.Add(directory);
        }

        var clientDirectory = Path.Combine(instance.GameDir, "Resources");
        if (Directory.Exists(clientDirectory)) directories.Add(clientDirectory);

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>包名要能原样放进一行文本：去掉换行与补丁消息用的分隔符。</summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\u0001' or '\u0002') continue;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }
}
