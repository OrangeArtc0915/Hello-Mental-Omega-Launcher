using System.Diagnostics;
using System.IO;

namespace HMOL.Core.IO;

/// <summary>
/// 目录联接（junction）的创建与安全移除。
/// 导出 7z 时用它在暂存目录里"虚拟"出一个 <c>game_files</c> 指向真实游戏目录，
/// 避免为了打包先把几个 GB 的游戏目录整个复制一份。
///
/// 用 cmd 的 mklink / rmdir 而不是 <see cref="Directory"/> 的符号链接 API：
/// 创建符号链接需要开发者模式或管理员权限，目录联接不需要；而删除联接时 rmdir 只摘除联接本身，
/// 绝不会递归进目标目录——这一点至关重要，写错了会删掉用户的游戏文件。
/// </summary>
public static class Junction
{
    /// <summary>
    /// 建立 <paramref name="linkPath"/> → <paramref name="targetPath"/> 的目录联接。
    /// 已存在同名联接时直接视为成功。常见失败原因：目标分区不是 NTFS（exFAT / FAT32 / 网络盘）。
    /// </summary>
    public static bool TryCreate(string linkPath, string targetPath, out string error)
    {
        error = string.Empty;

        try
        {
            if (Directory.Exists(linkPath)) return true;

            var parent = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        var exitCode = RunCmd($"mklink /J {Quote(linkPath)} {Quote(targetPath)}", out var output);

        if (exitCode == 0 && Directory.Exists(linkPath)) return true;

        error = string.IsNullOrWhiteSpace(output) ? $"mklink 返回 {exitCode}" : output.Trim();
        return false;
    }

    /// <summary>只摘除联接本身，不动目标目录里的任何内容。</summary>
    public static void Remove(string linkPath)
    {
        try
        {
            if (!Directory.Exists(linkPath)) return;

            RunCmd($"rmdir {Quote(linkPath)}", out _);
        }
        catch
        {
            // 摘不掉也不该让导出流程失败，剩余的空目录随暂存目录一起清
        }
    }

    private static int RunCmd(string arguments, out string output)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", "/c " + arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                output = "无法启动 cmd.exe";
                return -1;
            }

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    private static string Quote(string value) => '"' + value + '"';
}
