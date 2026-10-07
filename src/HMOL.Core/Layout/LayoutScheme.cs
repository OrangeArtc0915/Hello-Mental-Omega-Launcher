using System.Text.Json.Serialization;
using HMOL.Core.Localization;

namespace HMOL.Core.Layout;

/// <summary>
/// 布局方案里的一个元素：标识 + 显隐 + 自定义显示名 + 可选的主页自由定位坐标。
/// 清单顺序就是侧栏顺序；隐藏的元素保留自己的位置，只是不渲染（与旧版 setVisible(False) 的做法一致）。
/// 侧栏是固定布局，只用到顺序 / 显隐 / 名称；只有主页控件才会用到坐标。
/// </summary>
public sealed class LayoutItem
{
    /// <summary>自由定位尺寸的下限（占父容器的百分比）：再小就点不着了。</summary>
    public const double MinSizePercent = 5;

    public string ElementId { get; set; } = string.Empty;

    public bool Visible { get; set; } = true;

    /// <summary>用户自定义的显示名；为空表示沿用元素自带的名称。</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 自由定位的位置与尺寸，都是父容器宽高的百分比（0-100）。
    /// 与旧版 <c>ElementLayout</c> 的 x/y/w/h 同义，只是把 0-1 的小数换成 0-100 的百分数，读文件时更直观。
    /// 四个字段全为 null 表示「沿用流式布局」：旧方案文件没有这些字段，读出来正好是这种情况，因此向后兼容。
    /// </summary>
    public double? XPercent { get; set; }

    /// <inheritdoc cref="XPercent" />
    public double? YPercent { get; set; }

    /// <inheritdoc cref="XPercent" />
    public double? WidthPercent { get; set; }

    /// <inheritdoc cref="XPercent" />
    public double? HeightPercent { get; set; }

    /// <summary>四个坐标齐全才算指定了自由定位；只写一半的一律按流式布局处理。</summary>
    [JsonIgnore]
    public bool HasBounds => IsNumber(XPercent) && IsNumber(YPercent) && IsNumber(WidthPercent) && IsNumber(HeightPercent);

    public LayoutItem Clone() => new()
    {
        ElementId = ElementId,
        Visible = Visible,
        DisplayName = DisplayName,
        XPercent = XPercent,
        YPercent = YPercent,
        WidthPercent = WidthPercent,
        HeightPercent = HeightPercent,
    };

    /// <summary>清掉坐标，回到流式布局。</summary>
    public void ClearBounds()
    {
        XPercent = null;
        YPercent = null;
        WidthPercent = null;
        HeightPercent = null;
    }

    /// <summary>
    /// 把坐标收拾到合法范围：位置与尺寸都落在 0-100，尺寸不小于 <see cref="MinSizePercent"/>，
    /// 并让元素整体留在容器内（左 + 宽 ≤ 100、上 + 高 ≤ 100），这样窗口再小也不会溢出到别的区域。
    /// 坐标不完整、或不是有限数（手工改坏的文件）时一律当没给，退回流式布局，不抛异常。
    /// </summary>
    public void ClampBounds()
    {
        if (!HasBounds)
        {
            ClearBounds();
            return;
        }

        var width = Math.Clamp(WidthPercent!.Value, MinSizePercent, 100);
        var height = Math.Clamp(HeightPercent!.Value, MinSizePercent, 100);

        XPercent = Math.Clamp(XPercent!.Value, 0, 100 - width);
        YPercent = Math.Clamp(YPercent!.Value, 0, 100 - height);
        WidthPercent = width;
        HeightPercent = height;
    }

    private static bool IsNumber(double? value)
        => value is { } number && !double.IsNaN(number) && !double.IsInfinity(number);
}

/// <summary>
/// 一套界面布局方案。一套方案一个文件（<c>Paths.Layouts\&lt;id&gt;.json</c>）。
/// 描述「哪些元素、什么顺序、显示还是隐藏、叫什么名」，以及主页控件的自由定位坐标（可选）：
/// 侧栏是固定布局，只有主页控件才可能带坐标；没带坐标的元素照常走流式布局。
/// </summary>
public sealed class LayoutScheme
{
    /// <summary>内置默认方案的 Id 与名称。默认方案不可删除、不可重命名。</summary>
    public const string DefaultId = "default";

    public const string DefaultName = "默认布局";

    /// <summary>
    /// 当前方案格式版本。
    /// 1 或缺失 = 自由定位坐标以「主页舞台区」为基准；2 = 以整页内容区为基准。
    /// 旧方案在首次应用到主页时按实测高度换算一次并升级到 2（见 PageHome.MigrateScheme）。
    /// </summary>
    public const int CurrentVersion = 2;

    /// <summary>方案格式版本。旧文件里没有这个字段，读出来就是 1。</summary>
    public int Version { get; set; } = 1;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<LayoutItem> Items { get; set; } = [];

    /// <summary>是否内置的默认方案。不写进文件，按 Id 判断。</summary>
    [JsonIgnore]
    public bool IsBuiltIn => Same(Id, DefaultId);

    public static LayoutScheme CreateDefault() => new()
    {
        Id = DefaultId,
        Name = DefaultName,
        Description = Loc.T("系统默认布局"),
        Version = CurrentVersion,
    };

    public LayoutItem? Find(string? elementId) => string.IsNullOrWhiteSpace(elementId)
        ? null
        : Items.FirstOrDefault(item => Same(item.ElementId, elementId));

    /// <summary>
    /// 把元素清单对齐到当前实际存在的元素：丢掉已不存在的（旧方案文件里可能留着已删元素），
    /// 补上清单里新增的（默认显示）。新增项尽量插在「清单里前一个元素」之后，保持与清单一致的
    /// 相对顺序——否则版本升级新增的导航项会一律掉到末尾。对未知元素一律安全忽略，不抛异常。
    /// 顺手把每条记录的坐标裁到合法范围（见 <see cref="LayoutItem.ClampBounds"/>）。
    /// </summary>
    public void Normalize(IReadOnlyList<string> knownElementIds, Action<string>? onDropped = null)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in knownElementIds) canonical[id] = id;

        var kept = new List<LayoutItem>(Items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items)
        {
            if (!canonical.TryGetValue(item.ElementId, out var id))
            {
                onDropped?.Invoke(item.ElementId);
                continue;
            }

            if (!seen.Add(id)) continue;

            // 统一成元素清单里的大小写，后面按标识查找才不会漏
            item.ElementId = id;
            item.ClampBounds();
            kept.Add(item);
        }

        for (var index = 0; index < knownElementIds.Count; index++)
        {
            var id = knownElementIds[index];
            if (!seen.Add(id)) continue;

            // 往前找清单里最近的、已经存在的元素，插到它后面
            var insertAt = kept.Count;
            for (var back = index - 1; back >= 0; back--)
            {
                var position = kept.FindIndex(item => Same(item.ElementId, knownElementIds[back]));
                if (position < 0) continue;

                insertAt = position + 1;
                break;
            }

            kept.Insert(insertAt, new LayoutItem { ElementId = id });
        }

        Items = kept;
    }

    public static bool Same(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
