using QuotaFlow.Windows.Core.Assets;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Core.Services;
using QuotaFlow.Windows.PocConsole;

// 阶段 A 真实查询 POC 的命令行入口。
//
// 安全约定（对应文档 §7）：
// - 本程序绝不把 Token/API Key 打印到控制台或写入任何文件；
// - store-keys 只接受"文件路径"作为参数（文件路径本身不敏感），密钥内容从文件读取，
//   不通过命令行参数传递（避免明文出现在 shell 历史 / 进程参数列表里）；
// - check 命令只打印百分比、重置时间等展示层数据，出错信息也做了脱敏。

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var store = new SecureCredentialStore();

switch (args[0])
{
    case "store-keys":
        return StoreKeys(args, store);
    case "clear-keys":
        return ClearKeys(store);
    case "check":
        return await CheckAllAsync(store);
    case "diag-tokenplan":
        return await DiagTokenPlanAsync(store);
    case "diag-gateway":
        return await DiagGatewayAsync(store);
    case "clear-tokenplan":
        store.DeleteLarge("alibaba:tokenplan:consoleCookie");
        store.Delete("alibaba:tokenplan:secToken");
        Console.WriteLine("已清除百炼 Token Plan 登录态凭据");
        return 0;
    case "diag-tokenplan-html":
        return await DiagTokenPlanHtmlAsync(store);
    case "make-icon":
        return MakeIcon(args);
    case "preview-icon":
        return PreviewIcon(args);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("用法：");
    Console.WriteLine("  QuotaFlow.Windows.PocConsole store-keys <文件路径>   从文件读取 minimax=/deepseek= 两行并写入 Windows 凭据管理器");
    Console.WriteLine("  QuotaFlow.Windows.PocConsole clear-keys              清除已存储的 MiniMax / DeepSeek 凭据");
    Console.WriteLine("  QuotaFlow.Windows.PocConsole check                   对四个平台各查询一次并打印脱敏结果");
    Console.WriteLine("  QuotaFlow.Windows.PocConsole make-icon <输出路径>     生成多尺寸 app.ico");
}

static int PreviewIcon(string[] args)
{
    // 仅用于开发时肉眼检查设计，不参与产品运行时逻辑。
    if (args.Length < 2)
    {
        Console.WriteLine("缺少输出路径参数");
        return 1;
    }

    var size = args.Length >= 3 && int.TryParse(args[2], out var s) ? s : 256;
    using var bmp = AppIconRenderer.Render(size);
    // 小尺寸放大 8 倍导出，方便肉眼检查（原图太小看不清细节）。
    var scale = size <= 32 ? 8 : 1;
    using var scaled = scale > 1
        ? new System.Drawing.Bitmap(bmp, bmp.Width * scale, bmp.Height * scale)
        : bmp;
    scaled.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine($"已生成预览 PNG：{args[1]}");
    return 0;
}

static int MakeIcon(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("缺少输出路径参数");
        return 1;
    }

    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var images = sizes.Select(s => AppIconRenderer.Render(s)).ToList();

    try
    {
        var dir = Path.GetDirectoryName(args[1]);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        IcoEncoder.Save(args[1], images);
        Console.WriteLine($"已生成图标（{string.Join(",", sizes)}）：{args[1]}");
        return 0;
    }
    finally
    {
        foreach (var img in images)
        {
            img.Dispose();
        }
    }
}

static int StoreKeys(string[] args, SecureCredentialStore store)
{
    if (args.Length < 2)
    {
        Console.WriteLine("缺少文件路径参数");
        return 1;
    }

    var path = args[1];
    if (!File.Exists(path))
    {
        Console.WriteLine("文件不存在");
        return 1;
    }

    var lines = File.ReadAllLines(path);
    var stored = new List<string>();

    foreach (var line in lines)
    {
        var idx = line.IndexOf('=');
        if (idx <= 0)
        {
            continue;
        }

        var providerId = line[..idx].Trim().ToLowerInvariant();
        var secret = line[(idx + 1)..].Trim();
        if (secret.Length == 0)
        {
            continue;
        }

        var key = providerId switch
        {
            "minimax" => "minimax:ApiKey",
            "deepseek" => "deepseek:ApiKey",
            _ => null,
        };

        if (key is null)
        {
            Console.WriteLine($"跳过未知 provider: {providerId}");
            continue;
        }

        store.Save(key, secret);
        stored.Add($"{providerId} (len={secret.Length}, tail=***{secret[^Math.Min(4, secret.Length)..]})");
    }

    Console.WriteLine($"已写入 Windows 凭据管理器: {string.Join(", ", stored)}");
    return 0;
}

