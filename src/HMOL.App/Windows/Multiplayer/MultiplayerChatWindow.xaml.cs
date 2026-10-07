using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMOL.Core.Multiplayer;
using HMOL.Core.Localization;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 聊天窗：大厅聊天与房间聊天共用（对应旧版 <c>ChatDialog</c>）。
/// 发送逻辑由调用方以委托传入，本窗只负责显示与输入。
/// </summary>
public partial class MultiplayerChatWindow : Window
{
    private static readonly FontFamily MonoFont = new("Consolas, Microsoft YaHei UI");

    private readonly Func<string, ChatSendStatus> _send;
    private readonly string _selfName;

    public MultiplayerChatWindow(string title, string selfName, Func<string, ChatSendStatus> send)
    {
        InitializeComponent();

        Title = title;
        Card.Title = title;

        _selfName = string.IsNullOrWhiteSpace(selfName) ? Loc.T("我") : selfName;
        _send = send;

        Loaded += (_, _) => TxtInput.Focus();
    }

    /// <summary>追加一行到历史区。</summary>
    public void Append(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var block = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Text = line
        };

        block.SetResourceReference(TextBlock.ForegroundProperty,
            line.StartsWith("(", StringComparison.Ordinal) ? "Text.Tertiary" : "Text.Primary");

        PanHistory.Children.Add(block);

        // 只留最近 400 行，长时间挂着也不会把内存吃满
        while (PanHistory.Children.Count > 400) PanHistory.Children.RemoveAt(0);

        ScrollHistory.ScrollToEnd();
    }

    /// <summary>追加一条系统提示（灰色显示）。</summary>
    public void AppendSystem(string text) => Append($"({text})");

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        SendCurrent();
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => SendCurrent();

    private void SendCurrent()
    {
        var text = ChatCrypt.SanitizeText(TxtInput.Text, ChatCrypt.MaxTextLength);
        TxtInput.Clear();

        if (text.Length == 0) return;

        var status = _send(text);

        switch (status)
        {
            case ChatSendStatus.Ok:
                Append($"[{DateTime.Now:HH:mm:ss}] {_selfName}: {text}");
                break;

            case ChatSendStatus.Blocked:
                AppendSystem(Loc.T("消息含违禁内容或网址，已被拦截"));
                break;

            case ChatSendStatus.Limited:
                AppendSystem(Loc.T("发送过快，请稍候再试"));
                break;

            default:
                AppendSystem(Loc.T("未连接，消息无法发送"));
                break;
        }
    }
}
