using System.Windows;
using System.Windows.Controls;
using HMOL.App.Controls;
using HMOL.App.Services;
using HMOL.App.Windows;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 更多设置窗（对应旧版 <c>MoreDialog</c>）：自定义节点、进程守护与开机自启开关。
/// 自启状态以注册表为准，与「设置」页的开关各刷各的。
/// </summary>
public partial class MultiplayerMoreWindow : Window
{
    public MultiplayerMoreWindow()
    {
        InitializeComponent();

        RefreshNodes();
        RefreshGuard();
        RefreshAutoStart();
    }

    private void RefreshNodes()
    {
        ListNode.ItemsSource = MultiplayerSettingsStore.Current.ValidCustomNodes.ToList();
    }

    private void RefreshGuard()
    {
        var enabled = MultiplayerSettingsStore.Current.GuardEnabled;

        BtnGuard.Content = enabled ? Loc.T("已开启") : Loc.T("已关闭");
        BtnGuard.Tone = enabled ? ButtonTone.Solid : ButtonTone.Outline;
    }

    private void OnAddNodeClick(object sender, RoutedEventArgs e)
    {
        var node = TxtNode.Text.Trim();
        if (node.Length == 0) return;

        if (MultiplayerSettingsStore.Current.ValidCustomNodes.Any(item =>
                string.Equals(item, node, StringComparison.OrdinalIgnoreCase)))
        {
            ChoiceWindow.Info(this, Loc.T("自定义节点"), Loc.T("这个节点已经在列表里了。"));
            return;
        }

        MultiplayerSettingsStore.Update(settings => settings.CustomNodes.Add(node));

        TxtNode.Clear();
        RefreshNodes();
    }

    private void OnRemoveNodeClick(object sender, RoutedEventArgs e)
    {
        if (ListNode.SelectedItem is not string node) return;

        MultiplayerSettingsStore.Update(settings => settings.CustomNodes.RemoveAll(item =>
            string.Equals(item, node, StringComparison.OrdinalIgnoreCase)));

        RefreshNodes();
    }

    private void OnGuardClick(object sender, RoutedEventArgs e)
    {
        var enabled = !MultiplayerSettingsStore.Current.GuardEnabled;

        MultiplayerSettingsStore.Update(settings => settings.GuardEnabled = enabled);

        // 已经连着的时候立刻生效，不用重连
        MultiplayerHub.Session.GuardEnabled = enabled;

        RefreshGuard();
    }

    // ————— 开机自启 —————

    /// <summary>
    /// 刷新开关。<paramref name="operation"/> 不为空表示刚点过开关，直接报这次的结果；否则按注册表实际状态描述。
    /// 状态一律以注册表为准，配置文件里的 AutoStart 字段只作记录。
    /// </summary>
    private void RefreshAutoStart(AutoStartResult? operation = null)
    {
        var state = AutoStartManager.Query();

        BtnAutoStart.Content = state.IsEnabled ? Loc.T("已开启") : Loc.T("已关闭");
        BtnAutoStart.Tone = state.IsEnabled ? ButtonTone.Solid : ButtonTone.Outline;

        if (operation is not null)
        {
            SetAutoStartStatus(operation.Message, warn: !operation.Ok);
            return;
        }

        switch (state.Status)
        {
            case AutoStartStatus.Enabled:
                SetAutoStartStatus(Loc.F("自启命令：{0}", state.Command), warn: false);
                break;

            // 注册表里那条指向别的 exe（程序被挪过）：开关先按「关」显示，再点一次就改成当前位置
            case AutoStartStatus.PointsToOtherExe:
                SetAutoStartStatus(Loc.F("{0}再点一次开关即可改成当前位置。", state.Message), warn: true);
                break;

            case AutoStartStatus.Failed:
                SetAutoStartStatus(state.Message, warn: true);
                break;

            default:
                SetAutoStartStatus(Loc.T("未设置开机自启。"), warn: false);
                break;
        }
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        var state = AutoStartManager.Query();

        // 注册表都读不出来时先别瞎改，把原因摆给用户看
        if (state.Status == AutoStartStatus.Failed)
        {
            SetAutoStartStatus(state.Message, warn: true);
            return;
        }

        var enable = !state.IsEnabled;
        var result = enable ? AutoStartManager.Enable() : AutoStartManager.Disable();

        if (result.Ok)
        {
            // 改注册表成功才同步配置字段，两边保持一致
            MultiplayerSettingsStore.Update(settings => settings.AutoStart = enable);
        }
        else
        {
            Log.Warn(Loc.F("开机自启设置失败：{0}", result.Message));
        }

        RefreshAutoStart(result);
    }

    private void SetAutoStartStatus(string message, bool warn)
    {
        LabAutoStartStatus.Text = message;
        LabAutoStartStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Status.Warn" : "Text.Tertiary");
    }
}
