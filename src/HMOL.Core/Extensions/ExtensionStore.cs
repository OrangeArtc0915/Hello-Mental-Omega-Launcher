using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using HMOL.Core.App;
using HMOL.Core.IO;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.Core.Extensions;

/// <summary>
/// 扩展目录（<c>Data\extensions</c>）的读写入口，也是扩展机制的<b>唯一数据来源</b>。
///
/// 设计边界（安全上最关键的一条）：
/// <list type="bullet">
/// <item>一个 <c>.json</c> 文件 = 一个扩展。文件里只有声明（文字 / 图标名 / 网址），宿主逐字段读出来渲染；</item>
/// <item><b>只读扫描</b>：扫描时不会创建目录、不会写任何文件、不碰注册表、不加载任何程序集；</item>
/// <item>写操作（新建 / 编辑 / 启停 / 删除）只发生在用户主动操作时，且落点一律经
/// <see cref="PathGuard"/> 限定在 <see cref="RootDirectory"/> 之内，防目录穿越；</item>
/// <item>任何清单问题都只让<b>那一个</b>扩展被跳过并给出中文原因，不抛异常、不影响启动。</item>
/// </list>
/// </summary>
public static class ExtensionStore
{
    /// <summary>扩展目录名（<c>Data\extensions</c>）。</summary>
    public const string FolderName = "extensions";

    /// <summary>清单文件扩展名。</summary>
    public const string FileExtension = ".json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // 不转义中文，用户手工打开清单也能看懂
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static volatile IReadOnlyList<ExtensionInfo> _all = [];

    /// <summary>扩展目录绝对路径。注意这里只拼路径，<b>不</b>创建目录。</summary>
    public static string RootDirectory
    {
        get
        {
            Paths.Init();
            return Path.Combine(Paths.Data, FolderName);
        }
    }

    /// <summary>全部扩展（含停用与加载失败的），按扩展 ID 排序。</summary>
    public static IReadOnlyList<ExtensionInfo> All => _all;

    /// <summary>要渲染到主页的扩展：清单有效且启用着，按扩展 ID 排序。</summary>
    public static IReadOnlyList<ExtensionInfo> Widgets =>
        _all.Where(info => info.Status == ExtensionStatus.Enabled).ToList();

