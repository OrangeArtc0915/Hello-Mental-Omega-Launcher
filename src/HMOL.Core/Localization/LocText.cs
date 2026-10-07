using System.ComponentModel;

namespace HMOL.Core.Localization;

/// <summary>
/// XAML 绑定用的文本代理：<c>{loc:T '原文'}</c> 的目标属性是依赖属性时绑到这里的 <see cref="Value"/>，
/// 换语言时统一触发 <see cref="INotifyPropertyChanged.PropertyChanged"/>，界面即时刷新（不用重建窗口）。
/// 同一条原文全局只建一个实例（<see cref="For"/> 带缓存），绑定只依赖它、不额外持有引用。
/// </summary>
public sealed class LocText : INotifyPropertyChanged
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LocText> Cache = new(StringComparer.Ordinal);
    private static bool _hooked;

    private readonly string _msgId;

    private LocText(string msgId) => _msgId = msgId;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前语言下的译文；换语言后由 <see cref="RefreshAll"/> 触发绑定重读。</summary>
    public string Value => Loc.T(_msgId);

    /// <summary>取（或建）某条原文的代理实例。</summary>
    public static LocText For(string msgId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(msgId, out var cached)) return cached;

            if (!_hooked)
            {
                _hooked = true;
                Loc.Changed += RefreshAll;
            }

            var created = new LocText(msgId);
            Cache[msgId] = created;
            return created;
        }
    }

    private static void RefreshAll()
    {
        LocText[] all;

        lock (Gate) all = Cache.Values.ToArray();

        foreach (var item in all)
            item.PropertyChanged?.Invoke(item, new PropertyChangedEventArgs(nameof(Value)));
    }
}