static int ClearKeys(SecureCredentialStore store)
{
    store.Delete("minimax:ApiKey");
    store.Delete("deepseek:ApiKey");
    Console.WriteLine("已清除 MiniMax / DeepSeek 凭据");
    return 0;
}

static async Task<int> DiagTokenPlanAsync(SecureCredentialStore store)
{
    // 临时诊断命令：读真实已保存的百炼登录态（App 使用的凭据键名），打印状态与非敏感诊断信息
    // （Cookie 分片数/合计长度、SEC_TOKEN 是否存在），绝不打印 Cookie/SEC_TOKEN 内容本身。
    const string cookieKey = "alibaba:tokenplan:consoleCookie";
    const string secTokenKey = "alibaba:tokenplan:secToken";

    var cookie = store.TryReadLarge(cookieKey);
    var secToken = store.TryRead(secTokenKey, useUtf8: true);

    Console.WriteLine($"Cookie present: {cookie is not null}, length: {cookie?.Length ?? 0}, pair count: {cookie?.Split(';', StringSplitOptions.RemoveEmptyEntries).Length ?? 0}");
    Console.WriteLine($"SEC_TOKEN present: {!string.IsNullOrEmpty(secToken)}, length: {secToken?.Length ?? 0}");

    if (cookie is not null)
    {
        // 只打印 Cookie 名字列表（不含值），检查 SEC_TOKEN 是否其实是一个 Cookie 名而不是页面变量。
        var names = cookie.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)[0].Trim())
            .ToList();
        Console.WriteLine($"Cookie names: {string.Join(", ", names)}");
        Console.WriteLine($"Any cookie name contains 'SEC_TOKEN' (case-insensitive): {names.Any(n => n.Contains("SEC_TOKEN", StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine($"Any cookie name contains 'token' (case-insensitive): {names.Any(n => n.Contains("token", StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine($"Any cookie name contains 'csrf' (case-insensitive): {names.Any(n => n.Contains("csrf", StringComparison.OrdinalIgnoreCase))}");
        Console.WriteLine($"Any cookie name contains 'xsrf' (case-insensitive): {names.Any(n => n.Contains("xsrf", StringComparison.OrdinalIgnoreCase))}");
    }

    if (cookie is null)
    {
        Console.WriteLine("未配置：请先在应用设置页完成一键登录。");
        return 1;
    }

    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(20);
    var provider = new AlibabaTokenPlanQuotaProvider(httpClient, () => cookie, () => secToken);

    Console.WriteLine("=== alibaba-tokenplan ===");
    var snapshot = await provider.GetSnapshotAsync();
    PrintSnapshot(snapshot);
    return 0;
}

static async Task<int> DiagGatewayAsync(SecureCredentialStore store)
{
    // 临时诊断：网关返回 302，试几种请求头/参数组合，定位缺了什么。
    // 只打印状态码、重定向目标、响应体前若干字符（不含 Cookie/SEC_TOKEN 值本身）。
    var cookie = store.TryReadLarge("alibaba:tokenplan:consoleCookie");
    var secToken = store.TryRead("alibaba:tokenplan:secToken", useUtf8: true);
    if (cookie is null || string.IsNullOrEmpty(secToken))
    {
        Console.WriteLine("未配置登录态，请先在应用里完成一键登录。");
        return 1;
    }

    const string gatewayUrl = "https://bailian-cs.console.aliyun.com/data/api.json";
    const string query = "action=BroadScopeAspnGateway&product=sfm_bailian&api=zeldaHttp.apikeyMgr.%2Ftokenplan%2Fpersonal%2Fapi%2Fv2%2Fusage";
    const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36 Edg/151.0.0.0";
    const string consoleUrl = "https://bailian.console.aliyun.com/cn-beijing?tab=plan";

    // 不自动跟随重定向，才能看到 302 本身和 Location。
    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

    async Task Attempt(string label, Action<HttpRequestMessage> configure)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{gatewayUrl}?{query}");
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        configure(req);
        try
        {
            using var resp = await http.SendAsync(req);
            var location = resp.Headers.Location?.ToString() ?? "(none)";
            var body = await resp.Content.ReadAsStringAsync();
            var preview = body.Length > 160 ? body[..160] : body;
            preview = preview.Replace('\n', ' ').Replace('\r', ' ');
            Console.WriteLine($"[{label}] HTTP {(int)resp.StatusCode} | Location: {location} | BodyLen: {body.Length}");
            Console.WriteLine($"    body preview: {preview}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{label}] EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A：当前实现（Origin + Referer + form body 里带 SEC_TOKEN）
    await Attempt("A current", req =>
    {
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken, ["region"] = "cn-beijing",
        });
    });

    // B：A + 浏览器 UA + XHR 标记（网关常据此判断是否合法浏览器 XHR）
    await Attempt("B +UA +XHR", req =>
    {
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken, ["region"] = "cn-beijing",
        });
    });

    // C：B + SEC_TOKEN 同时放到请求头（很多阿里云控制台网关是从 header 读的）
    await Attempt("C +SEC_TOKEN header", req =>
    {
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("sec-token", secToken);
        req.Headers.TryAddWithoutValidation("x-xsrf-token", secToken);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken, ["region"] = "cn-beijing",
        });
    });

    // E：对照组——故意用一个不存在的 action。如果它和上面返回完全一样的 302，
    // 说明请求在到达业务逻辑前就被网关/路由层拒了，跟 SEC_TOKEN 和请求头都无关。
    await Attempt("E bogus action (control)", req =>
    {
        req.RequestUri = new Uri($"{gatewayUrl}?action=NoSuchActionForDiagnosis&product=sfm_bailian");
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["region"] = "cn-beijing" });
    });

    // F：对照组——完全不带 Cookie 访问同一个真实接口。若与 A 结果一致，
    // 说明服务端根本没读到我们的登录态（例如域名/路径不对）。
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{gatewayUrl}?{query}");
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken, ["region"] = "cn-beijing",
        });
        try
        {
            using var resp = await http.SendAsync(req);
            var location = resp.Headers.Location?.ToString() ?? "(none)";
            Console.WriteLine($"[F no-cookie (control)] HTTP {(int)resp.StatusCode} | Location: {location}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[F no-cookie (control)] EXCEPTION {ex.GetType().Name}");
        }
    }

    // D：C + SEC_TOKEN 也放进 query string（部分网关从 query 读）
    await Attempt("D +SEC_TOKEN query", req =>
    {
        req.RequestUri = new Uri($"{gatewayUrl}?{query}&SEC_TOKEN={Uri.EscapeDataString(secToken)}");
        req.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        req.Headers.TryAddWithoutValidation("Referer", consoleUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("sec-token", secToken);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken, ["region"] = "cn-beijing",
        });
    });

    return 0;
}

