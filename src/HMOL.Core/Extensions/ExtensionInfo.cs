
using HMOL.Core.Localization;
namespace HMOL.Core.Extensions;

/// <summary>扩展的加载状态。</summary>
public enum ExtensionStatus
{
    /// <summary>清单有效且用户启用着，会出现在主页小组件区。</summary>
    Enabled = 0,

    /// <summary>清单有效但被停用（<c>"enabled": false</c>），不出现在主页。</summary>
    Disabled = 1,

    /// <summary>清单读不出来或没通过校验，被跳过；原因见 <see cref="ExtensionInfo.Error"/>。</summary>
    Failed = 2
}

/// <summary>
/// 扫描扩展目录后得到的一份只读快照，供界面展示与渲染。
/// 它只是一份数据结构（纯文本），不持有任何可执行对象。
/// </summary>
public sealed record ExtensionInfo
{
    /// <summary>扩展 ID，等于清单文件名去掉 <c>.json</c>。</summary>
    public required string Id { get; init; }

    /// <summary>清单文件绝对路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>当前状态。</summary>
    public required ExtensionStatus Status { get; init; }

    /// <summary>校验 / 读取失败的可读中文原因；状态不是「加载失败」时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>展示名：清单里的 title；清单坏掉时退回文件名。</summary>
    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>校验通过的清单；失败时为 null（此时不要拿它去渲染）。</summary>
    public ExtensionManifest? Manifest { get; init; }

    /// <summary>是否可用（清单有效）。</summary>
    public bool IsUsable => Manifest is not null;

    /// <summary>是否配了数据源。</summary>
    public bool HasDataSource => Manifest?.DataSource is not null;

    /// <summary>卡片图标；没有清单时给默认图标，免得界面上空一块。</summary>
    public string Icon => string.IsNullOrEmpty(Manifest?.Icon) ? ExtensionIcons.Fallback : Manifest!.Icon!;

    /// <summary>界面上的状态文案。</summary>
    public string StatusText => Status switch
    {
        ExtensionStatus.Enabled => Loc.T("已启用"),
        ExtensionStatus.Disabled => Loc.T("已停用"),
        _ => Loc.T("加载失败")
    };

    /// <summary>列表里的次要信息：作者 / 版本 / 数据源。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();

            if (Author.Length > 0) parts.Add(Loc.F("作者 {0}", Author));
            if (Version.Length > 0) parts.Add($"v{Version.TrimStart('v', 'V')}");
            parts.Add(HasDataSource ? Loc.T("有数据源") : Loc.T("无数据源"));

            return string.Join(" · ", parts);
        }
    }
}
