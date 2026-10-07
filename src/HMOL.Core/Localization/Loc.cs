using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using HMOL.Core.App;

namespace HMOL.Core.Localization;

/// <summary>可选语言：代码（BCP-47，如 <c>zh-CN</c>）+ 显示名。</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// 界面文本查表与当前语言管理。
///
/// <para>
/// msgid 就是源码里的中文原文（gettext 风格），查不到时原样返回——缺翻译只会退回中文，不会出现空串或 key。
/// </para>
/// <para>
/// 语言包来源：内嵌资源 <c>HMOL.Core.Languages.&lt;code&gt;.json</c> 为主；
/// 数据目录 <c>Data\lang\&lt;code&gt;.json</c>（见 <see cref="Paths.LanguageDirectory"/>）可覆盖同名的内嵌语言，
/// 也可以直接新增语言——丢一个文件进去重启即可用，不用重新打包。
/// </para>
/// </summary>
public static class Loc
{
    /// <summary>基准语言：源码里的原文就是简体中文，不需要语言包。</summary>
    public const string DefaultLanguage = "zh-CN";

    private const string ResourcePrefix = "HMOL.Core.Languages.";
    private const string ResourceSuffix = ".json";

    /// <summary>基准语言的显示名（写死，它没有语言包）。</summary>
    private const string DefaultDisplayName = "简体中文";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Catalog> Catalogs = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> _map =
        new(StringComparer.Ordinal);

    private static List<LanguageOption> _available = [new LanguageOption(DefaultLanguage, DefaultDisplayName)];

    /// <summary>当前语言代码（永远是 <see cref="Available"/> 里存在的一项）。</summary>
    public static string Current { get; private set; } = DefaultLanguage;

    /// <summary>语言已切换（界面文案的刷新时机：XAML 绑定的代理、页面缓存重建、托盘菜单重建）。</summary>
    public static event Action? Changed;

    /// <summary>可用语言列表；基准语言恒排第一。</summary>
    public static IReadOnlyList<LanguageOption> Available
    {
        get { lock (Gate) return _available; }
    }

    /// <summary>
    /// 载入语言包并切到指定语言。<paramref name="language"/> 为空或语言包不存在时回退简体中文。
    /// 必须在使用任何界面文案之前调用（见 <c>App.OnStartup</c>）。
    /// </summary>
    public static void Init(string? language) => Init(language, autoDetect: false);

