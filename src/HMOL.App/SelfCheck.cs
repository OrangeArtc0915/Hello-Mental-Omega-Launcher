using System.Windows;
using HMOL.App.Controls.Svg;
using HMOL.App.Windows;
using HMOL.Core.Logging;
using HMOL.Core.Localization;

namespace HMOL.App;

/// <summary>
/// 启动自检（仅 Debug 构建）。
/// 图标以字符串形式在 XAML 中引用，写错名字不会报错、只会静默不显示，
/// 所以启动时把图标包里的每个 SVG 都解析一遍并写进日志。
/// 「新手教程」窗口只有用户点开时才创建，XAML 有问题平时看不出来，这里也一并试建一次。
/// </summary>
internal static class SelfCheck
{
    public static void Run()
    {
        CheckIcons();
        CheckTutorialWindow();
    }

    /// <summary>
    /// 试建「新手教程」窗口再关掉：它的 XAML 只在用户点开时才加载，
    /// 出错的话平时完全看不出来，这里提前把它挡在发布之前。
    /// </summary>
    private static void CheckTutorialWindow()
    {
        Window? window = null;

        try
        {
            window = new TutorialWindow();
            Log.Info(Loc.T("自检：新手教程窗口创建成功"));
        }
        catch (Exception ex)
        {
            Log.Error(Loc.T("自检（新手教程窗口）失败"), ex);
        }
        finally
        {
            try { window?.Close(); }
            catch { /* 没显示过的窗口关闭时可能抛异常，忽略 */ }
        }
    }

    private static void CheckIcons()
    {
        try
        {
            // 图标枚举与扩展清单的图标白名单共用一份实现（SvgIconLoader.AvailableKeys），
            // 图标的唯一事实来源就是内嵌资源本身。
            var names = SvgIconLoader.AvailableKeys();

            if (names.Count == 0)
            {
                Log.Warn(Loc.T("自检：未能枚举到任何图标资源"));
                return;
            }

            var failed = new List<string>();
            foreach (var name in names)
            {
                if (SvgIconLoader.Get(name) is null) failed.Add(name);
            }

            if (failed.Count == 0)
                Log.Info(Loc.F("自检：{0} 个图标全部解析成功", names.Count));
            else
                Log.Warn(Loc.F("自检：{0} 个图标中有 {1} 个解析失败 → {2}", names.Count, failed.Count, string.Join(Loc.T(", "), failed)));
        }
        catch (Exception ex)
        {
            Log.Warn(Loc.F("自检（图标）执行失败：{0}", ex.Message));
        }
    }
}
