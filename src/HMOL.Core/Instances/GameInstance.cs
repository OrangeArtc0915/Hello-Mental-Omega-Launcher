using System.IO;
using System.Text.Json.Serialization;
using HMOL.Core.App;
using HMOL.Core.Games;
using HMOL.Core.Packages;
using HMOL.Core.Localization;

namespace HMOL.Core.Instances;

/// <summary>
/// 一个游戏实例。实例只记录「游戏装在哪」，不复制游戏本体；
/// 实例自己的数据（安装记录、隔离区）放在 <c>instances\&lt;id&gt;\</c> 下。
///
/// 与旧版 GameInstance 的差异：
/// - 实例配置从 <c>instances\&lt;名称&gt;\config.json</c> 改为 <c>instances\&lt;id&gt;.json</c>，避免重命名实例要搬目录；
/// - installed_packages 只保留四类（旧版的 voice / beautification / music 读取时并入 plugin）。
/// </summary>
public sealed class GameInstance
{
    /// <summary>实例 Id。用 <c>instance_&lt;时间&gt;_&lt;短随机&gt;</c> 形式，可安全用作文件名。</summary>
    public string Id { get; set; } = NewId();

    public string Name { get; set; } = string.Empty;

    /// <summary>备注。旧版是运行时挂在对象上的 description，这里落盘。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>游戏目录（旧版的 path）。</summary>
    public string GameDir { get; set; } = string.Empty;

    /// <summary>
    /// 游戏类型。决定默认用哪个主程序名探测、卡片上显示哪个图标。
    /// 旧配置文件里没有这个字段时按心灵终结处理。
    /// </summary>
    public GameKind Kind { get; set; } = GameKind.MentalOmega;

    /// <summary>
    /// 用户手工指定的主程序。留空表示按 <see cref="Kind"/> 自动探测。
    /// 位于游戏目录内时存相对文件名（换目录/导入后仍可用），否则存绝对路径。
    /// 用于「其它红警 Mod」这类默认主程序名不固定的情况。
    /// </summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>
    /// 多版本：同一个游戏目录里的多个可执行文件，各带一个用户起的名字（如「3.3.6」「3.3.5」）。
    ///
    /// <para>
    /// 空表示不是多版本实例——按 <see cref="Executable"/> 或按类型自动探测启动。
    /// 配了 2 个以上时，点「启动游戏」会先让用户选启动哪个版本。
    /// </para>
    /// </summary>
    public List<InstanceVersion> Versions
    {
        get => _versions;
        set => _versions = NormalizeVersions(value);
    }

    private List<InstanceVersion> _versions = [];

    /// <summary>是不是配了多个版本（启动时要先选版本）。</summary>
    [JsonIgnore]
    public bool HasMultipleVersions => Versions.Count > 1;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最后一次成功启动游戏的时刻，随实例配置落盘，重启程序后仍然保留；从未启动过为 null。
    /// 主页「当前实例」卡里显示它。
    /// </summary>
    public DateTime? LastLaunchedAt { get; set; }

    /// <summary>
    /// 已安装的包：键为分类目录名（"ini" / "map" / "mission" / "plugin"），值为包名（压缩包去掉扩展名）。
    /// 用字符串键而不是枚举键，才能原样读入旧版数据（旧版还有 voice / beautification / music 三个键）。
    /// setter 会重建忽略大小写的字典并丢掉空值，避免反序列化后比较器丢失、或旧数据里的 null 引发空引用。
    /// </summary>
    public Dictionary<string, List<string>> InstalledPackages
    {
        get => _installedPackages;
        set => _installedPackages = NormalizePackages(value);
    }

    private Dictionary<string, List<string>> _installedPackages = new(StringComparer.OrdinalIgnoreCase);

    // ————— 以下为运行时派生，不写入配置文件 —————

    /// <summary>实例自己的数据目录：<c>instances\&lt;id&gt;\</c>。</summary>
    [JsonIgnore]
    public string InstanceDirectory => Paths.InstanceDir(Id);

    /// <summary>精确安装记录目录：<c>instances\&lt;id&gt;\install_records\</c>。</summary>
    [JsonIgnore]
    public string InstallRecordsDirectory => Path.Combine(InstanceDirectory, "install_records");

    /// <summary>游戏目录当前是否仍可用（用户指定了主程序或配了版本时放宽，见 <see cref="GameLocator.IsGameDirectory"/>）。</summary>
    [JsonIgnore]
    public bool IsValid => GameLocator.IsGameDirectory(GameDir, Kind, EffectiveExecutable);

    /// <summary>校验用的主程序：配了版本用第一个版本，否则用 <see cref="Executable"/>。</summary>
    [JsonIgnore]
    public string EffectiveExecutable => Versions.Count > 0 ? Versions[0].Executable : Executable;

    /// <summary>用户指定的主程序解析后的绝对路径；未指定或文件已不存在为 null。</summary>
    [JsonIgnore]
    public string? CustomExecutablePath => GameLocator.ResolveExecutable(GameDir, Executable);

    /// <summary>第一个版本的可执行文件解析后的绝对路径（没配版本时为 null）。</summary>
    [JsonIgnore]
    public string? FirstVersionPath
        => Versions.Count > 0 ? GameLocator.ResolveExecutable(GameDir, Versions[0].Executable) : null;

