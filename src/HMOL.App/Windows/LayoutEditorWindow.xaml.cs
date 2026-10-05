using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using HMOL.App.Controls;
using HMOL.App.Layout;
using HMOL.Core.Layout;
using HMOL.Core.Logging;

namespace HMOL.App.Windows;

/// <summary>元素列表里的一行。</summary>
public sealed class LayoutElementRow
{
    public LayoutElementRow(LayoutElementInfo info, LayoutItem item)
    {
        Id = info.Id;
        Icon = info.Icon;
        Name = string.IsNullOrWhiteSpace(item.DisplayName) ? info.DisplayName : item.DisplayName!;
        Hint = string.IsNullOrWhiteSpace(item.DisplayName) ? info.Group : $"{info.Group} · 已改名";
    }

    public string Id { get; }

    public string Icon { get; }

    public string Name { get; }

    /// <summary>行尾的补充说明：元素分组；改过名的额外标注一下。</summary>
    public string Hint { get; }
}

/// <summary>方案列表里的一行。</summary>
public sealed class LayoutSchemeRow
{
    public LayoutSchemeRow(LayoutScheme scheme, bool isActive)
    {
        Id = scheme.Id;
        NameText = isActive ? $"{scheme.Name}（当前启用）" : scheme.Name;
    }

    public string Id { get; }

    public string NameText { get; }
}

/// <summary>主页控件列表里的一行。行尾给出当前的坐标（百分比）或「跟随流式」。</summary>
public sealed class HomeElementRow
{
    public HomeElementRow(LayoutElementInfo info, LayoutItem? item)
    {
        Id = info.Id;
        Icon = info.Icon;
        Name = string.IsNullOrWhiteSpace(item?.DisplayName) ? info.DisplayName : item!.DisplayName!;

        // 隐藏的元素优先标明「已隐藏」，否则看不出它为什么在主页上不出现
        Hint = item is { Visible: false }
            ? "已隐藏"
            : item is { HasBounds: true }
                ? $"自由 {item.XPercent:0.#},{item.YPercent:0.#} · {item.WidthPercent:0.#}×{item.HeightPercent:0.#}"
                : "跟随流式";
    }

    public string Id { get; }

    public string Icon { get; }

    public string Name { get; }

    public string Hint { get; }
}

/// <summary>
/// 自定义界面布局编辑器，分两块：
/// 1）「侧栏导航」——左侧是方案列表，中间是隐藏中的元素，右侧是显示中的元素：拖到右边即显示、
///    拖回左边即隐藏、在右侧上下拖动即排序；侧栏是固定布局，只能改顺序 / 显隐 / 名称。
/// 2）「主页控件」——拖动移动、8 个手柄缩放、边界裁剪、双击居中、恢复默认位置与大小（与旧版一致）。
///
/// 编辑态是内存副本，编辑过程中不动主窗口，点「保存并应用」才写盘并生效。
/// 侧栏交互只用鼠标拖拽（<see cref="DragDrop"/>）+ 一组等价按钮，两种入口共用同一份实现。
/// </summary>
public partial class LayoutEditorWindow : Window
{
    /// <summary>拖拽元素时挂在数据对象上的格式名。</summary>
    private const string ElementDragFormat = "HMOL.LayoutElement";

    /// <summary>正在编辑的方案（<see cref="LayoutStore"/> 里的真实对象）。</summary>
    private LayoutScheme _target = LayoutScheme.CreateDefault();

    /// <summary>编辑中的元素清单（侧栏 + 主页）；点保存才写回 <see cref="_target"/>。</summary>
    private List<LayoutItem> _items = [];

    private bool _dirty;

    /// <summary>程序化改选中态时不要再当作用户操作处理。</summary>
    private bool _suppressSelection;

    private Point _dragStart;
    private string? _dragId;

    public LayoutEditorWindow()
    {
        InitializeComponent();

        LayoutStore.EnsureLoaded(LayoutElements.Ids);

        PanHomePreview.SelectionChanged += OnHomePreviewSelectionChanged;
        PanHomePreview.LayoutChanged += OnHomePreviewLayoutChanged;

        _target = LayoutStore.Find(LayoutStore.Active.Id) ?? LayoutStore.Active;
        LoadTarget();
        RefreshSchemeList();
    }

    /// <summary>打开编辑器。关掉后可自行刷新方案名显示。</summary>
    public static void Open(Window? owner) => new LayoutEditorWindow { Owner = owner }.ShowDialog();