static async Task<int> DiagTokenPlanHtmlAsync(SecureCredentialStore store)
{
    // 临时诊断：直接拉一次控制台页面，检查 SEC_TOKEN 到底在不在返回的 HTML 里，以及页面是不是
    // 真的处于登录态（是否包含登录后才有的特征）。绝不打印 Cookie/HTML 全文/任何 token 值本身，
    // 只打印结构性诊断信息（长度、是否包含某关键字、关键字周围的字符类别统计）。
    const string cookieKey = "alibaba:tokenplan:consoleCookie";
    var cookie = store.TryReadLarge(cookieKey);
    if (cookie is null)
    {
        Console.WriteLine("未配置。");
        return 1;
    }

    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(20);
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://bailian.console.aliyun.com/cn-beijing?tab=plan");
    request.Headers.TryAddWithoutValidation("Cookie", cookie);
    request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36 Edg/120.0");

    var response = await httpClient.SendAsync(request);
    var html = await response.Content.ReadAsStringAsync();

    Console.WriteLine($"HTTP status: {(int)response.StatusCode}");
    Console.WriteLine($"HTML length: {html.Length}");
    Console.WriteLine($"Contains 'SEC_TOKEN': {html.Contains("SEC_TOKEN")}");
    Console.WriteLine($"Contains 'CURRENT_PK': {html.Contains("CURRENT_PK")}");
    Console.WriteLine($"Contains 'ALIYUN_CONSOLE_CONFIG': {html.Contains("ALIYUN_CONSOLE_CONFIG")}");
    Console.WriteLine($"Contains 'passport.aliyun.com': {html.Contains("passport.aliyun.com")}");
    Console.WriteLine($"Contains '暂不支持移动端': {html.Contains("暂不支持移动端")}");
    Console.WriteLine($"Contains '请登录' or '登录后使用': {html.Contains("请登录") || html.Contains("登录后使用")}");
    Console.WriteLine($"Contains 'window.location': {html.Contains("window.location")}");
    Console.WriteLine($"Contains 'passport.aliyun.com': {html.Contains("passport.aliyun.com", StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine($"Contains '/login': {html.Contains("/login", StringComparison.OrdinalIgnoreCase)}");
    Console.WriteLine($"Contains '敬请登录': {html.Contains("敬请登录")}");
    Console.WriteLine($"Contains '登录后使用': {html.Contains("登录后使用")}");

    // 不打印 SEC_TOKEN 附近的原始文本（可能截到真实 token 片段）；只报告结构性判断：
    // 出现次数、以及是否匹配 Provider 里用的那个正则模式。
    var occurrences = System.Text.RegularExpressions.Regex.Matches(html, "SEC_TOKEN").Count;
    var regexMatches = System.Text.RegularExpressions.Regex.Matches(html, @"\bSEC_TOKEN\s*:\s*""([^""]+)""").Count;
    Console.WriteLine($"'SEC_TOKEN' occurrence count: {occurrences}");
    Console.WriteLine($"Provider regex (\\bSEC_TOKEN\\s*:\\s*\"...\") match count: {regexMatches}");

    return 0;
}

static async Task<int> CheckAllAsync(SecureCredentialStore store)
{
    using var httpClient = new HttpClient();
    httpClient.Timeout = TimeSpan.FromSeconds(20);

    IQuotaProvider[] providers =
    [
        new ClaudeQuotaProvider(httpClient),
        new CodexQuotaProvider(httpClient),
        new MiniMaxQuotaProvider(httpClient, () => store.TryRead("minimax:ApiKey"), MiniMaxQuotaProvider.DomainCn),
        new DeepSeekBalanceProvider(httpClient, () => store.TryRead("deepseek:ApiKey")),
    ];

    foreach (var provider in providers)
    {
        Console.WriteLine($"=== {provider.ProviderId} ===");
        try
        {
            var snapshot = await provider.GetSnapshotAsync();
            PrintSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            // Provider 承诺不抛未处理异常；真走到这里说明实现有缺口，需要修，但绝不能因为
            // 一个平台异常就让其余平台的查询也中断——外层已经用 foreach 顺序隔离。
            Console.WriteLine($"[意外异常，未被 Provider 内部捕获] {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    return 0;
}

static void PrintSnapshot(ProviderSnapshot snapshot)
{
    Console.WriteLine($"状态: {snapshot.State}  错误分类: {snapshot.ErrorCategory}");
    if (snapshot.UserGuidance is not null)
    {
        Console.WriteLine($"引导文案: {snapshot.UserGuidance}");
    }

    foreach (var window in snapshot.QuotaWindows)
    {
        var reset = window.ResetsAt is { } r ? r.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(无重置时间)";
        Console.WriteLine($"  [{window.Id}] {window.DisplayName}: 剩余 {window.RemainingPercent:F1}% / 已用 {window.UsedPercent:F1}%  重置于 {reset}");
    }

    if (snapshot.Balance is { } balance)
    {
        Console.WriteLine($"  余额: {balance.Amount} {balance.Currency}" +
                           (balance.GrantedAmount is { } g ? $"（赠送 {g}）" : "") +
                           (balance.ToppedUpAmount is { } t ? $"（充值 {t}）" : ""));
    }

    Console.WriteLine($"数据来源: {snapshot.DataSource}");
}
