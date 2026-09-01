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
/// 两者任一命中即认为登录成功。
///
/// Cookie 采集范围：抓取 WebView2 这个登录会话（独立 UserDataFolder）里的<b>全部</b> Cookie，
/// 不按域名/字段名做任何取舍——早期版本只挑了 bailian-cs.console + www.aliyun.com 两个域，
/// 结果漏掉了用户实际登录所在的 bailian.console.aliyun.com 自身的会话 Cookie，导致查询时
/// 鉴权信息不完整，反复出现"需要重新登录"。人为判断"哪个域的 Cookie 是查询必需的"本质上是
/// 一种脆弱的猜测，一旦服务端鉴权逻辑依赖了某个没被选中的域，就会复现同类问题——所以改为
/// 完整保留，把"体积可能超过 Windows 凭据管理器单条 2560 字节上限"这个问题交给
/// <see cref="Core.Services.SecureCredentialStore.SaveLarge"/> 的分片存储解决，而不是靠精简
/// 内容硬凑。
///
/// 登录成功后还会顺带从页面 JS 里提取 SEC_TOKEN（控制台脚本注入的令牌，供查询网关鉴权），
/// 一并交还调用方；Provider 优先使用它，避免每次查询都依赖从 HTML 正则提取。
///
/// 安全：WebView2 使用独立的 UserDataFolder（应用私有目录），Cookie 抓取后立即以事件形式
/// 交还调用方（仅内存传递），本窗口不落盘、不写日志。
/// </summary>
public partial class AlibabaLoginWindow : Window
{
    private const string ConsoleUrl = "https://bailian.console.aliyun.com/cn-beijing?tab=plan";

    /// <summary>出现即代表已登录的阿里云会话 Cookie 名（任一命中即可）。</summary>
    private static readonly string[] SessionCookieSignals =
        ["login_aliyunid", "login_aliyunid_pk", "sid", "unb", "aliyun_choice", "LOGIN_ALIYUNID"];

    private readonly DispatcherTimer _detectTimer;
    private bool _loginSucceeded;
    private bool _closedByUser;
    private bool _webViewReady;
    private bool _navigated;

    /// <summary>登录成功事件：携带抓取到的 Cookie 字符串 + 页面里提取到的 SEC_TOKEN（均为空
    /// 字符串表示未取到；仅内存传递，调用方负责安全存储）。</summary>
    public event Action<string, string>? LoginSucceeded;

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
            _detectTimer.Start();

            // 关键时序：不能在窗口还没完成布局时就 Navigate——WebView2 会用当时的极小初始尺寸
            // 渲染内容，实测百炼控制台据此判定为"移动端视口"而显示"暂不支持移动端体验"（内宽只有
            // 167px）。等到控件拿到真实宽度（>400px）后再导航，页面才会以桌面视口加载。
            LoginWebView.SizeChanged += OnLoginWebViewSizeChanged;
            TryNavigateWhenSized();
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

    /// <summary>等到 WebView2 控件拿到真实宽度后再执行一次导航（仅一次），避免以极小的预布局
    /// 尺寸渲染页面、被百炼控制台误判成移动端视口。</summary>
    private void OnLoginWebViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 400)
        {
            TryNavigateWhenSized();
        }
    }

    private void TryNavigateWhenSized()
    {
        if (_navigated || !_webViewReady || LoginWebView.ActualWidth <= 400)
        {
            return;
        }

        _navigated = true;
        LoginWebView.CoreWebView2.Navigate(ConsoleUrl);
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

        // 顺带从页面 JS 里提取 SEC_TOKEN（供查询网关鉴权），取不到也不阻塞——Provider 会降级
        // 到从控制台页面 HTML 正则提取。
        var secToken = string.Empty;
        try
        {
            secToken = await TryExtractSecTokenAsync();
        }
        catch (Exception)
        {
            // 提取失败不影响登录完成。
        }

        LoginSucceeded?.Invoke(cookieString, secToken);
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

        // ② 会话 Cookie 兜底：uri 传 null 时 WebView2 返回当前 Profile 里的全部 Cookie
        // （不限定域名——理由见类注释：按域名筛选是脆弱的猜测，曾经因此漏掉过关键会话 Cookie）。
        var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(null);
        return cookies.Any(c => SessionCookieSignals.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>抓取当前登录会话的<b>全部</b> Cookie（不按域名筛选），按
    /// "域名+路径+名称"去重（同名 Cookie 可能合法地存在于不同域，不能只按名称去重丢弃）。</summary>
    private async Task<string> CollectCookiesAsync()
    {
        IReadOnlyList<CoreWebView2Cookie> cookies;
        try
        {
            cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync(null);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var seen = new HashSet<(string Domain, string Path, string Name)>();
        foreach (var cookie in cookies)
        {
            if (!seen.Add((cookie.Domain, cookie.Path, cookie.Name)))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append("; ");
            }

            sb.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return sb.ToString();
    }

    /// <summary>从登录后的页面里提取 SEC_TOKEN：优先 window.SEC_TOKEN，其次遍历 window 全局里
    /// 带 SEC_TOKEN 字符串属性的对象，最后对 DOM 做正则（对应产品资料里的 SEC_TOKEN 提取模式）。</summary>
    private async Task<string> TryExtractSecTokenAsync()
    {
        const string js = """
            (function () {
                try {
                    if (typeof window.SEC_TOKEN !== 'undefined' && window.SEC_TOKEN) {
                        return window.SEC_TOKEN.toString();
                    }
                    for (var k in window) {
                        try {
                            var v = window[k];
                            if (v && typeof v === 'object' && typeof v.SEC_TOKEN === 'string' && v.SEC_TOKEN) {
                                return v.SEC_TOKEN;
                            }
                        } catch (e) { }
                    }
                    var m = (document.documentElement ? document.documentElement.outerHTML : '').match(/\bSEC_TOKEN\s*:\s*"([^"]+)"/);
                    if (m && m[1]) return m[1];
                } catch (e) { }
                return '';
            })();
            """;

        var result = await LoginWebView.CoreWebView2.ExecuteScriptAsync(js);
        var token = result?.Trim().Trim('"');
        return string.IsNullOrEmpty(token) ? string.Empty : token;
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
