using System.IO;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Packages;

/// <summary>
/// 插件包的分类。旧版有七类（ini / map / mission / voice / plugin / beautification / music），
/// 新版按用户要求把 voice / beautification / music 合并进 <see cref="Plugin"/>，只保留四类。
/// </summary>
public enum PackageType
{
    /// <summary>INI 包。目录名固定 "ini"（旧版曾用 "mod"，见 <see cref="PackageTypes.LegacyIniDirectoryName"/>）。</summary>
    Ini = 0,

    /// <summary>地图包。单个 .map 文件装到 Maps\Custom，压缩包与其它类型一致解压到游戏根目录。</summary>
    Map = 1,

    /// <summary>任务包。</summary>
    Mission = 2,

    /// <summary>
    /// 插件包。合并了旧版的语音 / 美化 / 音乐三类，但只接受压缩包
    /// （.7z / .zip / .rar），不再接受散装的 .mp3 / .ogg / .wav / .dll。
    /// </summary>
    Plugin = 3
}

/// <summary>一类包的元信息。</summary>
/// <param name="Type">分类。</param>
/// <param name="DisplayName">界面展示用的中文名。</param>
/// <param name="DirectoryName">包管理器目录下的子目录名。</param>
/// <param name="Extensions">允许出现在该分类下的文件扩展名。</param>
public sealed record PackageTypeSpec(
    PackageType Type,
    string DisplayName,
    string DirectoryName,
    IReadOnlyList<string> Extensions);

/// <summary>四类包的目录约定与扩展名规则。对应旧版 <c>_get_package_configs</c> / <c>_get_package_dirs</c>。</summary>
public static class PackageTypes
{
    /// <summary>旧版 INI 包目录名。存在时自动迁移为 "ini"。</summary>
    public const string LegacyIniDirectoryName = "mod";

    /// <summary>分类元信息。属性而非静态字段：显示名随界面语言变化。</summary>
    private static PackageTypeSpec[] Specs =>
    [
        new(PackageType.Ini, "INI", "ini", ArchiveFormats.Extensions),
        new(PackageType.Map, Loc.T("地图"), "map", [".map", .. ArchiveFormats.Extensions]),
        new(PackageType.Mission, Loc.T("任务"), "mission", ArchiveFormats.Extensions),
        new(PackageType.Plugin, Loc.T("插件"), "plugin", ArchiveFormats.Extensions)
    ];

    public static IReadOnlyList<PackageType> All { get; } =
        [PackageType.Ini, PackageType.Map, PackageType.Mission, PackageType.Plugin];

    /// <summary>取分类的元信息。</summary>
    public static PackageTypeSpec Of(PackageType type)
        => Specs.First(spec => spec.Type == type);

    /// <summary>分类在包管理器目录下的子目录名（"ini" / "map" / "mission" / "plugin"）。</summary>
    public static string DirectoryNameOf(PackageType type) => Of(type).DirectoryName;

    /// <summary>分类对应的包管理器绝对目录。</summary>
    public static string DirectoryOf(PackageType type) => Path.Combine(Paths.Packages, DirectoryNameOf(type));

    /// <summary>按目录名或枚举名解析分类，兼容旧版的 "mod" 写法与大小写差异。</summary>
    public static bool TryParse(string? text, out PackageType type)
    {
        type = PackageType.Ini;

        var name = (text ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        if (name.Equals(LegacyIniDirectoryName, StringComparison.OrdinalIgnoreCase)) name = "ini";

        foreach (var candidate in All)
        {
            if (candidate.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                type = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>按目录名解析分类。取不到时返回 null。</summary>
    public static PackageType? ParseOrNull(string? text) => TryParse(text, out var type) ? type : null;

    /// <summary>判断一个文件是不是压缩包（按扩展名，与旧版一致）。</summary>
    public static bool IsArchiveFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        return IsArchiveExtension(Path.GetExtension(path));
    }

    /// <summary>扩展名是否是压缩包（含前导点，忽略大小写）。</summary>
    public static bool IsArchiveExtension(string? extension)
        => ArchiveFormats.IsSupportedExtension(extension);

    /// <summary>文件名是否符合该分类允许的扩展名。</summary>
    public static bool AllowsFile(PackageType type, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return Of(type).Extensions.Contains(extension);
    }

    /// <summary>确保四类包的目录都存在，并把旧版的 packages\mod 迁移为 packages\ini。</summary>
    public static void EnsureDirectories()
    {
        MigrateLegacyIniDirectory();

        foreach (var type in All)
        {
            try
            {
                Directory.CreateDirectory(DirectoryOf(type));
            }
            catch (Exception ex)
            {
                Log.Error(Loc.F("创建包目录失败：{0}", DirectoryOf(type)), ex);
            }
        }
    }

    /// <summary>旧版 INI 包目录名为 "mod"：packages\mod 还在、packages\ini 还没有时自动重命名。</summary>
    private static void MigrateLegacyIniDirectory()
    {
        var oldDirectory = Path.Combine(Paths.Packages, LegacyIniDirectoryName);
        var newDirectory = DirectoryOf(PackageType.Ini);

        if (!Directory.Exists(oldDirectory) || Directory.Exists(newDirectory)) return;

        try
        {
            Directory.Move(oldDirectory, newDirectory);
            Log.Info(Loc.F("已将旧版 INI 包目录重命名：{0} → {1}", oldDirectory, newDirectory));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.F("重命名 INI 包目录失败：{0}", oldDirectory), ex);
        }
    }
}
