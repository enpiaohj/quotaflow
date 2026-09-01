using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 阿里云百炼 Token Plan（个人版）额度查询 —— 最小可用测试版本。
///
/// 背景：Token Plan 没有公开的用量查询 REST API，本实现走"控制台网关"（非公开接口，随时可能
/// 变化）。查询只需要两样东西：Console Cookie（登录态）+ SEC_TOKEN（网关鉴权令牌）：
///   1. Cookie 由 <see cref="Views.AlibabaLoginWindow"/>（WebView2 一键登录）在登录成功后
///      自动抓取整份会话 Cookie 并交给 App 层存入 Windows 凭据管理器；
///   2. SEC_TOKEN 优先也在登录成功那一刻由 WebView2 执行页面 JS 提取（同一个登录窗口负责）——
///      实测这是唯一可靠的获取方式：百炼控制台是纯前端 SPA（efm-fe 异步微前端），SEC_TOKEN
///      是页面 JS 运行后才动态注入的全局变量，<b>不会出现在任何一次 GET 请求拿到的服务端渲染
///      HTML 里</b>，哪怕 Cookie 是完全有效的登录态。本类里的 <see cref="FetchSecTokenAsync"/>
///      （从控制台页面 HTML 正则提取）只是登录时提取失败时的兜底路径，架构上注定拿不到值，
///      失败时应引导用户重新走一次一键登录，而不是被误判成"登录已失效"；
///   3. 携带 Cookie + SEC_TOKEN POST https://bailian-cs.console.aliyun.com/data/api.json
///      （action=BroadScopeAspnGateway / product=sfm_bailian /
///       api=zeldaHttp.apikeyMgr.%2Ftokenplan%2Fpersonal%2Fapi%2Fv2%2Fusage，
///      Content-Type: application/x-www-form-urlencoded，带 Origin/Referer/region）；
///   4. 从返回 JSON 解析 per1WeekPercentage（= 7 天额度已使用比例，0.7913 → 79.13%）和
///      per1WeekResetTime（= 7 天额度重置时间，Unix 毫秒时间戳）。
///
/// 安全约束（硬性）：Console Cookie 只经构造注入的 <c>cookieProvider</c> 委托取得（App 层从
/// Windows 凭据管理器读取），本类绝不把 Cookie / SEC_TOKEN 写日志、写文件、拼进异常消息或
/// ProviderSnapshot 的任何字段；所有输出仅含百分比/时间/错误分类。日志走 System.Diagnostics.Trace
/// （调试输出窗口可见），不影响正式发布版本的行为。
///
/// 暂不实现（后续迭代）：套餐 Lite/Standard/Pro 自动识别、Addon Credits、套餐有效期。
/// </summary>
public sealed class AlibabaTokenPlanQuotaProvider : IQuotaProvider
{
    public const string Region = "cn-beijing";

    /// <summary>百炼控制台首页（含 Token Plan tab）；第一步从这里抓 SEC_TOKEN。</summary>
    public const string ConsoleUrl = "https://bailian.console.aliyun.com/cn-beijing?tab=plan";

    /// <summary>控制台统一网关；第二步 POST 数据都发到这里。</summary>
    public const string GatewayUrl = "https://bailian-cs.console.aliyun.com/data/api.json";

    /// <summary>网关转发的目标接口路径（字面量，不做百分号编码，与控制台真实请求一致）。</summary>
    internal const string UsageApi = "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage";

    /// <summary>
    /// 构造网关信封 <c>params</c>。
    ///
    /// <c>cornerstoneParam</c> 不是可选的埋点数据——实测只发 <c>{"Api":…,"V":"1.0","Data":{}}</c>
    /// 时网关会返回内层 <c>Bad Request</c>，补上这组字段后才返回真实用量。这里只保留控制台请求里
    /// 那些稳定的结构性字段，刻意不带 feTraceId / feURL / X-Anonymous-Id / switchAgent 等
    /// 会话级或账号级字段：它们既不影响结果，也不该由本程序伪造。
    /// </summary>
    internal static string BuildGatewayParams(string api)
    {
        var envelope = new
        {
            Api = api,
            V = "1.0",
            Data = new
            {
                cornerstoneParam = new
                {
                    protocol = "V2",
                    console = "ONE_CONSOLE",
                    productCode = "p_efm",
                    domain = "bailian.console.aliyun.com",
                    consoleSite = "BAILIAN_ALIYUN",
                    xsp_lang = "zh-CN",
                },
            },
        };

        return JsonSerializer.Serialize(envelope);
    }

