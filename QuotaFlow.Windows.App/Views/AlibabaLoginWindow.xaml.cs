using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace QuotaFlow.Windows.App.Views;

/// <summary>
/// 阿里云百炼"一键登录"窗口（WebView2）。
///
/// 目的：让用户在自己的浏览器会话里登录阿里云百炼，登录成功后本窗口自动抓取登录态
/// （Console Cookie），交给调用方存入 Windows 凭据管理器——全程无需用户手工复制粘贴 Cookie，
/// 也绝不在日志/文件里出现 Cookie 明文。
///
/// 登录完成判定：轮询页面里的 <c>window.ALIYUN_CONSOLE_CONFIG.CURRENT_PK</c>（登录后才会有
/// 用户主账号 ID），或凭据管理器里出现阿里云登录会话 Cookie（login_aliyunid / sid 等），
/// 两者任一命中即认为登录成功。抓 Cookie 时对 <c>bailian.console.aliyun.com</c> 和
/// <c>bailian-cs.console.aliyun.com</c> 两个域名都取一次并去重合并——控制台网关和页面分属
/// 不同子域，只取一个域的 Cookie 可能缺失部分会话信息。
///
/// 安全：WebView2 使用独立的 UserDataFolder（应用私有目录），Cookie 抓取后立即以事件形式
/// 交还调用方（仅内存传递），本窗口不落盘。
/// </summary>
public partial class AlibabaLoginWindow : Window
{
    private const string ConsoleUrl = "https://bailian.console.aliyun.com/cn-beijing?tab=plan";

    /// <summary>出现即代表已登录的阿里云会话 Cookie 名（任一命中即可）。</summary>
    private static readonly string[] SessionCookieSignals =
        ["login_aliyunid", "login_aliyunid_pk", "sid", "unb", "aliyun_choice", "LOGIN_ALIYUNID"];

    private static readonly string[] CookieHosts =
    [
        "https://bailian.console.aliyun.com",
        "https://bailian-cs.console.aliyun.com",
    ];

    private readonly DispatcherTimer _detectTimer;
    private bool _loginSucceeded;
    private bool _closedByUser;
    private bool _webViewReady;

    /// <summary>登录成功事件：携带抓取到的完整 Cookie 字符串（仅内存传递）。</summary>
    public event Action<string>? LoginSucceeded;

    /// <summary>登录过程失败（WebView2 不可用等）。非用户主动取消。</summary>
    public event Action<string>? LoginFailed;

    public AlibabaLoginWindow()
    {
        InitializeComponent();
        _detectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _detectTimer.Tick += async (_, _) => await OnDetectTickAsync();
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: GetUserDataFolder());

            await LoginWebView.EnsureCoreWebView2Async(env);
            LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            LoginWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webViewReady = true;

            LoadingHint.Text = "正在加载阿里云百炼控制台…";
            LoginWebView.CoreWebView2.Navigate(ConsoleUrl);
            _detectTimer.Start();
        }
        catch (Exception)
        {
            // WebView2 运行时缺失/初始化失败：明确告知用户，而不是让窗口一直空白。
            _detectTimer.Stop();
            _webViewReady = false;
            LoadingHint.Text = "登录组件初始化失败";
            System.Windows.MessageBox.Show(this,
                "无法初始化 WebView2 登录组件（通常是系统缺少 Microsoft Edge WebView2 Runtime）。\n\n" +
                "请从 https://developer.microsoft.com/microsoft-edge/webview2/ 安装 WebView2 Runtime 后重试。",
                "登录组件不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            LoginFailed?.Invoke("WebView2 运行时不可用");
            Close();
        }
    }

    private static string GetUserDataFolder()
    {
        // 应用私有目录下独立 UserDataFolder：避免与 Edge / 其它 WebView2 应用互相污染登录态，
        // 也保证 Cookie 在应用重启后仍可用（配合凭据管理器双保险）。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            ?? Path.GetTempPath();
        var folder = Path.Combine(localAppData, "QuotaFlow", "WebView2");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task OnDetectTickAsync()
    {
        if (!_webViewReady || _loginSucceeded)
        {
            return;
        }

        bool loggedIn;
        try
        {
            loggedIn = await IsLoggedInAsync();
        }
        catch (Exception)
        {
            // 页面正在导航/JS 执行瞬时失败时跳过本轮，等下一个 tick。
            return;
        }

        if (!loggedIn)
        {
            return;
        }

        _loginSucceeded = true;
        _detectTimer.Stop();

        string cookieString;
        try
        {
            cookieString = await CollectCookiesAsync();
        }
        catch (Exception ex)
        {
            LoginFailed?.Invoke($"登录成功但抓取 Cookie 失败：{ex.GetType().Name}");
            Close();
            return;
        }

        if (string.IsNullOrWhiteSpace(cookieString))
        {
            LoginFailed?.Invoke("登录成功但未取到会话 Cookie，请重试");
            Close();
            return;
        }

        LoginSucceeded?.Invoke(cookieString);
        Close();
    }

    private async Task<bool> IsLoggedInAsync()
    {
        // ① 页面里的用户主账号 ID（登录后才存在）。
        const string js = """
            (function () {
                try {
                    var cfg = window.ALIYUN_CONSOLE_CONFIG || {};
                    return (cfg.CURRENT_PK || '').toString();
                } catch (e) { return ''; }
            })();
            """;
        var pkResult = await LoginWebView.CoreWebView2.ExecuteScriptAsync(js);
        if (!string.IsNullOrWhiteSpace(pkResult) && pkResult.Trim('"').Length > 0)
        {
            return true;
        }

        // ② 会话 Cookie 兜底。
        var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(CookieHosts[0]);
        return cookies.Any(c => SessionCookieSignals.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<string> CollectCookiesAsync()
    {
        var sb = new StringBuilder();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in CookieHosts)
        {
            IReadOnlyList<CoreWebView2Cookie> cookies;
            try
            {
                cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(host);
            }
            catch (Exception)
            {
                continue; // 单个域失败不阻塞整体。
            }

            foreach (var cookie in cookies)
            {
                if (seenNames.Add(cookie.Name))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append("; ");
                    }

                    sb.Append(cookie.Name).Append('=').Append(cookie.Value);
                }
            }
        }

        return sb.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _closedByUser = true;
        _detectTimer.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _detectTimer.Stop();
        if (!_loginSucceeded && !_closedByUser)
        {
            // 未成功也未取消（例如窗口被系统关闭）：给调用方一个干净的结果。
            LoginFailed?.Invoke("登录窗口已关闭，未完成登录");
        }

        base.OnClosed(e);
    }
}