    /// <summary>
    /// 载入语言包并选一个语言。<paramref name="autoDetect"/> 为 true 时先按系统时区与区域判定
    /// （见 <see cref="DetectSystemLanguage"/>），判不出来再用 <paramref name="language"/>。
    /// </summary>
    public static void Init(string? language, bool autoDetect)
    {
        lock (Gate)
        {
            Catalogs.Clear();
            Catalogs[DefaultLanguage] = new Catalog(
                DefaultLanguage, DefaultDisplayName, new Dictionary<string, string>(StringComparer.Ordinal));

            LoadEmbedded();
            LoadExternal();

            _available = Catalogs.Values
                .OrderBy(c => c.Code.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .Select(c => new LanguageOption(c.Code, c.DisplayName))
                .ToList();
        }

        SetLanguage(autoDetect ? DetectSystemLanguage() ?? language : language);
    }

    /// <summary>
    /// 按系统时区与区域猜一个界面语言；判不出来返回 null，调用方回退到 <see cref="DefaultLanguage"/>。
    ///
    /// <para>
    /// 中文时区（大陆 / 港澳台 / 新加坡 / 乌兰巴托）一律判中文：用英文系统但人在国内的并不少，
    /// 光看系统区域名会把界面判成英文，而这正是「按系统时区自动设置语言」要照顾的场景。
    /// 其余时区看系统区域语言名，能对上已装语言包就用它——语言包只有中英两套，别的语言判不出来就走回退。
    /// </para>
    /// </summary>
    public static string? DetectSystemLanguage()
    {
        if (ChineseTimeZones.Contains(TimeZoneInfo.Local.Id)) return MatchCode(DefaultLanguage);

        return MatchCode(CultureInfo.CurrentUICulture.Name)
               ?? MatchCode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
               ?? MatchCode(CultureInfo.InstalledUICulture.Name)
               ?? MatchCode(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName);
    }

    /// <summary>Windows 时区标识里属于中文地区的那几个（不按 UTC 偏移判定，+8 还包含澳洲西部）。</summary>
    private static readonly HashSet<string> ChineseTimeZones = new(StringComparer.OrdinalIgnoreCase)
    {
        "China Standard Time",
        "Taipei Standard Time",
        "Singapore Standard Time",
        "Hong Kong Standard Time",
        "Ulaanbaatar Standard Time",
    };

    /// <summary>把语言代码（<c>en</c> / <c>en-US</c>）对到已装语言包，对不上返回 null。</summary>
    private static string? MatchCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        lock (Gate)
        {
            if (Catalogs.TryGetValue(code, out var exact)) return exact.Code;

            // 「en」这类语言名对上「en-US」这样的具体语言包
            var prefix = code + "-";

            foreach (var catalog in Catalogs.Values)
            {
                if (catalog.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return catalog.Code;
            }
        }

        return null;
    }

    /// <summary>切换语言（代码不存在时回退简体中文）；切换后立即抛 <see cref="Changed"/>。</summary>
    public static void SetLanguage(string? code)
    {
        Catalog catalog;

        lock (Gate)
        {
            var requested = string.IsNullOrWhiteSpace(code) ? DefaultLanguage : code.Trim();

            if (!Catalogs.TryGetValue(requested, out catalog!))
                catalog = Catalogs[DefaultLanguage];

            var switched = !Current.Equals(catalog.Code, StringComparison.OrdinalIgnoreCase);
            _map = catalog.Strings;
            Current = catalog.Code;

            if (!switched) return;
        }

        ApplyCulture(Current);
        Changed?.Invoke();
    }

    /// <summary>查表取译文；未命中（或语言包没这一条）时返回原文。</summary>
    public static string T(string msgId)
    {
        if (string.IsNullOrEmpty(msgId)) return msgId;

        var map = _map;
        return map.Count > 0 && map.TryGetValue(msgId, out var text) ? text : msgId;
    }

    /// <summary>先查表，再做 <see cref="string.Format(string,object[])"/>。译文里的占位符写坏时退回用原文格式化。</summary>
    public static string F(string template, params object?[] args)
    {
        var text = T(template);

        try
        {
            return string.Format(text, args);
        }
        catch (FormatException)
        {
            try { return string.Format(template, args); }
            catch (FormatException) { return template; }
        }
    }

    // ————— 语言包载入 —————
    // 结构：{ "_code": "en-US", "_name": "English", "strings": { "启动游戏": "Start Game" } }

    private static void LoadEmbedded()
    {
        var assembly = typeof(Loc).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) continue;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                Merge(name[ResourcePrefix.Length..^ResourceSuffix.Length], reader.ReadToEnd());
            }
            catch
            {
                // 语言包坏了不能拦住启动：跳过它，界面退回中文原文
            }
        }
    }

    private static void LoadExternal()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Paths.LanguageDirectory, "*.json"))
            {
                try { Merge(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)); }
                catch { /* 单个文件坏了只跳过它 */ }
            }
        }
        catch { /* 目录不存在或不可读：只用内嵌语言包 */ }
    }

    /// <summary>把一份语言包并入目录；同语言的重复载入后者整份覆盖（外置压内嵌）。</summary>
    private static void Merge(string code, string json)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(json)) return;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        var name = root.TryGetProperty("_name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root.TryGetProperty("strings", out var mapElement) && mapElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in mapElement.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.String) continue;

                var value = item.Value.GetString();
                if (!string.IsNullOrEmpty(value)) strings[item.Name] = value!;
            }
        }

        Catalogs[code] = new Catalog(
            code,
            string.IsNullOrWhiteSpace(name) ? code : name!.Trim(),
            strings);
    }

    /// <summary>把线程文化也切过去：<c>string.Format</c> 里的日期 / 数字格式跟着语言走。</summary>
    private static void ApplyCulture(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (ArgumentException)
        {
            // 语言包自带的文化代码系统不认识：只影响格式化，界面文本仍按语言包显示
        }
    }

    private sealed record Catalog(string Code, string DisplayName, Dictionary<string, string> Strings);
}