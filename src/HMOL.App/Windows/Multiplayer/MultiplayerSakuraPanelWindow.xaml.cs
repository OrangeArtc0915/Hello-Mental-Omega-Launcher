using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using HMOL.Core.App;
using HMOL.Core.Logging;

namespace HMOL.App.Windows.Multiplayer;

/// <summary>
/// 樱花FRP 网页面板的宿主窗口（可选功能）。
///
/// <para>
/// 面板要登录，所以用户数据目录单独一份（<c>Data\WebView2-Sakura</c>）：登录状态留在本机、
/// 可随程序一起搬走，也不会和别处的 WebView2 抢同一个目录（同一份目录不能被两个内核同时占用）。
/// </para>
/// <para>
/// 依赖本机的 WebView2 运行时（Win10/11 通常自带，或装了 Edge 就有）。缺失时
/// <see cref="IsRuntimeAvailable"/> 返回 false，调用方应当把入口置灰而不是让窗口报错。
/// </para>
/// </summary>
public partial class MultiplayerSakuraPanelWindow : Window
{
    /// <summary>官方管理面板的隧道列表页。</summary>
    public const string PanelUrl = "https://www.natfrp.com/tunnel/";

    private WebView2? _browser;

    public MultiplayerSakuraPanelWindow()
    {
        InitializeComponent();

        LabAddress.Text = PanelUrl;
        LabAddress.ToolTip = PanelUrl;

        Loaded += OnLoaded;
        Closed += (_, _) => DisposeBrowser();
    }

    /// <summary>本机是否装了 WebView2 运行时。缺了就不要再开这个窗口。</summary>
    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var userDataFolder = Path.Combine(Paths.Data, "WebView2-Sakura");
            Directory.CreateDirectory(userDataFolder);

            var browser = new WebView2();
            _browser = browser;
            PanHost.Child = browser;

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await browser.EnsureCoreWebView2Async(environment);

            var core = browser.CoreWebView2;
            if (core is null)
            {
                ShowError("WebView2 内核没有创建成功，换个方式打开 natfrp.com 吧。");
                return;
            }

            core.SourceChanged += (_, _) => RefreshAddress();
            core.NavigationCompleted += (_, _) =>
            {
                RefreshAddress();
                RefreshNavState();
            };

            // 面板里的外链都在本窗口内打开，不弹系统浏览器
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrWhiteSpace(args.Uri)) core.Navigate(args.Uri);
            };

            BtnBack.IsEnabled = true;
            BtnForward.IsEnabled = true;
            BtnRefresh.IsEnabled = true;

            core.Navigate(PanelUrl);
            Log.Info("樱花FRP 面板已在内嵌浏览器中打开");
        }
        catch (Exception ex)
        {
            ShowError($"无法打开内嵌浏览器：{ex.Message}");
        }
    }

    private void DisposeBrowser()
    {
        try
        {
            _browser?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"释放樱花FRP 面板失败：{ex.Message}");
        }

        _browser = null;
    }

    // ————— 导航 —————

    private void RefreshAddress()
    {
        var uri = _browser?.Source?.ToString() ?? PanelUrl;

        LabAddress.Text = uri;
        LabAddress.ToolTip = uri;
    }

    private void RefreshNavState()
    {
        var core = _browser?.CoreWebView2;
        if (core is null) return;

        BtnBack.IsEnabled = core.CanGoBack;
        BtnForward.IsEnabled = core.CanGoForward;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_browser?.CoreWebView2?.CanGoBack == true) _browser.CoreWebView2.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_browser?.CoreWebView2?.CanGoForward == true) _browser.CoreWebView2.GoForward();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var core = _browser?.CoreWebView2;
        if (core is null) return;

        core.Reload();
    }

    private void ShowError(string message)
    {
        LabError.Text = message;
        LabError.Visibility = Visibility.Visible;
        PanHost.Visibility = Visibility.Collapsed;

        Log.Warn($"樱花FRP 面板初始化失败：{message}");
    }
}
