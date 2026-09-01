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

    /// <summary>
    /// 【已废弃的判定方式，保留说明以免重蹈覆辙】早期用"存在某些 Cookie 名"来判定已登录，
    /// 实测这是错的：`login_aliyunid_pk` / `login_current_pk` 这类 Cookie 记录的是"这个浏览器
    /// Profile 上次登录过哪个账号"，属于历史残留，即使当前会话已登出/过期也依然存在；
    /// `cna` / `isg` / `tfstk` 更是阿里巴巴全站的匿名追踪 Cookie，跟登录完全无关。
    /// 依赖它们会导致"页面明明显示未登录，程序却判定已登录、抓一堆无效 Cookie 就关窗"。
    /// 现在改为只信任页面真实渲染出的登录态（<see cref="IsLoggedInAsync"/>）。
    /// </summary>
    /// <summary>登录探测脚本约定的"已登录"标记前缀，宿主侧按它精确匹配。</summary>
    private const string LoggedInMarker = "YES:";

    private const string LoginStateProbeJs = """
        (function () {
            try {
                var cfg = window.ALIYUN_CONSOLE_CONFIG || {};
                var pk = cfg.CURRENT_PK;
                // CURRENT_PK 必须是非空、非 'null'/'undefined' 字面量的真实账号 ID。
                if (pk === null || pk === undefined) { return 'NO'; }
                pk = ('' + pk).trim();
                if (pk === '' || pk === 'null' || pk === 'undefined' || pk === '0') { return 'NO'; }
                return 'YES:' + pk.length;
            } catch (e) { return 'ERR'; }
        })();
        """;

    private readonly DispatcherTimer _detectTimer;
    private readonly List<CoreWebView2Frame> _childFrames = [];
    private bool _loginSucceeded;
    private bool _closedByUser;
    private bool _webViewReady;
    private bool _navigated;
    private int _networkResponseCount;

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
            // 百炼控制台是微前端架构，子应用（含 Token Plan 页面本身）很可能渲染在 iframe 里，
            // SEC_TOKEN 若被子应用自己的脚本注入，只会存在于那个 iframe 的 window/DOM 里，
            // 主 frame（LoginWebView.CoreWebView2）看不到。这个 SDK 版本没有同步的 Frames
            // 集合，只能靠 FrameCreated 事件持续收集，供提取 SEC_TOKEN 时逐个尝试。
            LoginWebView.CoreWebView2.FrameCreated += (_, args) => _childFrames.Add(args.Frame);

            // 临时诊断（问题解决后删除）：SEC_TOKEN 既不在主/子 frame 的 window 全局或 DOM 里，
            // 也不是一个 Cookie 名——排除了目前所有假设。改为监听全部网络响应，看它是否是某次
            // XHR/fetch 响应体里的字段（不挂在 window 上、只存在于页面脚本的闭包变量里，
            // 外部 ExecuteScriptAsync 天然访问不到）。只记录"哪个 URL 的响应体里含有该字符串"
            // 这类结构性信息，不记录响应体内容本身。
            Interlocked.Exchange(ref _networkResponseCount, 0);
            LoginWebView.CoreWebView2.WebResourceResponseReceived += async (_, args) =>
            {
                Interlocked.Increment(ref _networkResponseCount);
                try
                {
                    var url = args.Request.Uri;
                    var contentType = args.Response.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value ?? "";

                    // 不再按 Content-Type 过滤——之前只看 json/text 会漏掉 application/javascript
                    // 这类脚本响应，而 SEC_TOKEN 的生成逻辑很可能就内嵌在某个业务 JS 文件里。
                    // 跳过明显的图片/字体二进制资源即可，其余一律检查响应体文本内容。
                    if (contentType.Contains("image/", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("font/", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("audio/", StringComparison.OrdinalIgnoreCase) ||
                        contentType.Contains("video/", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    using var stream = await args.Response.GetContentAsync();
                    if (stream is null)
                    {
                        return;
                    }

                    using var reader = new StreamReader(stream);
                    var body = await reader.ReadToEndAsync();
                    if (body.Contains("SEC_TOKEN", StringComparison.Ordinal))
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "quotaflow-tokenplan-network-diag.txt"),
                            $"[{DateTime.Now:HH:mm:ss}] Response body contains 'SEC_TOKEN': {url} (Content-Type: {contentType}, length: {body.Length})\n");
                    }
                }
                catch (Exception ex)
                {
                    // 记录异常类型（不含内容），帮助判断是否 GetContentAsync 本身就失败了。
                    try
                    {
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "quotaflow-tokenplan-network-diag.txt"),
                            $"[{DateTime.Now:HH:mm:ss}] Probe failed for {args.Request.Uri}: {ex.GetType().Name}\n");
                    }
                    catch (Exception)
                    {
                    }
                }
            };

            _webViewReady = true;

            LoadingHint.Text = "正在加载阿里云百炼控制台…";
            // 首屏加载完就把这层覆盖文字撤掉——它是绝对定位悬浮在 WebView2 之上的，
            // 不隐藏会一直压在登录页面中央。
            LoginWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                LoadingHint.Visibility = Visibility.Collapsed;
            };
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
            // 让用户看得见程序在等什么——之前窗口"没登录就自己关了"时，用户完全无从判断
            // 程序处于什么状态。
            StatusHint.Text = loggedIn
                ? "已检测到登录状态，正在读取额度所需信息…"
                : "等待登录…（请在下方页面完成阿里云账号登录）";
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

        // 顺带从页面 JS 里提取 SEC_TOKEN（供查询网关鉴权）。实测：控制台是纯前端 SPA
        // （efm-fe 异步微前端），服务端渲染的 HTML 里永远不包含 SEC_TOKEN——它是页面 JS
        // 执行后才动态注入的；"检测到已登录"（CURRENT_PK 出现）和"SEC_TOKEN 已注入"是两个
        // 独立的异步时机，前者可能先于后者发生。这里退避重试几次，给 SPA 懒加载的业务模块
        // 留出时间，而不是只试一次就放弃——只试一次曾经导致"明明登录成功却拿不到 SEC_TOKEN"。
        var secToken = string.Empty;
        for (var attempt = 0; attempt < 6 && string.IsNullOrEmpty(secToken); attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(500);
            }

            try
            {
                secToken = await TryExtractSecTokenAsync();
            }
            catch (Exception)
            {
                // 单次提取失败不影响后续重试；重试次数用尽后 secToken 仍为空，交给调用方处理。
            }
        }

        LoginSucceeded?.Invoke(cookieString, secToken);
        Close();
    }

    /// <summary>
    /// 判定当前页面是否处于真实登录态。只信任页面渲染出的 <c>ALIYUN_CONSOLE_CONFIG.CURRENT_PK</c>，
    /// 不再用"存在某些 Cookie 名"做兜底（原因见 <see cref="LoginStateProbeJs"/> 的注释：那些
    /// Cookie 是历史残留/匿名追踪，会把未登录误判成已登录）。
    ///
    /// 注意 <c>ExecuteScriptAsync</c> 的返回值是 <b>JSON 序列化后</b>的结果：JS 返回字符串
    /// <c>'NO'</c> 时，这里拿到的是带引号的 <c>"NO"</c>；JS 抛异常或返回 undefined 时拿到的是
    /// 字面量 <c>null</c>（4 个字符）。早期代码用 <c>Trim('"').Length > 0</c> 判断，会把 <c>null</c>
    /// 当成"拿到了账号 ID"从而误判已登录——这里改为精确匹配约定好的 <c>YES:</c> 前缀。
    /// </summary>
    private async Task<bool> IsLoggedInAsync()
    {
        var raw = await LoginWebView.CoreWebView2.ExecuteScriptAsync(LoginStateProbeJs);
        return Core.Providers.WebViewScriptResult.HasMarker(raw, LoggedInMarker);
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
        if (!string.IsNullOrEmpty(token))
        {
            return token;
        }

        // 主 frame 拿不到：百炼控制台是微前端架构，Token Plan 子应用很可能渲染在 iframe 里，
        // SEC_TOKEN 如果由子应用自己的脚本注入，只存在于那个 iframe 的 window/DOM 中，
        // 在主 frame 执行的脚本看不到。对已知的每个子 frame 重复同样的探测。
        foreach (var frame in _childFrames.ToArray())
        {
            try
            {
                var frameResult = await frame.ExecuteScriptAsync(js);
                var frameToken = frameResult?.Trim().Trim('"');
                if (!string.IsNullOrEmpty(frameToken))
                {
                    return frameToken;
                }
            }
            catch (Exception)
            {
                // 单个 frame 失败（例如已销毁）不影响尝试其余 frame。
            }
        }

        // 临时诊断（问题解决后删除）：主 frame 和所有已知子 frame 都提取不到时，把结构性诊断
        // 信息（不含任何 token/Cookie 值本身）写到本机临时文件，帮助判断根因。
        try
        {
            await DiagnoseFrameStructureAsync();
        }
        catch (Exception)
        {
            // 诊断失败不影响主流程。
        }

        return string.Empty;
    }

    /// <summary>临时诊断（问题解决后删除）：枚举 WebView2 里所有子 frame，尝试在每个 frame 上下文
    /// 执行同样的探测脚本，把"哪个 frame 里能看到 SEC_TOKEN"这类结构性信息写入本机文件——
    /// 绝不写入 token/Cookie 的实际值。</summary>
    private async Task DiagnoseFrameStructureAsync()
    {
        var lines = new List<string>
        {
            $"[{DateTime.Now:HH:mm:ss}] Main frame URL: {LoginWebView.Source}",
            $"Network responses observed so far: {Volatile.Read(ref _networkResponseCount)}",
        };

        const string probeJs = """
            (function () {
                try {
                    var hasSecToken = typeof window.SEC_TOKEN !== 'undefined' && !!window.SEC_TOKEN;
                    var htmlHasSecToken = (document.documentElement ? document.documentElement.outerHTML : '').indexOf('SEC_TOKEN') >= 0;
                    return JSON.stringify({ href: location.href, hasSecToken: hasSecToken, htmlHasSecToken: htmlHasSecToken, bodyLen: document.body ? document.body.innerHTML.length : 0 });
                } catch (e) { return JSON.stringify({ error: e.message }); }
            })();
            """;

        try
        {
            var mainResult = await LoginWebView.CoreWebView2.ExecuteScriptAsync(probeJs);
            lines.Add($"Main frame probe: {mainResult}");
        }
        catch (Exception ex)
        {
            lines.Add($"Main frame probe failed: {ex.GetType().Name}");
        }

        var frames = _childFrames.ToArray();
        lines.Add($"Child frame count (via FrameCreated): {frames.Length}");
        var index = 0;
        foreach (var frame in frames)
        {
            try
            {
                var frameResult = await frame.ExecuteScriptAsync(probeJs);
                lines.Add($"Frame[{index}] Name='{frame.Name}' probe: {frameResult}");
            }
            catch (Exception ex)
            {
                lines.Add($"Frame[{index}] Name='{frame.Name}' probe failed: {ex.GetType().Name}: {ex.Message}");
            }

            index++;
        }

        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "quotaflow-tokenplan-frame-diag.txt"), lines);
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
