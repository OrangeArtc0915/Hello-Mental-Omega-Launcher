using System.IO;
using HMOL.Core.Instances;
using HMOL.Core.Logging;

namespace HMOL.Core.Packages;

/// <summary>包管理器目录里的一个可用包。</summary>
/// <param name="Name">包名。地图包是相对目录的路径，其余是文件/目录名。</param>
/// <param name="FullPath">绝对路径。</param>
/// <param name="IsDirectory">是否是目录形式的包。</param>
/// <param name="SizeBytes">体积（目录取递归大小）。</param>
public sealed record PackageEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes)
{
    /// <summary>是否压缩包（决定安装时用不用解压）。</summary>
    public bool IsArchive => !IsDirectory && PackageTypes.IsArchiveExtension(Path.GetExtension(Name));
}

/// <summary>
/// 包管理器目录（<c>packages\&lt;类型&gt;\</c>）的读取与维护。
/// 可见包判定按旧版 PackageManagerTab._refresh_available：
/// 地图包递归列出所有匹配扩展名的文件；其余类型只看顶层，跳过隐藏项与 desktop.ini，
/// 目录必须是「非空且含有匹配扩展名的文件」（或名字里带 config/setting/mod/theme 的 .ini）。
/// </summary>
public static class PackageLibrary
{
    /// <summary>列出某类型下可用的包。</summary>
    public static IReadOnlyList<PackageEntry> List(PackageType type)
    {
        var result = new List<PackageEntry>();
        var root = PackageTypes.DirectoryOf(type);

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            Log.Error($"创建包目录失败：{root}", ex);
            return result;
        }

        if (type == PackageType.Map)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!PackageTypes.AllowsFile(type, file)) continue;

                result.Add(new PackageEntry(IO.PathGuard.RelativeOf(root, file), file, false, FileLength(file)));
            }

            result.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            return result;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0) continue;

            // 跳过隐藏文件与 desktop.ini（旧版显式排除）
            if (name.StartsWith('.') || name.StartsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            // 目录名不能以非字母开头（旧版对目录名的额外过滤）
            if (Directory.Exists(path) && !char.IsLetter(name[0])) continue;
            // 卷标 / 桌面配置等不可打印名的文件（旧版靠扩展名过滤，这里再挡一次零长度）
            if (File.Exists(path) && FileLength(path) == 0) continue;

            if (Directory.Exists(path))
            {
                if (!IsValidDirectory(type, path)) continue;

                result.Add(new PackageEntry(name, path, true, IO.DirectoryCopier.GetSize(path)));
            }
            else
            {
                if (!PackageTypes.AllowsFile(type, name)) continue;

                result.Add(new PackageEntry(name, path, false, FileLength(path)));
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return result;
    }

    /// <summary>按包名查找；找不到返回 null（旧版会额外尝试去掉扩展名的写法）。</summary>
    public static PackageEntry? Find(PackageType type, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        foreach (var entry in List(type))
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)) return null;

        foreach (var entry in List(type))
        {
            if (string.Equals(entry.Name, Path.GetFileName(stem), StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    /// <summary>预览压缩包内的条目（对应旧版 _peek_package_contents）。</summary>
    public static IReadOnlyList<string> Peek(PackageType type, string packageName, int maxCount = 30)
    {
        var entry = Find(type, packageName);

        return entry is null || entry.IsDirectory
            ? []
            : ArchiveExtractor.ListEntries(entry.FullPath, maxCount);
    }

    /// <summary>
    /// 把一个包导入到管理器目录（对应旧版 _import_package）。
    /// overwrite 为 false 时目标已存在直接失败，由调用方决定是否覆盖。
    /// </summary>
    public static bool Import(PackageType type, string sourceFile, bool overwrite, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
        {
            message = $"所选文件不存在：{sourceFile}";
            return false;
        }

        var fileName = Path.GetFileName(sourceFile);
        var targetDirectory = PackageTypes.DirectoryOf(type);
        var target = Path.Combine(targetDirectory, fileName);

        if (File.Exists(target) && !overwrite)
        {
            message = $"目标目录已存在同名文件：{target}";
            return false;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);

            IO.DirectoryCopier.CopyFile(sourceFile, target);
            message = $"已成功导入：{target}";
            Log.Info($"已导入包：{sourceFile} → {target}");

            return true;
        }
        catch (Exception ex)
        {
            message = $"导入失败：{ex.Message}";
            Log.Error($"导入包失败：{sourceFile}", ex);
            return false;
        }
    }

    /// <summary>列出安装了该包的实例名（对应旧版 _remove_package 的使用状态检查）。</summary>
    public static IReadOnlyList<string> InUseBy(PackageType type, string packageName)
    {
        var result = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(packageName);

        foreach (var instance in InstanceStore.All)
        {
            var installed = instance.InstalledOf(type);

            if (installed.Any(item => string.Equals(item, packageName, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(item, stem, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(instance.Name);
            }
        }

        return result;
    }

    /// <summary>从管理器目录移除包（对应旧版 _remove_package）：只删源文件，不影响已安装的实例。</summary>
    public static bool Remove(PackageType type, string packageName, out string message)
    {
        message = string.Empty;

        var entry = Find(type, packageName);
        if (entry is null)
        {
            message = $"包文件不存在：{Path.Combine(PackageTypes.DirectoryOf(type), packageName)}";
            return false;
        }

        try
        {
            if (entry.IsDirectory) Directory.Delete(entry.FullPath, recursive: true);
            else File.Delete(entry.FullPath);

            message = $"已移除：{entry.Name}";
            Log.Info($"已移除包：{entry.FullPath}");
            return true;
        }
        catch (Exception ex)
        {
            message = $"移除失败：{ex.Message}\n文件可能被占用或权限不足，请关闭正在使用它的程序后重试。";
            Log.Error($"移除包失败：{entry.FullPath}", ex);
            return false;
        }
    }

    /// <summary>目录形式的包是否有效：非空且含匹配扩展名的文件（或名字带 config/setting/mod/theme 的 .ini）。</summary>
    private static bool IsValidDirectory(PackageType type, string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (PackageTypes.AllowsFile(type, file)) return true;

                if (!Path.GetExtension(file).Equals(".ini", StringComparison.OrdinalIgnoreCase)) continue;

                var directoryName = Path.GetFileName(directory).ToLowerInvariant();
                if (new[] { "config", "setting", "mod", "theme" }.Any(key => directoryName.Contains(key))) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"检查包目录失败：{directory}（{ex.Message}）");
        }

        return false;
    }

    private static long FileLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch
        {
            return 0;
        }
    }
}