    // ————— 方案 —————

    /// <summary>把选中方案的内容读进编辑态。</summary>
    private void LoadTarget()
    {
        // 借 Normalize 过一遍：清单缺项补上、已删元素丢掉、越界坐标裁回来
        var staging = new LayoutScheme { Items = _target.Items.Select(item => item.Clone()).ToList() };
        staging.Normalize(LayoutElements.Ids);

        _items = staging.Items;
        _dirty = false;

        RefreshElementLists(null);
        RefreshHomeTab(null);
    }

    private void RefreshSchemeList()
    {
        var activeId = LayoutStore.Active.Id;
        var rows = LayoutStore.All
            .Select(scheme => new LayoutSchemeRow(scheme, LayoutScheme.Same(scheme.Id, activeId)))
            .ToList();

        _suppressSelection = true;
        ListSchemes.ItemsSource = rows;
        ListSchemes.SelectedItem = rows.FirstOrDefault(row => LayoutScheme.Same(row.Id, _target.Id));
        _suppressSelection = false;

        BtnSchemeRename.IsEnabled = !_target.IsBuiltIn;
        BtnSchemeDelete.IsEnabled = !_target.IsBuiltIn;
    }

    private void OnSchemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (ListSchemes.SelectedItem is not LayoutSchemeRow row) return;
        if (LayoutScheme.Same(row.Id, _target.Id)) return;

        if (_dirty && !ConfirmDiscard())
        {
            SelectSchemeRow(_target.Id);
            return;
        }

        var scheme = LayoutStore.Find(row.Id);
        if (scheme is null)
        {
            SelectSchemeRow(_target.Id);
            return;
        }

        _target = scheme;
        LoadTarget();
        RefreshSchemeList();

