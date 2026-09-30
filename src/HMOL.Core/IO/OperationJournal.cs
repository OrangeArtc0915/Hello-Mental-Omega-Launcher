using System.IO;
using HMOL.Core.Logging;

namespace HMOL.Core.IO;

/// <summary>
/// 一次写操作的痕迹记录，用于「部分失败回滚」。
/// 安装 / 卸载 / 还原这类多文件操作边做边登记，全部成功调 <see cref="Commit"/> 落地，
/// 中途失败或取消调 <see cref="Rollback"/> 逆序撤销已做的部分：
///   - 本次新建的文件 → 删除
///   - 本次覆盖的已有文件 → 用操作前留下的 .bak-&lt;时间戳&gt; 覆盖回去
///   - 被整体搬到隔离目录的目录/文件 → 先清掉新内容再搬回原位
/// </summary>
public sealed class OperationJournal
{
    private enum Kind
    {
        Created,
        Overwritten,
        Moved
    }

    private sealed record Entry(Kind Kind, string Path, string Extra);

    private readonly List<Entry> _entries = [];
    private readonly string _quarantineRoot;

    public OperationJournal(string quarantineRoot)
    {
        _quarantineRoot = Path.Combine(quarantineRoot, $"op-{DateTime.Now:yyyyMMdd_HHmmss_fff}");
    }

    /// <summary>隔离目录。被搬走的原内容暂存在这里，成功提交后删除、失败回滚后搬回。</summary>
    public string QuarantineRoot => _quarantineRoot;

    /// <summary>登记「本次新建的文件」，回滚时删除。</summary>
    public void TrackCreated(string path) => _entries.Add(new Entry(Kind.Created, path, string.Empty));

    /// <summary>登记「本次覆盖的已有文件」，回滚时用备份覆盖回去。</summary>
    public void TrackOverwritten(string path, string backupPath)
        => _entries.Add(new Entry(Kind.Overwritten, path, backupPath));

    /// <summary>登记「被整体搬到隔离目录的原内容」，回滚时搬回原位。</summary>
    public void TrackMoved(string originalPath, string quarantinePath)
        => _entries.Add(new Entry(Kind.Moved, originalPath, quarantinePath));

    /// <summary>
    /// 覆盖前备份。同一文件在一次操作里只备份一次；文件不存在则什么都不做。
    /// 返回备份文件路径（未备份时为 null）。
    /// </summary>
    public string? BackupExisting(string file)
    {
        if (!File.Exists(file)) return null;
        if (_entries.Any(entry => entry.Kind == Kind.Overwritten &&
                                  string.Equals(entry.Path, file, StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var backup = $"{file}.bak-{stamp}";
            var suffix = 1;
            while (File.Exists(backup)) backup = $"{file}.bak-{stamp}-{suffix++}";

            File.Copy(file, backup, overwrite: false);
            TrackOverwritten(file, backup);
            return backup;
        }
        catch (Exception ex)
        {
            Log.Warn($"覆盖前备份失败：{file}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>把已有的文件/目录整体搬到隔离目录，返回新位置；源不存在返回 null。</summary>
    public string? MoveToQuarantine(string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        if (!exists) return null;

        try
        {
            Directory.CreateDirectory(_quarantineRoot);

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Length == 0) name = "unnamed";

            var target = Path.Combine(_quarantineRoot, name);
            var suffix = 1;
            while (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(_quarantineRoot, $"{name}-{suffix++}");

            DirectoryCopier.MovePath(path, target);
            TrackMoved(path, target);

            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"移动到隔离目录失败：{path}（{ex.Message}）");
            return null;
        }
    }

    /// <summary>整份操作成功：删掉隔离目录。覆盖前留下的 .bak 备份会保留，方便用户自己找回原文件。</summary>
    public void Commit()
    {
        DeleteQuarantine();
    }

    /// <summary>逆序撤销本次操作已做的部分。返回撤销失败的文件数。</summary>
    public int Rollback()
    {
        var failed = 0;

        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];

            try
            {
                switch (entry.Kind)
                {
                    case Kind.Created:
                        if (File.Exists(entry.Path)) File.Delete(entry.Path);
                        break;

                    case Kind.Overwritten:
                        if (!File.Exists(entry.Extra)) break;

                        Directory.CreateDirectory(Path.GetDirectoryName(entry.Path) ?? ".");
                        File.Copy(entry.Extra, entry.Path, overwrite: true);
                        File.Delete(entry.Extra);
                        break;

                    case Kind.Moved:
                        if (!File.Exists(entry.Extra) && !Directory.Exists(entry.Extra)) break;

                        // 先清掉新内容，再把原内容搬回原位
                        if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, recursive: true);
                        else if (File.Exists(entry.Path)) File.Delete(entry.Path);

                        Directory.CreateDirectory(Path.GetDirectoryName(entry.Path) ?? ".");
                        DirectoryCopier.MovePath(entry.Extra, entry.Path);
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"回滚失败：{entry.Path}（{ex.Message}）");
            }
        }

        TryDeleteEmptyQuarantine();

        Log.Info($"回滚完成：登记 {_entries.Count} 条，失败 {failed} 条");
        return failed;
    }

    /// <summary>回滚完把隔离目录收拾掉；里面还有东西（搬回失败）就留着，不硬删数据。</summary>
    private void TryDeleteEmptyQuarantine()
    {
        try
        {
            if (!Directory.Exists(_quarantineRoot)) return;
            if (Directory.EnumerateFileSystemEntries(_quarantineRoot).Any())
            {
                Log.Warn($"隔离目录里仍有内容，未删除：{_quarantineRoot}");
                return;
            }

            Directory.Delete(_quarantineRoot);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理隔离目录失败：{_quarantineRoot}（{ex.Message}）");
        }
    }

    private void DeleteQuarantine()
    {
        try
        {
            if (Directory.Exists(_quarantineRoot)) Directory.Delete(_quarantineRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理隔离目录失败：{_quarantineRoot}（{ex.Message}）");
        }
    }
}
