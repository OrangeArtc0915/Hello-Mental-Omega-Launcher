using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HMOL.Core.Instances;
using HMOL.Core.Localization;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;

namespace HMOL.Core.App;

/// <summary>
/// 启动器配置的打包备份与恢复：设置、联机配置与实例清单（不含游戏文件、插件包、壁纸与日志）。
/// 换机器或重装后导入即可把设置与实例列表找回来。
/// </summary>
public static class ConfigBackupService
{
    /// <summary>压缩包里统一的顶层目录名，导入时靠它定位内容。</summary>
    private const string RootFolder = "HMOL-config";

    /// <summary>备份默认文件名。</summary>
    public static string SuggestedFileName => $"HMOL_config_{DateTime.Now:yyyyMMdd_HHmmss}.zip";

    /// <summary>把当前配置打包到 <paramref name="destination"/>，返回写入的实例数。</summary>
    public static int Export(string destination)
    {
        var staging = Path.Combine(Paths.Temp, $"config_{Guid.NewGuid():N}");

        try
        {
            var root = Path.Combine(staging, RootFolder);
            Directory.CreateDirectory(root);

            if (File.Exists(Paths.SettingsFile))
                File.Copy(Paths.SettingsFile, Path.Combine(root, "Settings.json"), overwrite: true);

            var multiplayer = Path.Combine(Paths.Data, "multiplayer.json");
            if (File.Exists(multiplayer))
                File.Copy(multiplayer, Path.Combine(root, "multiplayer.json"), overwrite: true);

            var instances = CopyInstances(root);

            File.WriteAllText(Path.Combine(root, "manifest.txt"),
                string.Join(Environment.NewLine,
                    Loc.T("HMOL 配置备份"),
                    Loc.F("导出时间：{0:yyyy-MM-dd HH:mm:ss}", DateTime.Now),
                    Loc.F("启动器版本：{0}", AppInfo.VersionDisplay),
                    Loc.F("实例数量：{0}", instances)) + Environment.NewLine,
                new UTF8Encoding(true));

            WriteZip(staging, destination);

            return instances;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// 从备份压缩包恢复配置。设置与联机配置整体替换；实例按 Id 合并（同名覆盖、其余保留）。
    /// 恢复前会先把当前配置备份到 <c>Data\backup</c>。
    /// </summary>
    public static ConfigImportResult Import(string source)
    {
        var staging = Path.Combine(Paths.Temp, $"restore_{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(source, staging, overwriteFiles: true);

            var root = Path.Combine(staging, RootFolder);
            if (!Directory.Exists(root)) root = staging;

            var settings = Path.Combine(root, "Settings.json");
            if (!File.Exists(settings))
                return ConfigImportResult.Fail(Loc.T("这不是 HMOL 的配置备份：压缩包里没有 Settings.json。"));

            // 先验一遍 JSON：坏文件导进来会把启动器直接打回默认设置
            if (!IsValidJson(settings))
                return ConfigImportResult.Fail(Loc.T("备份里的 Settings.json 已损坏，未执行导入。"));

            // 恢复前留一份当前配置，导错了还能找回
            TrySafetyBackup();

            File.Copy(settings, Paths.SettingsFile, overwrite: true);

            var multiplayer = Path.Combine(root, "multiplayer.json");
            if (File.Exists(multiplayer) && IsValidJson(multiplayer))
                File.Copy(multiplayer, Path.Combine(Paths.Data, "multiplayer.json"), overwrite: true);

            var (written, skipped) = ImportInstances(root);

            // 内存里的旧配置必须立刻换掉：否则之后任何一次保存都会把刚导入的文件写回旧值
            SettingsStore.Load();
            MultiplayerSettingsStore.Load();
            InstanceStore.Load();

            return new ConfigImportResult(true, written, skipped, Loc.F("已导入 {0} 个实例配置（{1} 个无效已跳过）。", written, skipped));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("导入配置失败：{0}", source), ex);
            return ConfigImportResult.Fail(Loc.F("导入配置失败：{0}", ex.Message));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    // ————— 内部 —————

    private static int CopyInstances(string root)
    {
        try
        {
            if (!Directory.Exists(Paths.Instances)) return 0;

            var target = Path.Combine(root, "instances");
            Directory.CreateDirectory(target);

            var count = 0;

            foreach (var file in Directory.EnumerateFiles(Paths.Instances, "*.json"))
            {
                try
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn(Loc.F("配置备份：实例文件读取失败 {0}（{1}）", Path.GetFileName(file), ex.Message));
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("配置备份：读取实例目录失败（{0}）", ex.Message));
            return 0;
        }
    }

    private static (int Written, int Skipped) ImportInstances(string root)
    {
        var source = Path.Combine(root, "instances");
        if (!Directory.Exists(source)) return (0, 0);

        Directory.CreateDirectory(Paths.Instances);

        var written = 0;
        var skipped = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            if (!IsValidJson(file))
            {
                skipped++;
                continue;
            }

            try
            {
                File.Copy(file, Path.Combine(Paths.Instances, Path.GetFileName(file)), overwrite: true);
                written++;
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Warn(Loc.F("导入配置：实例写入失败 {0}（{1}）", Path.GetFileName(file), ex.Message));
            }
        }

        return (written, skipped);
    }

    /// <summary>导入前把当前配置另存到 backup 目录；失败只记日志，不阻断导入。</summary>
    private static void TrySafetyBackup()
    {
        try
        {
            Directory.CreateDirectory(Paths.Backup);

            var file = Path.Combine(Paths.Backup, $"config-before-restore-{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            Export(file);

            Log.Info(Loc.F("导入配置前已备份当前配置：{0}", file));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("导入配置前的自动备份失败：{0}", ex.Message));
        }
    }

    private static bool IsValidJson(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteZip(string sourceDirectory, string destination)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(destination)) File.Delete(destination);

        ZipFile.CreateFromDirectory(sourceDirectory, destination, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响结果
        }
    }
}

/// <summary>配置导入的结果。<see cref="Success"/> 为 false 时 <see cref="Message"/> 是失败原因。</summary>
public sealed record ConfigImportResult(bool Success, int InstancesWritten, int InstancesSkipped, string Message)
{
    public static ConfigImportResult Fail(string message) => new(false, 0, 0, message);
}