        SetStatus($"已载入方案「{scheme.Name}」，改完点「保存并应用」生效。", warn: false);
    }

    private void SelectSchemeRow(string id)
    {
        _suppressSelection = true;

        if (ListSchemes.ItemsSource is IEnumerable<LayoutSchemeRow> rows)
            ListSchemes.SelectedItem = rows.FirstOrDefault(row => LayoutScheme.Same(row.Id, id));

        _suppressSelection = false;
    }

    private void OnSchemeNewClick(object sender, RoutedEventArgs e)
    {
        var name = TextInputWindow.Ask(this, "新建方案", "新方案名称：", "自定义布局", "新方案使用默认顺序，全部元素显示。",
            value => LayoutStore.ValidateName(value, null));

        if (name is null) return;

        var scheme = LayoutStore.Create(name, LayoutElements.Ids);
        if (scheme is null)
        {
            SetStatus("新建失败，请换一个名称。", warn: true);
            return;
        }

        _target = scheme;
        LoadTarget();
        RefreshSchemeList();

        SetStatus($"已新建方案「{scheme.Name}」，点「保存并应用」后生效。", warn: false);
    }

    private void OnSchemeRenameClick(object sender, RoutedEventArgs e)
    {
        if (_target.IsBuiltIn)
        {
            SetStatus($"内置方案「{LayoutScheme.DefaultName}」不支持重命名。", warn: true);
            return;
        }

        var name = TextInputWindow.Ask(this, "重命名方案", "新名称：", _target.Name, string.Empty,
            value => LayoutStore.ValidateName(value, _target.Id));

        if (name is null) return;

        if (!LayoutStore.Rename(_target, name))
        {
            SetStatus("重命名失败，请换一个名称。", warn: true);
            return;
        }

        RefreshSchemeList();
        SetStatus($"方案已重命名为「{name}」。", warn: false);
    }

    private void OnSchemeDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_target.IsBuiltIn)
        {
            SetStatus($"内置方案「{LayoutScheme.DefaultName}」不支持删除。", warn: true);
            return;
        }

        var choice = ChoiceWindow.Ask(this, "删除方案", $"确定删除方案「{_target.Name}」？",
            "删除后无法恢复。若删的正是当前启用的方案，界面会回到默认布局。",
            new ChoiceOption("删除", "delete", ButtonTone.Danger), new ChoiceOption("取消", "cancel"));

        if (choice != "delete") return;

        var wasActive = LayoutScheme.Same(LayoutStore.Active.Id, _target.Id);

        if (!LayoutStore.Delete(_target))
        {
            SetStatus("删除失败。", warn: true);
            return;
        }

        _target = LayoutStore.Find(LayoutStore.Active.Id) ?? LayoutStore.Active;
        LoadTarget();
        RefreshSchemeList();

        // 删掉的正是启用方案时，主界面必须立刻回到默认布局
        if (wasActive) ApplyToMainWindow();

        SetStatus("已删除方案。", warn: false);
    }

    /// <summary>把当前方案（含未保存的改动）导出成 JSON，方便备份或分享给朋友。</summary>
    private void OnSchemeExportClick(object sender, RoutedEventArgs e)
    {
        var export = new LayoutScheme
        {
            Id = _target.Id,
            Name = _target.Name,
            Description = _target.Description,
            Version = _target.Version,
            Items = _items.Select(item => item.Clone()).ToList(),
        };

        foreach (var item in export.Items) item.ClampBounds();

        var dialog = new SaveFileDialog
        {
            Title = "导出布局方案",
            Filter = "布局方案 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = $"{_target.Name}.json",
        };

        if (dialog.ShowDialog(this) != true) return;

        if (LayoutStore.ExportTo(export, dialog.FileName, out var error))
            SetStatus($"已导出方案「{_target.Name}」：{dialog.FileName}", warn: false);
        else
            SetStatus($"导出失败：{error}", warn: true);
    }

    /// <summary>从别人的 JSON 导入一套方案，导入后自动切过去。</summary>
    private void OnSchemeImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入布局方案",
            Filter = "布局方案 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        if (_dirty && !ConfirmDiscard()) return;

        var scheme = LayoutStore.ImportFrom(dialog.FileName, LayoutElements.Ids, out var error);

        if (scheme is null)
        {
            SetStatus($"导入失败：{error}", warn: true);
            return;
        }

        _target = scheme;
        LoadTarget();
        RefreshSchemeList();

        SetStatus($"已导入方案「{scheme.Name}」，点「保存并应用」后生效。", warn: false);
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        var name = TextInputWindow.Ask(this, "另存为新方案", "新方案名称：", $"布局 {DateTime.Now:MM-dd HHmm}",
            "会把当前编辑内容存成一套新方案。", value => LayoutStore.ValidateName(value, null));

        if (name is null) return;

        var scheme = LayoutStore.Create(name, LayoutElements.Ids);
        if (scheme is null)
        {
            SetStatus("另存失败，请换一个名称。", warn: true);
            return;
        }

        scheme.Items = _items.Select(item => item.Clone()).ToList();
        LayoutStore.Save(scheme);

        _target = scheme;
        RefreshSchemeList();

        SetStatus($"已另存为「{scheme.Name}」，点「保存并应用」后生效。", warn: false);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        _target.Items = _items;
        _target.Normalize(LayoutElements.Ids);

        if (!LayoutStore.Save(_target))
        {
            SetStatus("保存失败，请检查数据目录是否可写。", warn: true);
            return;
        }

        LayoutStore.SetActive(_target.Id);
        _dirty = false;

        RefreshSchemeList();
        RefreshHomeTab(PanHomePreview.SelectedId);
        ApplyToMainWindow();

        SetStatus($"已应用方案「{_target.Name}」。", warn: false);
        Log.Info($"界面布局已切换到方案「{_target.Name}」");
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var staging = new LayoutScheme();
        staging.Normalize(LayoutElements.Ids);

        _items = staging.Items;
        _dirty = true;

        RefreshElementLists(null);
        RefreshHomeTab(null);
        SetStatus("已恢复默认顺序、显示状态、名称与主页位置，点「保存并应用」后生效。", warn: false);
    }

    // ————— 元素列表 —————

    private void RefreshElementLists(string? selectId)
    {
        var shown = new List<LayoutElementRow>();
        var hidden = new List<LayoutElementRow>();

        foreach (var item in _items)
        {
            var info = LayoutElements.Find(item.ElementId);
            if (info is null) continue;

            var row = new LayoutElementRow(info, item);
            (item.Visible ? shown : hidden).Add(row);
        }

        _suppressSelection = true;
        ListShown.ItemsSource = shown;
        ListHidden.ItemsSource = hidden;

        var target = selectId is null ? null : shown.Concat(hidden).FirstOrDefault(row => LayoutScheme.Same(row.Id, selectId));
        ListShown.SelectedItem = target is not null && shown.Contains(target) ? target : null;
        ListHidden.SelectedItem = target is not null && hidden.Contains(target) ? target : null;
        _suppressSelection = false;

        // 立刻生成行容器：拖拽落点与插入位置全靠容器坐标算出来
        ListShown.UpdateLayout();
        ListHidden.UpdateLayout();

        UpdateActionButtons();
    }

    private void OnElementSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        // 两个列表共用一份选中态，选中一边就清掉另一边
        if (sender is ListBox { SelectedItem: not null } list)
        {
            _suppressSelection = true;
            if (!ReferenceEquals(list, ListShown)) ListShown.SelectedItem = null;
            if (!ReferenceEquals(list, ListHidden)) ListHidden.SelectedItem = null;
            _suppressSelection = false;
        }

        UpdateActionButtons();
    }

    private string? SelectedElementId() => SelectedRow()?.Id;

    private LayoutElementRow? SelectedRow() => ListShown.SelectedItem as LayoutElementRow
                                                ?? ListHidden.SelectedItem as LayoutElementRow;

    private LayoutItem? FindItem(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : _items.FirstOrDefault(item => LayoutScheme.Same(item.ElementId, id));

    /// <summary>显示中的侧栏元素。主页控件不参与侧栏排序，必须先排除，否则排序的插入位置会算错。</summary>
    private List<LayoutItem> VisibleNav()
        => _items.Where(item => item.Visible && LayoutElements.Find(item.ElementId) is not null).ToList();

    private void UpdateActionButtons()
    {
        var item = FindItem(SelectedElementId());
        var info = LayoutElements.Find(item?.ElementId);
        var visible = VisibleNav();
        var index = item is not null && item.Visible ? visible.IndexOf(item) : -1;

        BtnMoveUp.IsEnabled = index > 0;
        BtnMoveDown.IsEnabled = index >= 0 && index < visible.Count - 1;
        BtnHide.IsEnabled = item?.Visible == true && info?.CanHide == true;
        BtnShow.IsEnabled = item is not null && !item.Visible;
        BtnRenameElement.IsEnabled = item is not null;
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => SwapVisible(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => SwapVisible(1);

    /// <summary>与相邻的「显示中」元素交换位置。隐藏元素留在原地不动。</summary>
    private void SwapVisible(int delta)
    {
        var item = FindItem(SelectedElementId());
        if (item is null || !item.Visible) return;

        var visible = VisibleNav();
        var index = visible.IndexOf(item);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= visible.Count) return;

        var other = visible[target];
        var left = _items.IndexOf(item);
        var right = _items.IndexOf(other);
        (_items[left], _items[right]) = (_items[right], _items[left]);

        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideSelected();

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        var item = FindItem(SelectedElementId());
        if (item is null || item.Visible) return;

        item.Visible = true;
        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    private void HideSelected()
    {
        var item = FindItem(SelectedElementId());
        var info = LayoutElements.Find(item?.ElementId);
        if (item is null || info is null || !item.Visible) return;

        if (!info.CanHide)
        {
            SetStatus($"「{info.DisplayName}」是布局编辑器的入口，不能隐藏。", warn: true);
            return;
        }

        item.Visible = false;
        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    private void OnRenameElementClick(object sender, RoutedEventArgs e)
    {
        var item = FindItem(SelectedElementId());
        var info = LayoutElements.Find(item?.ElementId);
        if (item is null || info is null) return;

        var current = string.IsNullOrWhiteSpace(item.DisplayName) ? info.DisplayName : item.DisplayName!;

        var name = TextInputWindow.Ask(this, "重命名元素", $"「{info.DisplayName}」在界面上显示的名称：", current,
            $"留空表示恢复成「{info.DisplayName}」。", value => value.Length > 12 ? "名称不要超过 12 个字。" : null);

        if (name is null) return;

        item.DisplayName = string.IsNullOrWhiteSpace(name) || string.Equals(name, info.DisplayName, StringComparison.Ordinal)
            ? null
            : name;

        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    // ————— 主页控件 —————

    /// <summary>重绑预览并把列表一起刷新（切方案、恢复默认、应用之后）。</summary>
    private void RefreshHomeTab(string? selectId)
    {
        if (PanHomePreview is null) return;

        PanHomePreview.Bind(_items);
        RefreshHomeRows(selectId);
        PanHomePreview.Select(selectId);
    }

    /// <summary>
    /// 只重建列表行。拖动过程中不能重绑预览：预览里的方块正握着鼠标捕获，重建会打断拖动。
    /// </summary>
    private void RefreshHomeRows(string? selectId)
    {
        var rows = HomeLayoutElements.All.Select(info => new HomeElementRow(info, FindItem(info.Id))).ToList();

        _suppressSelection = true;
        ListHome.ItemsSource = rows;
        ListHome.SelectedItem = selectId is null
            ? null
            : rows.FirstOrDefault(row => LayoutScheme.Same(row.Id, selectId));
        _suppressSelection = false;

        UpdateHomeButtons();
    }

    private void OnHomeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        PanHomePreview.Select((ListHome.SelectedItem as HomeElementRow)?.Id);
        UpdateHomeButtons();
    }

    /// <summary>预览里点了方块：把列表选中态跟着挪过去。</summary>
    private void OnHomePreviewSelectionChanged(string? id)
    {
        _suppressSelection = true;
        ListHome.SelectedItem = id is null
            ? null
            : (ListHome.ItemsSource as IEnumerable<HomeElementRow>)?.FirstOrDefault(row => LayoutScheme.Same(row.Id, id));
        _suppressSelection = false;

        UpdateHomeButtons();
    }

    /// <summary>预览里的位置或大小变了：记脏 + 刷新行尾数值 + 把精确坐标写到状态栏。</summary>
    private void OnHomePreviewLayoutChanged()
    {
        _dirty = true;

        var id = PanHomePreview.SelectedId;
        RefreshHomeRows(id);
        ShowHomeStatus(id);
    }

    private void OnHomeCenterClick(object sender, RoutedEventArgs e) => PanHomePreview.Center(PanHomePreview.SelectedId);

    private void OnHomeResetClick(object sender, RoutedEventArgs e) => PanHomePreview.ResetToDefault(PanHomePreview.SelectedId);

    private void UpdateHomeButtons()
    {
        if (BtnHomeCenter is null) return;

        var row = ListHome.SelectedItem as HomeElementRow;
        var selected = row is not null;

        BtnHomeCenter.IsEnabled = selected;
        BtnHomeReset.IsEnabled = selected;

        if (BtnHomeHide is null || BtnHomeShow is null) return;

        var item = selected ? FindItem(row!.Id) : null;
        var info = HomeLayoutElements.Find(row?.Id);

        BtnHomeHide.IsEnabled = item is { Visible: true } && (info?.CanHide ?? false);
        BtnHomeShow.IsEnabled = item is { Visible: false };
    }

    // ————— 主页控件的显隐 —————

    private void OnHomeHideClick(object sender, RoutedEventArgs e)
    {
        var item = FindItem((ListHome.SelectedItem as HomeElementRow)?.Id);
        var info = HomeLayoutElements.Find(item?.ElementId);

        if (item is null || info is null || !item.Visible) return;

        if (!info.CanHide)
        {
            SetStatus($"「{info.DisplayName}」是主页的启动入口，不能隐藏。", warn: true);
            return;
        }

        item.Visible = false;
        _dirty = true;
        RefreshHomeTab(item.ElementId);
    }

    private void OnHomeShowClick(object sender, RoutedEventArgs e)
    {
        var item = FindItem((ListHome.SelectedItem as HomeElementRow)?.Id);
        if (item is null || item.Visible) return;

        item.Visible = true;
        _dirty = true;
        RefreshHomeTab(item.ElementId);
    }

    /// <summary>把选中控件的坐标写进状态栏：拖动时给个精确数值，便于微调。</summary>
    private void ShowHomeStatus(string? id)
    {
        var info = HomeLayoutElements.Find(id);
        var item = FindItem(id);
        if (info is null || item is null) return;

        SetStatus(item is { HasBounds: true }
            ? $"「{info.DisplayName}」左 {item.XPercent:0.#}%、上 {item.YPercent:0.#}%、宽 {item.WidthPercent:0.#}%、高 {item.HeightPercent:0.#}%（点「保存并应用」后生效）"
            : $"「{info.DisplayName}」跟随流式布局（点「保存并应用」后生效）", warn: false);
    }

    // ————— 拖拽 —————

    private void OnElementListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragId = FindRow(e.OriginalSource as DependencyObject)?.Id;
    }

    private void OnElementListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragId is null) return;

        var position = e.GetPosition(this);

        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var id = _dragId;
        _dragId = null;

        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ElementDragFormat, id), DragDropEffects.Move);
    }

    private void OnElementListDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || !e.Data.GetDataPresent(ElementDragFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        ShowDropMark(list, DropIndex(list, e.GetPosition(list)));
    }

    private void OnElementListDragLeave(object sender, DragEventArgs e) => HideDropMarks();

    private void OnElementListDrop(object sender, DragEventArgs e)
    {
        HideDropMarks();

        if (sender is not ListBox list) return;
        if (e.Data.GetData(ElementDragFormat) is not string id) return;

        if (ReferenceEquals(list, ListHidden)) HideDropped(id);
        else MoveToVisibleIndex(id, DropIndex(list, e.GetPosition(list)));

        e.Handled = true;
    }

    /// <summary>拖回左列表 = 隐藏。</summary>
    private void HideDropped(string id)
    {
        var item = FindItem(id);
        var info = LayoutElements.Find(id);

        if (item is null || info is null || !item.Visible) return;

        if (!info.CanHide)
        {
            SetStatus($"「{info.DisplayName}」是布局编辑器的入口，不能隐藏。", warn: true);
            return;
        }

        item.Visible = false;
        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    /// <summary>
    /// 落到右列表的第 <paramref name="visibleIndex"/> 行之前：先设为显示，再插入到该行对应元素前面。
    /// 本来就显示中的元素走同一条路径，于是「排序」与「拖入」共用一份实现。
    /// </summary>
    private void MoveToVisibleIndex(string id, int visibleIndex)
    {
        var item = FindItem(id);
        if (item is null) return;

        var info = LayoutElements.Find(id);
        if (info is null) return;

        var visible = VisibleNav();
        var anchor = visibleIndex < visible.Count ? visible[visibleIndex] : null;

        // 落点就在自己前面，位置不变
        if (ReferenceEquals(anchor, item)) return;

        item.Visible = true;
        _items.Remove(item);

        if (anchor is null) _items.Add(item);
        else _items.Insert(_items.IndexOf(anchor), item);

        _dirty = true;
        RefreshElementLists(item.ElementId);
    }

    /// <summary>鼠标位置对应的插入位置：落在哪一行的上半部分，就插到那一行前面。</summary>
    private static int DropIndex(ListBox list, Point point)
    {
        list.UpdateLayout();

        var count = list.Items.Count;

        for (var i = 0; i < count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;

            var bounds = Bounds(container, list);
            if (point.Y < bounds.Top + bounds.Height / 2) return i;
        }

        return count;
    }

    private void ShowDropMark(ListBox list, int index)
    {
        HideDropMarks();

        var mark = ReferenceEquals(list, ListHidden) ? MarkHidden : MarkShown;

        mark.Width = Math.Max(list.ActualWidth, 0);
        Canvas.SetLeft(mark, 0);
        Canvas.SetTop(mark, DropMarkY(list, index));
        mark.Visibility = Visibility.Visible;
    }

    private static double DropMarkY(ListBox list, int index)
    {
        var count = list.Items.Count;
        if (count == 0) return 2;

        var probe = index < count ? index : count - 1;
        if (list.ItemContainerGenerator.ContainerFromIndex(probe) is not ListBoxItem container) return 2;

        var bounds = Bounds(container, list);
        return index < count ? bounds.Top : Math.Max(bounds.Bottom - 2, 0);
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private void HideDropMarks()
    {
        MarkShown.Visibility = Visibility.Collapsed;
        MarkHidden.Visibility = Visibility.Collapsed;
    }

    /// <summary>从事件源向上找所属的列表行。</summary>
    private static LayoutElementRow? FindRow(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem { DataContext: LayoutElementRow row }) return row;

            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
        }

        return null;
    }

    // ————— 收尾 —————

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty && !ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private bool ConfirmDiscard() => ChoiceWindow.Ask(this, "放弃改动", "这套方案还有未保存的改动。",
        "点「放弃」会丢掉这些改动，界面布局保持原样。",
        new ChoiceOption("放弃", "discard", ButtonTone.Danger), new ChoiceOption("继续编辑", "keep")) == "discard";

    private void ApplyToMainWindow()
    {
        var main = Owner as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        main?.ApplyLayout();
    }

    private void SetStatus(string message, bool warn)
    {
        if (LabStatus is null) return;

        LabStatus.Text = message;
        LabStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }
}