    /// <summary>游戏主程序路径：优先用第一个版本，其次用户指定的，最后按类型自动探测；都找不到为 null。</summary>
    [JsonIgnore]
    public string? ExecutablePath
        => FirstVersionPath ?? CustomExecutablePath ?? GameLocator.FindExecutable(GameDir, Kind);

    /// <summary>界面上的一句摘要。</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            if (!Directory.Exists(GameDir)) return Loc.T("游戏目录不可用");
            if (!IsValid) return Loc.T("未找到主程序，可在「编辑」里指定可执行文件");

            if (ExecutablePath is null) return Loc.T("游戏目录有效，但未找到主程序");

            return HasMultipleVersions ? Loc.F("就绪（{0} 个版本）", Versions.Count) : Loc.T("就绪");
        }
    }

    /// <summary>取某分类下已安装的包名（只读副本）。</summary>
    public IReadOnlyList<string> InstalledOf(PackageType type)
    {
        var key = PackageTypes.DirectoryNameOf(type);
        return InstalledPackages.TryGetValue(key, out var list) ? [.. list] : [];
    }

    /// <summary>记录一个已安装的包名（已存在则不重复添加）。</summary>
    public void AddInstalled(PackageType type, string packageName)
    {
        var name = NormalizePackageName(packageName);
        if (name.Length == 0) return;

        var key = PackageTypes.DirectoryNameOf(type);
        if (!InstalledPackages.TryGetValue(key, out var list))
        {
            list = [];
            InstalledPackages[key] = list;
        }

        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
    }

    /// <summary>移除已安装的包名。</summary>
    public void RemoveInstalled(PackageType type, string packageName)
    {
        var key = PackageTypes.DirectoryNameOf(type);
        if (!InstalledPackages.TryGetValue(key, out var list)) return;

        var raw = (packageName ?? string.Empty).Trim();
        if (raw.Length == 0) return;

        // 旧数据里的名字可能带或不带扩展名（"A.zip" / "A"），这里三种写法都认，
        // 否则卸载后列表里会留下删不掉的同名项。
        var normalized = NormalizePackageName(raw);
        var stem = Path.GetFileNameWithoutExtension(raw);

        list.RemoveAll(item =>
            string.Equals(item, raw, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(item), stem, StringComparison.OrdinalIgnoreCase));

        if (list.Count == 0) InstalledPackages.Remove(key);
    }

    /// <summary>把一个分类下已安装的包名整体替换。</summary>
    public void SetInstalled(PackageType type, IEnumerable<string> packageNames)
    {
        var key = PackageTypes.DirectoryNameOf(type);
        var names = packageNames.Select(NormalizePackageName).Where(name => name.Length > 0).Distinct().ToList();

        if (names.Count == 0) InstalledPackages.Remove(key);
        else InstalledPackages[key] = names;
    }

    /// <summary>清空全部已安装记录（对应旧版全量恢复原版后的处理）。</summary>
    public void ClearInstalled() => InstalledPackages.Clear();

    /// <summary>
    /// 兼容旧版数据：把 voice / beautification / music 三类并入 plugin，mod 并入 ini，
    /// 去掉非列表值与重复项。
    /// </summary>
    public void MigrateLegacyPackages()
    {
        var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, values) in InstalledPackages)
        {
            if (values is null) continue;

            var type = PackageTypes.ParseOrNull(rawKey) ?? PackageType.Plugin;
            var key = PackageTypes.DirectoryNameOf(type);

            if (!merged.TryGetValue(key, out var list))
            {
                list = [];
                merged[key] = list;
            }

            foreach (var value in values)
            {
                var name = NormalizePackageName(value);
                if (name.Length == 0) continue;
                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
            }
        }

        InstalledPackages = merged;
    }

    /// <summary>包名统一用「去掉压缩包扩展名」的形式记录（旧版 16878 行的 record_name 规则）。</summary>
    private static string NormalizePackageName(string? packageName)
    {
        var name = (packageName ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;

        return PackageTypes.IsArchiveExtension(Path.GetExtension(name))
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }

    public static string NewId()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"instance_{stamp}_{suffix}";
    }

    /// <summary>重建字典：忽略大小写、丢掉空值（旧数据里非列表的值一律当作没有）。</summary>
    private static Dictionary<string, List<string>> NormalizePackages(Dictionary<string, List<string>>? source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;

        foreach (var (key, values) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || values is null) continue;

            result[key] = [.. values];
        }

        return result;
    }

    /// <summary>版本列表重建：丢掉没有可执行文件的项（旧数据里可能留着空行）。</summary>
    private static List<InstanceVersion> NormalizeVersions(List<InstanceVersion>? source)
        => source is null
            ? []
            : [.. source.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Executable))];
}

/// <summary>
/// 实例里的一个「版本」：一个可执行文件 + 用户给它起的名字。
/// 同一个游戏目录里有两个可执行文件（两个版本）时靠它区分，启动游戏时按名字选。
/// </summary>
public sealed class InstanceVersion
{
    /// <summary>用户给这个版本起的名字（如「3.3.6」）；留空时界面退回显示可执行文件名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 可执行文件。位于游戏目录内时存相对文件名（换目录 / 导入后仍可用），否则存绝对路径——
    /// 与 <see cref="GameInstance.Executable"/> 同一套规则。
    /// </summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>界面上显示的名字：用户起的名字，没起就用可执行文件名。</summary>
    [JsonIgnore]
    public string DisplayName
        => string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(Executable) : Name.Trim();
}
