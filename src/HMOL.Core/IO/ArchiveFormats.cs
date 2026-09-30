namespace HMOL.Core.IO;

/// <summary>
/// 程序支持的压缩包格式。插件包与实例包的格式策略统一在这里，
/// 避免两处白名单各写一份、日后改一处漏一处。
/// 注：<see cref="Packages.ArchiveExtractor"/> 底层还能识别 tar / gz，但不属于对外支持的格式。
/// </summary>
public static class ArchiveFormats
{
    /// <summary>支持的扩展名（含前导点，全小写）。</summary>
    public static readonly string[] Extensions = [".zip", ".7z", ".rar"];

    /// <summary>
    /// 可以**导出**（写出）的扩展名，比导入少一个 .rar：
    /// RAR 是专有格式，没有任何可用的写入实现，7-Zip 也创建不了 rar，
    /// 唯一途径是调用 WinRAR 的 rar.exe（商业软件，不能随包分发），因此 rar 只支持导入。
    /// </summary>
    public static readonly string[] ExportExtensions = [".zip", ".7z"];

    /// <summary>面向用户展示的格式列表，例如 ".zip / .7z / .rar"。</summary>
    public static string Display => string.Join(" / ", Extensions);

    /// <summary>面向用户展示的可导出格式列表。</summary>
    public static string ExportDisplay => string.Join(" / ", ExportExtensions);

    /// <summary>文件选择对话框用的过滤器片段，例如 "*.zip;*.7z;*.rar"。</summary>
    public static string PickPattern => string.Join(";", Extensions.Select(extension => "*" + extension));

    /// <summary>导出对话框用的过滤器片段（不含 .rar）。</summary>
    public static string ExportPickPattern => string.Join(";", ExportExtensions.Select(extension => "*" + extension));

    /// <summary>扩展名是否受支持（含前导点，忽略大小写）。</summary>
    public static bool IsSupportedExtension(string? extension)
        => !string.IsNullOrWhiteSpace(extension) && Extensions.Contains(extension.ToLowerInvariant());

    /// <summary>路径的扩展名是否受支持。</summary>
    public static bool IsSupported(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsSupportedExtension(Path.GetExtension(path));

    /// <summary>路径的扩展名是否可导出。</summary>
    public static bool IsExportable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return ExportExtensions.Contains(extension);
    }
}
