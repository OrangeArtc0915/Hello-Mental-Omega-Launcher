using HMOL.App.Controls.Svg;
using HMOL.Core.Logging;

namespace HMOL.App;

/// <summary>
/// 启动自检（仅 Debug 构建）。
/// 图标以字符串形式在 XAML 中引用，写错名字不会报错、只会静默不显示，
/// 所以启动时把图标包里的每个 SVG 都解析一遍并写进日志。
/// </summary>
internal static class SelfCheck
{
    public static void Run() => CheckIcons();

    private static void CheckIcons()
    {
        try
        {
            // 图标枚举与扩展清单的图标白名单共用一份实现（SvgIconLoader.AvailableKeys），
            // 图标的唯一事实来源就是内嵌资源本身。
            var names = SvgIconLoader.AvailableKeys();

            if (names.Count == 0)
            {
                Log.Warn("自检：未能枚举到任何图标资源");
                return;
            }

            var failed = new List<string>();
            foreach (var name in names)
            {
                if (SvgIconLoader.Get(name) is null) failed.Add(name);
            }

            if (failed.Count == 0)
                Log.Info($"自检：{names.Count} 个图标全部解析成功");
            else
                Log.Warn($"自检：{names.Count} 个图标中有 {failed.Count} 个解析失败 → {string.Join(", ", failed)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"自检（图标）执行失败：{ex.Message}");
        }
    }
}