    /// <summary>按扩展 ID 取一份快照；找不到返回 null。</summary>
    public static ExtensionInfo? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _all.FirstOrDefault(info => string.Equals(info.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    // ————— 扫描（只读）—————

    /// <summary>
    /// 重新扫描扩展目录，返回新的快照。
    /// 目录不存在时立刻返回空列表：<b>不创建目录</b>，没有扩展就什么都不做。
    /// 单个文件出问题只影响它自己，会在快照里以「加载失败 + 中文原因」出现。
    /// </summary>
    public static IReadOnlyList<ExtensionInfo> Reload()
    {
        var root = RootDirectory;
        var list = new List<ExtensionInfo>();

        try
        {
            if (!Directory.Exists(root))
            {
                _all = list;
                Log.Info(Loc.F("扩展目录还不存在（{0}），本次没有扩展", root));
                return list;
            }

            foreach (var file in Directory.GetFiles(root, "*" + FileExtension, SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(file);

                // 以「.」开头的是宿主的隐藏文件（历史遗留的元数据文件也在此列），不是扩展
                if (id.StartsWith('.') || ExtensionValidator.ValidateId(id) is not null) continue;

                list.Add(Load(id, file));
            }
        }
        catch (Exception ex)
        {
            // 目录枚举失败也不能把启动拖下水
            Log.Info(Loc.F("扫描扩展目录失败（{0}）：{1}", root, Describe(ex)));
            _all = list;
            return list;
        }

        _all = list;

        Log.Info(Loc.F("扩展：共发现 {0} 个（启用 {1}，", list.Count, list.Count(i => i.Status == ExtensionStatus.Enabled)) +
                 Loc.F("停用 {0}，", list.Count(i => i.Status == ExtensionStatus.Disabled)) +
                 Loc.F("加载失败 {0}）", list.Count(i => i.Status == ExtensionStatus.Failed)));

        // 具体原因走 DEBUG：设置页里已经用红字写明了，日志不必每次扫描都重复一遍
        foreach (var failed in list.Where(i => i.Status == ExtensionStatus.Failed))
            Log.Debug(Loc.F("扩展「{0}」已跳过：{1}", failed.Id, failed.Error));

        return list;
    }

    /// <summary>读一个清单文件并校验，产出快照。<b>纯读取</b>，绝不因为扩展而写盘。</summary>
    private static ExtensionInfo Load(string id, string file)
    {
        var manifest = TryParse(file, out var parseError);

        if (manifest is null)
        {
            return new ExtensionInfo
            {
                Id = id,
                FilePath = file,
                Status = ExtensionStatus.Failed,
                Error = parseError,
                DisplayName = id
            };
        }

        var data = manifest.Normalize();
        var error = ExtensionValidator.Validate(data);

        // 清单坏掉时仍然尽量把用户填的名字显示出来，方便在设置页里认出是哪一个
        var display = data.Title!.Length > 0 ? data.Title : id;

        if (error is not null)
        {
            return new ExtensionInfo
            {
                Id = id,
                FilePath = file,
                Status = ExtensionStatus.Failed,
                Error = error,
                DisplayName = display,
                Version = data.Version!,
                Author = data.Author!,
                Description = data.Description!
            };
        }

        return new ExtensionInfo
        {
            Id = id,
            FilePath = file,
            Status = data.Enabled ? ExtensionStatus.Enabled : ExtensionStatus.Disabled,
            DisplayName = display,
            Version = data.Version!,
            Author = data.Author!,
            Description = data.Description!,
            Manifest = data
        };
    }

    /// <summary>读并解析清单文件；失败返回 null 并把可读中文原因写进 <paramref name="error"/>。</summary>
    private static ExtensionManifest? TryParse(string file, out string? error)
    {
        error = null;

        string json;

        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            error = Loc.F("文件读不出来（{0}）", Describe(ex));
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, ReadOptions);

            if (manifest is null)
            {
                error = Loc.T("文件内容是空的");
                return null;
            }

            return manifest;
        }
        catch (JsonException ex)
        {
            // 带上行列位置，用户能直接定位到写错的那一处
            var where = ex.LineNumber is { } line
                ? Loc.F("（第 {0} 行第 {1} 列）", line + 1, (ex.BytePositionInLine ?? 0) + 1)
                : string.Empty;

            error = Loc.F("不是有效的清单 JSON{0}：{1}", where, DescribeJson(ex));
            return null;
        }
        catch (Exception ex)
        {
            error = Loc.F("解析清单失败：{0}", Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// 把 System.Text.Json 的英文原因整理成一句更好读的话：去掉它追加的「Path / LineNumber」尾巴
    /// （位置我们已经在外面报了），并把最常见的「类型不对」换成人话。
    /// </summary>
    private static string DescribeJson(JsonException ex)
    {
        var message = ex.Message;

        var tail = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (tail > 0) message = message[..tail].TrimEnd('.', ' ');

        return message.Contains("could not be converted", StringComparison.Ordinal)
            ? Loc.T("字段类型不对：这一处应该是文本（数字、true/false、数组要按字段表写成对应的写法）")
            : message;
    }

    // ————— 单个扩展的读取（供设置页的表单）—————

    /// <summary>读一个扩展的清单，供编辑表单回填。<paramref name="id"/> 非法或文件不存在时返回 false。</summary>
    public static bool TryRead(string? id, out ExtensionManifest manifest, out string error)
    {
        manifest = new ExtensionManifest();

        if (!TryResolve(id, out var file, out error)) return false;

        if (!File.Exists(file))
        {
            error = Loc.T("这个扩展的清单文件已经不在了（可能被手工删除），点「重新加载」刷新列表。");
            return false;
        }

        var parsed = TryParse(file, out var parseError);

        if (parsed is null)
        {
            error = parseError ?? Loc.T("清单内容无法解析");
            return false;
        }

        manifest = parsed.Normalize();
        return true;
    }

    // ————— 写操作（用户主动触发，落点限定在扩展目录内）—————

    /// <summary>
    /// 新建 / 覆盖一个扩展的清单。会先用同一套规则校验 ID 与清单内容，
    /// 任何一项不过就直接返回可读中文原因，<b>不落盘</b>。
    /// </summary>
    public static bool TrySave(string? id, ExtensionManifest manifest, out string error, bool reload = true)
    {
        if (!TryResolve(id, out var file, out error)) return false;

        var errorOfManifest = ExtensionValidator.Validate(manifest);

        if (errorOfManifest is not null)
        {
            error = errorOfManifest;
            return false;
        }

        try
        {
            // 只在用户真要保存时才建目录（扫描阶段绝不创建）
            Directory.CreateDirectory(RootDirectory);

            File.WriteAllText(file, JsonSerializer.Serialize(manifest.Normalize(), WriteOptions));
        }
        catch (Exception ex)
        {
            error = Loc.F("保存失败：{0}", Describe(ex));
            Log.Info(Loc.F("保存扩展「{0}」失败：{1}", id, ex.Message));
            return false;
        }

        Log.Info(Loc.F("已保存扩展清单：{0}", file));

        if (reload) Reload();
        return true;
    }

    /// <summary>启用 / 停用：改清单里的 <c>enabled</c> 字段并落盘（仍然只写这一个清单文件）。</summary>
    public static bool TrySetEnabled(string? id, bool enabled, out string error)
    {
        if (!TryRead(id, out var manifest, out error)) return false;

        manifest.Enabled = enabled;

        if (!TrySave(id, manifest, out error)) return false;

        Log.Info(Loc.F("扩展「{0}」已{1}", id, (enabled ? Loc.T("启用") : Loc.T("停用"))));
        return true;
    }

    /// <summary>删除一个扩展的清单文件。<paramref name="id"/> 会被限定在扩展目录内。</summary>
    public static bool TryDelete(string? id, out string error)
    {
        if (!TryResolve(id, out var file, out error)) return false;

        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            error = Loc.F("删除失败：{0}", Describe(ex));
            Log.Info(Loc.F("删除扩展「{0}」失败：{1}", id, ex.Message));
            return false;
        }

        Log.Info(Loc.F("已删除扩展清单：{0}", file));

        Reload();
        return true;
    }

    /// <summary>把扩展 ID 解析成扩展目录内的绝对路径，防目录穿越。</summary>
    private static bool TryResolve(string? id, out string file, out string error)
    {
        file = string.Empty;

        // 允许调用方传「名称」或「名称.json」，统一按名称处理
        var name = (id ?? string.Empty).Trim();

        if (name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^FileExtension.Length];

        var idError = ExtensionValidator.ValidateId(name);

        if (idError is not null)
        {
            error = idError;
            return false;
        }

        if (!PathGuard.TryResolve(RootDirectory, name + FileExtension, out file))
        {
            error = Loc.T("扩展名不合法：只能写在扩展目录里，不能包含路径分隔符或上跳片段");
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>打开扩展目录（不存在就先建出来）。只在用户点按钮时调用。</summary>
    public static bool OpenFolder()
    {
        var root = RootDirectory;

        try { Directory.CreateDirectory(root); }
        catch (Exception ex) { Log.Info(Loc.F("创建扩展目录失败（{0}）：{1}", root, ex.Message)); }

        if (!Directory.Exists(root)) return false;

        ShellHelper.OpenFolder(root);
        return true;
    }

    /// <summary>异常 → 一句可读中文原因（消息为空时退回异常类型名）。</summary>
    private static string Describe(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
}