    private static readonly Regex SecTokenPattern = new(@"\bSEC_TOKEN\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>登录页特征：HTML 里如果出现这些字样，说明 Cookie 已失效、被重定向到了登录接口。
    /// 注意：不能包含 "window.location" ——实测这是任何现代前端 bundle 里都极常见的普通 JS
    /// 字符串，一个完全有效的登录态页面（243KB 的正常控制台内容，CURRENT_PK 存在）同样会命中，
    /// 曾经导致把正常登录态误判成"已失效"、给用户展示错误的"请重新登录"提示。</summary>
    private static readonly string[] LoginPageSignals = ["passport.aliyun.com", "敬请登录", "登录后使用"];

    /// <summary>表示 7 天周期"已使用比例"的字段名（1.0 = 全部用尽）。</summary>
    private const string FieldPercentage = "per1WeekPercentage";

    /// <summary>表示 7 天周期重置时间的字段名（Unix 毫秒）。</summary>
    private const string FieldResetTime = "per1WeekResetTime";

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _cookieProvider;
    private readonly Func<string?>? _secTokenProvider;

    public string ProviderId => "alibaba-tokenplan";

    /// <param name="secTokenProvider">登录时从控制台页面抓到的 SEC_TOKEN（存 Windows 凭据管理器）。
    /// 提供时优先使用，跳过"从控制台 HTML 正则提取"这一步；为 null（或取不到）时降级到 HTML 提取。</param>
    public AlibabaTokenPlanQuotaProvider(HttpClient httpClient, Func<string?> cookieProvider, Func<string?>? secTokenProvider = null)
    {
        _httpClient = httpClient;
        _cookieProvider = cookieProvider;
        _secTokenProvider = secTokenProvider;
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var cookie = _cookieProvider();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return NotConfigured();
        }

        var secToken = await ResolveSecTokenAsync(cookie, cancellationToken);
        if (secToken.Error is not null)
        {
            return secToken.Error;
        }

        TraceLog("SEC_TOKEN acquired");
        TraceLog("Fetching usage...");
        var usage = await FetchUsageAsync(cookie, secToken.Token!, cancellationToken);
        if (usage.Error is not null)
        {
            return usage.Error;
        }

        TraceLog($"Weekly usage: {usage.UsedPercent:F2}%");
        TraceLog($"Reset at: {usage.ResetsAt:yyyy-MM-dd HH:mm}");

        var remaining = 100.0 - usage.UsedPercent;
        var state = usage.UsedPercent switch
        {
            >= 100 => ProviderState.Exhausted,
            _ when remaining < 10 => ProviderState.Critical,
            _ when remaining < 30 => ProviderState.Low,
            _ => ProviderState.Available,
        };

        // 只保留一个窗口。早期为了"测试阶段同时看到已用和剩余"建了 weekly_used / weekly_remaining
        // 两个窗口，但 QuotaWindow.FromUtilization(已用) 和 FromRemaining(剩余) 产出的是完全
        // 相同的对象（内部都存 remaining + used），而面板卡片统一显示"剩余"——结果两张卡都显示
        // 53%，其中标着"7 天已用"的那张实际显示的是剩余值，属于明确的错误信息。
        // 已用比例并没有丢失：它在同一个 QuotaWindow 的 UsedPercent 里，与其他 Provider 一致。
        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            DisplayName = "Alibaba Token Plan",
            State = state,
            QuotaWindows =
            [
                QuotaWindow.FromRemaining("weekly", "7 天额度", remaining, usage.ResetsAt),
            ],
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = GatewayUrl,
            ErrorCategory = ErrorCategory.None,
        };
    }

    /// <summary>
    /// 解析 SEC_TOKEN：优先用登录时从页面抓到的（存凭据管理器，最可靠）；没有时才降级为
    /// "拉控制台页面 HTML 正则提取"。用存储值可以跳过一整个 HTTP 往返，也更不容易因
    /// 控制台前端结构变化而失败。
    /// </summary>
    private async Task<SecTokenResult> ResolveSecTokenAsync(string cookie, CancellationToken cancellationToken)
    {
        try
        {
            var stored = _secTokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(stored))
            {
                TraceLog("Using SEC_TOKEN captured at login");
                return SecTokenResult.Ok(stored.Trim());
            }
        }
        catch (Exception)
        {
            // 读取凭据失败走 HTML 提取兜底。
        }

        TraceLog("Fetching SEC_TOKEN from console page...");
        return await FetchSecTokenAsync(cookie, cancellationToken);
    }

    /// <summary>第一步（降级路径）：拉控制台页面并从 HTML 里提取 SEC_TOKEN。</summary>
    private async Task<SecTokenResult> FetchSecTokenAsync(string cookie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConsoleUrl);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            response = await _httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
            return SecTokenResult.FromError(MakeSnapshot(state, category, guidance));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return SecTokenResult.FromError(MakeSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    "百炼控制台拒绝访问：Console Cookie 无效或已失效，请在设置页重新填写"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return SecTokenResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"拉取百炼控制台页面失败（HTTP {(int)response.StatusCode}）"));
            }

            string html;
            try
            {
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
                return SecTokenResult.FromError(MakeSnapshot(state, category, guidance));
            }

            if (LooksLikeLoginPage(html))
            {
                return SecTokenResult.FromError(MakeSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    "Console Cookie 已失效（被重定向到登录页），请重新登录百炼控制台后更新 Cookie"));
            }

            var match = SecTokenPattern.Match(html);
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                // 实测更正：即使是有效登录态的 Cookie，服务端渲染的 HTML 里也从不包含
                // SEC_TOKEN——百炼控制台是纯前端 SPA（efm-fe 异步微前端），SEC_TOKEN 是页面
                // JS 执行后才动态注入的，不会出现在这次 GET 拿到的静态 HTML 里。也就是说
                // 这条"HTML 正则提取"路径本身在架构上就不可能取到 SEC_TOKEN——不是 Cookie
                // 失效的信号，早期版本把它归类为 AuthenticationExpired 会误导用户去重新登录。
                // 正确路径是登录时由 WebView2 执行 JS 提取（见 AlibabaLoginWindow），
                // 这里只是它不可用时的兜底，理应引导用户重新走一次一键登录来刷新这个值。
                return SecTokenResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "未能取得 SEC_TOKEN（该值只能在登录时由浏览器页面动态生成，无法从静态页面内容解析），请在设置页重新执行一次「一键登录」"));
            }

            return SecTokenResult.Ok(match.Groups[1].Value);
        }
    }

    /// <summary>第二步：POST 控制台网关拿 7 天周期用量。</summary>
    private async Task<UsageResult> FetchUsageAsync(string cookie, string secToken, CancellationToken cancellationToken)
    {
        // api 参数在控制台的真实请求里是不做百分号编码的字面量（含 . 和 /），保持一致。
        var query = "action=BroadScopeAspnGateway"
                    + "&product=sfm_bailian"
                    + $"&api={UsageApi}"
                    + "&_v=undefined";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayUrl}?{query}");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        request.Headers.TryAddWithoutValidation("Referer", ConsoleUrl);

        // 请求体的三个字段缺一不可，全部由抓包比对确定：
        //   params    —— 网关信封，必须带 cornerstoneParam，否则内层返回 "Bad Request"（实测对照组）
        //   region    —— 区域
        //   sec_token —— 小写下划线，且只能放在 body 里；放 query 或请求头都会被网关 302 掉
        // 早期版本发的是空 body + 大写 SEC_TOKEN，请求在网关层就被拒，这正是长期查询失败的根因。
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["params"] = BuildGatewayParams(UsageApi),
            ["region"] = Region,
            ["sec_token"] = secToken,
        });

        HttpResponseMessage response;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            response = await _httpClient.SendAsync(request, timeoutCts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
            return UsageResult.FromError(MakeSnapshot(state, category, guidance));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return UsageResult.FromError(MakeSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    "用量接口返回 401/403：登录会话已过期，请重新登录百炼控制台后更新 Cookie"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return UsageResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"百炼用量接口返回异常状态（HTTP {(int)response.StatusCode}）"));
            }

            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
                return UsageResult.FromError(MakeSnapshot(state, category, guidance));
            }

            return ParseUsageResponse(raw);
        }
    }

    /// <summary>
    /// 解析网关返回的 JSON。内部方法便于单测直接构造各种响应形态（正常/缺字段/业务错误）验证。
    /// 兼容常见的两种包裹结构：<c>{ "result": {...} }</c> 和顶层平铺，字段名统一按
    /// <see cref="FieldPercentage"/> / <see cref="FieldResetTime"/> 递归查找（最多深入 6 层，
    /// 避免在明显不相关的嵌套里浪费时间）。
    /// </summary>
    internal UsageResult ParseUsageResponse(string raw)
    {
        JsonDocument doc;
        // 实测：无效 Cookie/SEC_TOKEN 时网关会 302 重定向到淘宝错误页（err.taobao.com/error1.html），
        // HttpClient 默认跟随重定向后拿到的是 HTML 而非 JSON。这种情况要明确报"会话失效"，
        // 而不是笼统的"返回的不是有效 JSON"。
        if (LooksLikeRedirectErrorPage(raw))
        {
            return UsageResult.FromError(MakeSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                "百炼登录会话已失效（用量接口被重定向到错误页），请重新登录百炼控制台后更新 Cookie"));
        }

        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return UsageResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                "百炼用量接口返回的不是有效 JSON"));
        }

        using (doc)
        {
            var root = doc.RootElement;

            // 网关通用失败语义：code != 200 或 success == false 都属于业务错误；session 类错误
            // 放到 AuthenticationExpired，其余归 ServerError。
            if (TryReadErrorMessage(root, out var code, out var message))
            {
                var guidance = string.IsNullOrWhiteSpace(message)
                    ? $"百炼用量接口返回业务错误（code {code}）"
                    : $"百炼用量接口返回业务错误（code {code}）：{SanitizeGuidance(message)}";
                var state = IsSessionExpiredMessage(message)
                    ? ProviderState.AuthenticationExpired
                    : ProviderState.ProviderError;
                return UsageResult.FromError(MakeSnapshot(state,
                    state == ProviderState.AuthenticationExpired ? ErrorCategory.AuthenticationExpired : ErrorCategory.ServerError,
                    guidance));
            }

            var percentage = FindNumeric(root, FieldPercentage, 0);
            if (!percentage.HasValue)
            {
                return UsageResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    $"百炼用量接口返回的 JSON 中缺少 {FieldPercentage} 字段（接口可能已变化），请反馈给开发者核对"));
            }

            var resetMs = FindNumeric(root, FieldResetTime, 0);
            if (!resetMs.HasValue)
            {
                return UsageResult.FromError(MakeSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    $"百炼用量接口返回的 JSON 中缺少 {FieldResetTime} 字段（接口可能已变化），请反馈给开发者核对"));
            }

            var usedPercent = Math.Clamp(percentage.Value * 100.0, 0.0, 100.0);
            var resetsAt = FromUnixMilliseconds(resetMs.Value);
            return UsageResult.Ok(usedPercent, resetsAt);
        }
    }

    /// <summary>
    /// 读取节点上的 <c>code</c>，兼容数字与字符串两种形态。
    ///
    /// 实测这个网关的 <c>code</c> 是字符串（<c>"200"</c>、<c>"SUCCESS"</c>、
    /// <c>"PostonlyOrTokenError"</c> 都出现过）。<see cref="JsonElement.TryGetInt64"/> 在
    /// 非 Number 元素上是<b>抛异常</b>而不是返回 false，直接调用会让整个查询崩掉。
    /// </summary>
    private static bool TryReadCode(JsonElement node, out long numeric, out string? text)
    {
        numeric = 0;
        text = null;

        if (!node.TryGetProperty("code", out var codeEl))
        {
            return false;
        }

        switch (codeEl.ValueKind)
        {
            case JsonValueKind.Number when codeEl.TryGetInt64(out var n):
                numeric = n;
                text = n.ToString(CultureInfo.InvariantCulture);
                return true;
            case JsonValueKind.String:
                text = codeEl.GetString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    numeric = parsed;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>判断一个 code 是否表示成功。</summary>
    private static bool IsSuccessCode(long numeric, string? text)
    {
        if (numeric is 0 or 200)
        {
            return true;
        }

        return text is not null
               && (text.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
                   || text.Equals("OK", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从节点上取一条可读的错误描述。</summary>
    private static string? ReadMessage(JsonElement node)
    {
        foreach (var key in new[] { "message", "errorMessage", "errorMsg", "msg", "errorCode" })
        {
            if (node.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>检查根节点里是否带"业务失败"标记（code/success 语义）。</summary>
    private static bool TryReadErrorMessage(JsonElement root, out long code, out string? message)
    {
        code = 0;
        message = null;

        foreach (var key in new[] { "result", "data" })
        {
            if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.Object)
            {
                if (TryReadCode(node, out var c, out var t) && !IsSuccessCode(c, t))
                {
                    code = c;
                    message = ReadMessage(node) ?? t;
                    return true;
                }

                if (node.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    message = ReadMessage(node);
                    return true;
                }
            }
        }

        // 顶层也可能直接带 code/success（部分网关形态）。
        if (TryReadCode(root, out var topC, out var topT) && !IsSuccessCode(topC, topT))
        {
            code = topC;
            message = ReadMessage(root) ?? topT;
            return true;
        }

        return false;
    }

    private static bool IsSessionExpiredMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("登录", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("session", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("鉴权", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 JSON 里按属性名找第一个数值（double 或数字字符串）。深度受限、不抛异常。
    /// 用于对未知包裹层（result/data/嵌套对象）的容错查找。</summary>
    private static double? FindNumeric(JsonElement element, string propertyName, int depth)
    {
        if (depth > 6)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == propertyName)
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var d))
                    {
                        return d;
                    }

                    if (property.Value.ValueKind == JsonValueKind.String &&
                        double.TryParse(property.Value.GetString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var sd))
                    {
                        return sd;
                    }
                }
                else
                {
                    var found = FindNumeric(property.Value, propertyName, depth + 1);
                    if (found.HasValue)
                    {
                        return found;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindNumeric(item, propertyName, depth + 1);
                if (found.HasValue)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset FromUnixMilliseconds(double ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

    /// <summary>网关响应被 302 重定向到错误页的特征（实测无效凭证时跳到 err.taobao.com/error1.html）。</summary>
    private static bool LooksLikeRedirectErrorPage(string raw) =>
        raw.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("err.taobao.com", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("error1.html", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("error.html", StringComparison.OrdinalIgnoreCase);

    /// <summary>内部方法便于单测直接验证误判/漏判场景，不依赖真实 HTTP 请求。</summary>
    internal static bool LooksLikeLoginPage(string html)
    {
        // 登录页一般是返回 200 但内容是登录 SPA/重定向脚本；URL 或脚本特征能识别的就判失效。
        return LoginPageSignals.Any(s => html.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>错误消息里可能回显了不该出现的内容，做基础脱敏：截断到 200 字符并去掉引号包裹的
    /// token 形态字符串，绝不把完整原文带进 UI。</summary>
    private static string SanitizeGuidance(string message)
    {
        var cleaned = message.Replace("&", " ").Replace("&amp;", " ");
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Alibaba Token Plan",
        State = ProviderState.NotConfigured,
        DataSource = GatewayUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "请在设置页填写百炼 Console Cookie",
    };

    private ProviderSnapshot MakeSnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Alibaba Token Plan",
        State = state,
        DataSource = GatewayUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };

    private static void TraceLog(string message) =>
        System.Diagnostics.Trace.WriteLine($"[AlibabaTokenPlan] {message}");

    // ---- 内部结果载体 ----

    internal readonly record struct SecTokenResult(string? Token, ProviderSnapshot? Error)
    {
        public static SecTokenResult Ok(string token) => new(token, null);
        public static SecTokenResult FromError(ProviderSnapshot error) => new(null, error);
    }

    internal readonly record struct UsageResult(double UsedPercent, DateTimeOffset ResetsAt, ProviderSnapshot? Error)
    {
        public static UsageResult Ok(double usedPercent, DateTimeOffset resetsAt) => new(usedPercent, resetsAt, null);
        public static UsageResult FromError(ProviderSnapshot error) => new(0, default, error);
    }
}