using HMOL.Core.IO;

namespace HMOL.Core.Packages;

/// <summary>安装前对「将被覆盖的已有文件」的快照条目（对应旧版 snapshot_existing_files 的 {size, mtime}）。</summary>
/// <param name="Size">文件字节数。</param>
/// <param name="ModifiedAt">文件修改时间。</param>
public sealed record FileSnapshot(long Size, DateTime ModifiedAt);

/// <summary>
/// 一个包的精确安装记录。用于按包卸载：只删这个包装进去的文件，其余不动。
/// 字段沿用旧版 install_records\&lt;type&gt;\&lt;name&gt;.json 的结构：
/// package_type / package_name / source_archive / install_time / instance_id / instance_name /
/// file_count / files / original_snapshot。
/// </summary>
public sealed class InstallRecord
{
    public string PackageType { get; set; } = string.Empty;

    public string PackageName { get; set; } = string.Empty;

    /// <summary>来源压缩包文件名，非压缩包为空。</summary>
    public string SourceArchive { get; set; } = string.Empty;

    public DateTime InstallTime { get; set; } = DateTime.Now;

    public string InstanceId { get; set; } = string.Empty;

    public string InstanceName { get; set; } = string.Empty;

    /// <summary>安装的文件数。由 <see cref="Files"/> 推导，读旧数据时也接受这个字段。</summary>
    public int FileCount { get; set; }

    /// <summary>本次安装落进游戏目录的文件，相对**游戏目录**的路径，统一用 "/" 分隔。</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>安装前原文件快照，键为相对游戏目录的路径。</summary>
    public Dictionary<string, FileSnapshot> OriginalSnapshot { get; set; } = [];

    /// <summary>去掉重复项与非法相对路径，并同步文件数。</summary>
    public void Normalize()
    {
        var files = new List<string>();

        foreach (var file in Files)
        {
            var relative = PathGuard.NormalizeRelative(file);
            if (relative.Length == 0) continue;
            if (files.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;

            files.Add(relative);
        }

        Files = files;
        FileCount = files.Count;
    }
}
