namespace HMOL.Core.Backup;

/// <summary>备份种类。</summary>
public enum BackupKind
{
    /// <summary>用户为某个实例做的备份：<c>backup\game\&lt;名称&gt;</c>。</summary>
    User = 0,

    /// <summary>原版游戏备份：<c>backup\MO</c>，名称固定不可改。</summary>
    Original = 1
}

/// <summary>
/// 备份目录里的 <c>backup_info.json</c>。字段沿用旧版（原版备份写 type/source_path，
/// 用户备份写 name/source_instance/installed_packages）。
/// </summary>
public sealed class BackupInfo
{
    /// <summary>"user" 或 "original"。</summary>
    public string Type { get; set; } = "user";

    public string Name { get; set; } = string.Empty;

    /// <summary>源实例名。原版备份时为空。</summary>
    public string SourceInstance { get; set; } = string.Empty;

    /// <summary>源实例 Id。原版备份为 "original"。</summary>
    public string SourceInstanceId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public int FileCount { get; set; }

    public int FailedCount { get; set; }

    public long SourceSizeBytes { get; set; }

    public long BackupSizeBytes { get; set; }

    /// <summary>备份时该实例已安装的包（键为分类目录名）。</summary>
    public Dictionary<string, List<string>> InstalledPackages { get; set; } = [];
}

/// <summary>备份列表里的一项。</summary>
/// <param name="Kind">备份种类。</param>
/// <param name="Name">备份名称。</param>
/// <param name="Path">备份目录。</param>
/// <param name="CreatedAt">创建时间。元数据缺失时退回目录修改时间。</param>
/// <param name="FileCount">文件数（不含 backup_info.json）。</param>
/// <param name="SizeBytes">体积（不含 backup_info.json）。</param>
/// <param name="SourceInstance">源实例名，未知为空。</param>
public sealed record BackupEntry(BackupKind Kind, string Name, string Path, DateTime? CreatedAt,
    int FileCount, long SizeBytes, string SourceInstance)
{
    /// <summary>列表展示用的种类文字。</summary>
    public string KindText => Kind == BackupKind.Original ? "原版游戏" : "用户备份";
}

/// <summary>备份 / 还原的结果。</summary>
public sealed record BackupOutcome(bool Success, bool Cancelled, string Message,
    int FileCount, int FailedFiles, long SizeBytes);

/// <summary>备份完整性抽样校验结果（对应旧版 _verify_backup_integrity）。</summary>
/// <param name="Verified">读取成功的文件数。</param>
/// <param name="Missing">元数据里声明但缺失的文件数。</param>
/// <param name="Mismatched">读取失败的文件数。</param>
/// <param name="Error">整体性错误（目录不存在 / 为空）时的说明。</param>
public sealed record BackupVerifyResult(int Verified, int Missing, int Mismatched, string Error);
