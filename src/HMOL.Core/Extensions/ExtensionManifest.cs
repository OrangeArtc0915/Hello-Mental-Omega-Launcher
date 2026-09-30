using System.Text.Json.Serialization;

namespace HMOL.Core.Extensions;

/// <summary>
/// 扩展清单里各字段的长度 / 数量 / 取值上限。校验与界面提示都读这里，避免两处各写一份数字。
/// 上限存在的意义：扩展是用户手写的文本，宿主必须保证一个乱写的清单不会把首页排版撑坏。
/// </summary>
public static class ExtensionLimits
{
    /// <summary>扩展文件名（不含 .json）的长度上限。</summary>
    public const int IdMax = 64;

    public const int TitleMax = 40;

    public const int VersionMax = 20;

    public const int AuthorMax = 30;

    public const int DescriptionMax = 120;

    public const int IconMax = 40;

    public const int ValueMax = 60;

    /// <summary>文字行最多几条。</summary>
    public const int LineCountMax = 8;

    public const int LineLabelMax = 12;

    public const int LineTextMax = 200;

    /// <summary>网址（数据源、点击打开）的长度上限。</summary>
    public const int UrlMax = 500;

    /// <summary>数据源取值路径的长度上限。</summary>
    public const int DataPathMax = 200;

    /// <summary>数据源刷新间隔下限（秒）。太短会变成对别人服务器的压力测试。</summary>
    public const int RefreshSecondsMin = 60;

    /// <summary>数据源刷新间隔上限（秒）。</summary>
    public const int RefreshSecondsMax = 86400;

    /// <summary>没写刷新间隔时的默认值（秒）。</summary>
    public const int RefreshSecondsDefault = 600;
}

/// <summary>
/// 一个扩展小组件的清单，对应 <c>Data\extensions\&lt;名称&gt;.json</c>。
///
/// 这是<b>纯声明式</b>配置：里面只有文字、图标名和网址，没有任何可执行内容。
/// 宿主（启动器）负责按这些声明把卡片画出来，扩展自己不会被执行、也不会被加载成代码。
/// 字段全部可选，缺什么就少显示什么；<see cref="ExtensionValidator"/> 负责把关。
/// </summary>
public sealed class ExtensionManifest
{
    /// <summary>卡片标题（必填，显示在卡片左上）。</summary>
    public string? Title { get; set; }

    /// <summary>版本号，自由文本，只用于展示。</summary>
    public string? Version { get; set; }

    /// <summary>作者，只用于展示。</summary>
    public string? Author { get; set; }

    /// <summary>一句话说明，显示在设置页的扩展列表里。</summary>
    public string? Description { get; set; }

    /// <summary>
    /// 卡片图标：内置图标包（lucide）里的名字，可写 <c>coffee</c> 或 <c>lucide/coffee</c>。
    /// 留空用默认图标 <see cref="ExtensionIcons.Fallback"/>；写了不存在的名字会被判为清单无效。
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// 主数值（卡片上的大字），例如 <c>23°C</c>。配了数据源时它是「取不到数据」时的降级显示。
    /// </summary>
    public string? Value { get; set; }

    /// <summary>文字行（最多 <see cref="ExtensionLimits.LineCountMax"/> 行）。</summary>
    public List<ExtensionLine>? Lines { get; set; }

    /// <summary>可选的数据源：只读 HTTP GET 一个 JSON，取其中的一个值当主数值。</summary>
    public ExtensionDataSource? DataSource { get; set; }

    /// <summary>可选的点击网址：填了之后整张卡可点，用系统默认浏览器打开（只放行 http / https）。</summary>
    public string? Link { get; set; }

    /// <summary>是否启用。默认启用；停用后不占主页位置，但清单仍留在磁盘上。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>去掉首尾空白、补上下限内的默认值，得到一份可直接展示 / 保存的清单。</summary>
    public ExtensionManifest Normalize()
    {
        var lines = new List<ExtensionLine>();

        foreach (var line in Lines ?? [])
        {
            if (line is null) continue;

            lines.Add(new ExtensionLine
            {
                Label = (line.Label ?? string.Empty).Trim(),
                Text = (line.Text ?? string.Empty).Trim()
            });
        }

        return new ExtensionManifest
        {
            Title = (Title ?? string.Empty).Trim(),
            Version = (Version ?? string.Empty).Trim(),
            Author = (Author ?? string.Empty).Trim(),
            Description = (Description ?? string.Empty).Trim(),
            Icon = ExtensionIcons.Normalize(Icon),
            Value = (Value ?? string.Empty).Trim(),
            Lines = lines,
            DataSource = DataSource is null ? null : new ExtensionDataSource
            {
                Url = (DataSource.Url ?? string.Empty).Trim(),
                Path = (DataSource.Path ?? string.Empty).Trim(),
                RefreshSeconds = DataSource.RefreshSeconds
            },
            Link = (Link ?? string.Empty).Trim(),
            Enabled = Enabled
        };
    }
}

/// <summary>清单里的一行文字。带标签时显示成「标签：内容」。</summary>
public sealed class ExtensionLine
{
    /// <summary>标签（可留空）。</summary>
    public string? Label { get; set; }

    /// <summary>内容。</summary>
    public string? Text { get; set; }

    /// <summary>界面上的一行文本。</summary>
    [JsonIgnore]
    public string Display => string.IsNullOrEmpty(Label) ? (Text ?? string.Empty) : $"{Label}：{Text}";
}

/// <summary>
/// 数据源声明：只读 GET 一个返回 JSON 的网址，按 <see cref="Path"/> 取一个值当卡片主数值。
///
/// 宿主只做三件事：发一次 GET、在内存里缓存结果、把取到的值渲染成文字。
/// 不会跟随重定向到非 http/https、不会写文件、不会带任何凭据。
/// </summary>
public sealed class ExtensionDataSource
{
    /// <summary>数据源网址，必须是 http / https。</summary>
    public string? Url { get; set; }

    /// <summary>
    /// 取值路径，形如 <c>current.temperature_2m</c> 或 <c>items.0.name</c>：
    /// 用「.」逐层进入对象 / 数组，数字段表示数组下标。留空表示取整个根值（要求根是字符串或数字）。
    /// </summary>
    public string? Path { get; set; }

    /// <summary>刷新间隔（秒），同时也是缓存有效期：两次请求之间不会重复打同一个接口。</summary>
    public int RefreshSeconds { get; set; } = ExtensionLimits.RefreshSecondsDefault;
}
