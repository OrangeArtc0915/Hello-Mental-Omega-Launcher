using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.Core.Layout;

/// <summary>
/// 布局方案的读写与增删改。方案文件放在 <c>Paths.Layouts\&lt;id&gt;.json</c>，一套方案一个文件：
/// 单个文件坏掉只跳过它自己，不影响其它方案与程序启动（与实例列表同一套做法）。
/// 当前启用的是哪一套记在 <see cref="Settings.ActiveLayoutSchemeId"/>，为空表示默认布局。
/// </summary>
public static class LayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 方案名与自定义名用户可能手工改过，键名大小写不敏感
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 不转义中文，保证用户手工编辑方案文件时看得懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly List<LayoutScheme> Schemes = [];

    public static IReadOnlyList<LayoutScheme> All => Schemes;

    /// <summary>当前启用的方案。设置里的 Id 为空、或指向已删除的方案时，回退到内置默认方案。</summary>
    public static LayoutScheme Active { get; private set; } = LayoutScheme.CreateDefault();

    public static bool IsLoaded { get; private set; }

    /// <summary>方案列表或启用方案变化时触发。</summary>
    public static event Action? Changed;

    /// <summary>首次使用时读一次盘。界面各处都调它，重复调用不会重复读。</summary>
    public static void EnsureLoaded(IReadOnlyList<string> knownElementIds)
    {
        if (!IsLoaded) Load(knownElementIds);
    }

    /// <summary>
    /// 读取全部方案。<paramref name="knownElementIds"/> 不为 null 时，顺手把每个方案的元素清单
    /// 对齐到现有元素（未知标识安全忽略）。
    /// </summary>
    public static void Load(IReadOnlyList<string>? knownElementIds = null)
    {
        Schemes.Clear();

        if (!string.IsNullOrWhiteSpace(Paths.Layouts))
        {
            try
            {
                Directory.CreateDirectory(Paths.Layouts);

                foreach (var file in Directory.EnumerateFiles(Paths.Layouts, "*.json"))
                {
                    var scheme = ReadFrom(file, out var error);

                    if (scheme is null)
                    {
                        Log.Warn($"布局方案解析失败，已跳过：{Path.GetFileName(file)}（{error}）");
                        continue;
                    }

                    Schemes.Add(scheme);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取布局方案失败", ex);
            }
        }

        // 内置默认方案始终可用，但它只在被改过之后才落盘（见 Save）
        if (Schemes.All(scheme => !scheme.IsBuiltIn)) Schemes.Insert(0, LayoutScheme.CreateDefault());

        if (knownElementIds is not null)
        {
            foreach (var scheme in Schemes)
            {
                var name = scheme.Name;
                scheme.Normalize(knownElementIds, dropped => Log.Warn($"布局方案「{name}」引用了不存在的元素「{dropped}」，已忽略"));
            }
        }

        SortSchemes();
        Active = ResolveActive();

        IsLoaded = true;
        Changed?.Invoke();
        Log.Info($"已载入 {Schemes.Count} 套布局方案，当前启用：{Active.Name}");
    }

    /// <summary>从指定文件读一个方案。</summary>
    public static LayoutScheme? ReadFrom(string filePath, out string? error)
    {
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "文件不存在";
                return null;
            }

            var scheme = JsonSerializer.Deserialize<LayoutScheme>(File.ReadAllText(filePath), Options);
            if (scheme is null)
            {
                error = "内容为空";
                return null;
            }

            // 手工改过的文件可能缺 Id / Name，用文件名兜底
            if (string.IsNullOrWhiteSpace(scheme.Id)) scheme.Id = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(scheme.Name)) scheme.Name = scheme.Id;
            if (scheme.Items is null) scheme.Items = [];

            // 越界或半套坐标在读取时就收拾干净，免得界面拿到脏数据（旧文件没有坐标，这里是空转）
            foreach (var item in scheme.Items) item.ClampBounds();

            return scheme;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static LayoutScheme? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : Schemes.FirstOrDefault(scheme => LayoutScheme.Same(scheme.Id, id));

    /// <summary>切换启用方案并落盘到设置。传 null / 空串表示回到内置默认布局。</summary>
    public static void SetActive(string? id)
    {
        Active = Find(id) ?? Schemes.First(scheme => scheme.IsBuiltIn);

        // 默认布局用设置里的空值表达，不写多余的文件名
        SettingsStore.Current.ActiveLayoutSchemeId = Active.IsBuiltIn ? null : Active.Id;
        SettingsStore.Save();

        Changed?.Invoke();
    }

    /// <summary>写入一个方案（已存在则覆盖），顺带刷新内存列表。</summary>
    public static bool Save(LayoutScheme scheme)
    {
        scheme.UpdatedAt = DateTime.Now;

        if (!Schemes.Contains(scheme))
        {
            Schemes.Add(scheme);
            SortSchemes();
        }

        var ok = WriteToFile(scheme);
        if (ok) Changed?.Invoke();

        return ok;
    }

    /// <summary>
    /// 新建方案。<paramref name="copyFrom"/> 不为 null 时复制它的元素清单，否则用默认顺序。
    /// 名称非法或重名时返回 null。
    /// </summary>
    public static LayoutScheme? Create(string name, IReadOnlyList<string> knownElementIds, LayoutScheme? copyFrom = null)
    {
        var trimmed = name.Trim();
        if (ValidateName(trimmed, null) is not null) return null;

        var scheme = new LayoutScheme
        {
            Id = MakeId(trimmed),
            Name = trimmed,
            Description = copyFrom is null ? "自定义布局" : $"复制自 {copyFrom.Name}",
        };

        if (copyFrom is not null) scheme.Items = copyFrom.Items.Select(item => item.Clone()).ToList();

        scheme.Normalize(knownElementIds);

        return Save(scheme) ? scheme : null;
    }

    /// <summary>重命名方案。默认方案不可改名；文件跟着一起改名。</summary>
    public static bool Rename(LayoutScheme scheme, string newName)
    {
        if (scheme.IsBuiltIn) return false;

        var trimmed = newName.Trim();
        if (LayoutScheme.Same(scheme.Name, trimmed)) return true;
        if (ValidateName(trimmed, scheme.Id) is not null) return false;

        var oldId = scheme.Id;
        var newId = MakeId(trimmed);

        scheme.Id = newId;
        scheme.Name = trimmed;
        scheme.UpdatedAt = DateTime.Now;

        if (!WriteToFile(scheme))
        {
            scheme.Id = oldId;
            return false;
        }

        TryDeleteFile(SchemeFile(oldId));

        // 改的正是当前启用方案时，设置里记的 Id 跟着改
        if (LayoutScheme.Same(SettingsStore.Current.ActiveLayoutSchemeId, oldId))
        {
            SettingsStore.Current.ActiveLayoutSchemeId = newId;
            SettingsStore.Save();
        }

        SortSchemes();
        Changed?.Invoke();
        return true;
    }

    /// <summary>删除方案。默认方案不可删除；删的是启用方案时切回默认布局。</summary>
    public static bool Delete(LayoutScheme scheme)
    {
        if (scheme.IsBuiltIn) return false;

        var wasActive = LayoutScheme.Same(SettingsStore.Current.ActiveLayoutSchemeId, scheme.Id);
        if (!Schemes.Remove(scheme)) return false;

        TryDeleteFile(SchemeFile(scheme.Id));

        if (wasActive || ReferenceEquals(Active, scheme))
        {
            Active = Schemes.First(item => item.IsBuiltIn);
            SettingsStore.Current.ActiveLayoutSchemeId = null;
            SettingsStore.Save();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>恢复默认布局：清空默认方案里的自定义内容，并把启用方案切回默认。</summary>
    public static void RestoreDefault(IReadOnlyList<string> knownElementIds)
    {
        var scheme = Schemes.First(item => item.IsBuiltIn);

        scheme.Items = [];
        scheme.Normalize(knownElementIds);
        Save(scheme);

        SetActive(null);
    }

    /// <summary>校验方案名，返回错误说明；通过时返回 null。<paramref name="exceptId"/> 用于重命名时排除自己。</summary>
    public static string? ValidateName(string? name, string? exceptId)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0) return "方案名不能为空。";
        if (trimmed.Length > 24) return "方案名不要超过 24 个字。";

        if (string.Equals(trimmed, LayoutScheme.DefaultName, StringComparison.OrdinalIgnoreCase))
            return $"「{LayoutScheme.DefaultName}」是内置方案名，请换一个。";

        var duplicated = Schemes.Any(scheme =>
            !LayoutScheme.Same(scheme.Id, exceptId) &&
            string.Equals(scheme.Name, trimmed, StringComparison.OrdinalIgnoreCase));

        return duplicated ? "已有同名方案，请换一个。" : null;
    }

    private static LayoutScheme ResolveActive()
    {
        var id = SettingsStore.Current.ActiveLayoutSchemeId;

        if (!string.IsNullOrWhiteSpace(id))
        {
            var found = Find(id);
            if (found is not null) return found;

            Log.Warn($"设置里启用的布局方案「{id}」不存在，已回退到默认布局");
        }

        return Schemes.First(scheme => scheme.IsBuiltIn);
    }

    private static void SortSchemes() => Schemes.Sort((left, right) =>
    {
        if (left.IsBuiltIn != right.IsBuiltIn) return left.IsBuiltIn ? -1 : 1;
        return string.Compare(left.Name, right.Name, StringComparison.CurrentCulture);
    });

    private static string SchemeFile(string id) => Path.Combine(Paths.Layouts, id + ".json");

    private static bool WriteToFile(LayoutScheme scheme)
    {
        try
        {
            Directory.CreateDirectory(Paths.Layouts);
            File.WriteAllText(SchemeFile(scheme.Id), JsonSerializer.Serialize(scheme, Options));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"布局方案保存失败：{scheme.Name}", ex);
            return false;
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"布局方案文件删除失败：{file}（{ex.Message}）");
        }
    }

    /// <summary>由方案名推出文件名（去掉非法字符），重名时补序号。</summary>
    private static string MakeId(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var ch in name) builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var baseId = builder.ToString().Trim().Trim('.');
        if (baseId.Length == 0) baseId = "scheme";
        if (LayoutScheme.Same(baseId, LayoutScheme.DefaultId)) baseId = "scheme";

        var id = baseId;
        var index = 2;
        while (Schemes.Any(scheme => LayoutScheme.Same(scheme.Id, id))) id = $"{baseId} ({index++})";

        return id;
    }
}
