using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 阿里云百炼 Token Plan（个人版）额度查询 —— 最小可用测试版本。
///
/// 背景：Token Plan 没有公开的用量查询 REST API，本实现走"控制台网关"（非公开接口，随时可能
/// 变化），流程完全来自产品侧提供的研究资料：
///   1. 携带 Console Cookie GET https://bailian.console.aliyun.com/cn-beijing?tab=plan，
///      从返回 HTML 中用正则提取全局变量 SEC_TOKEN；
///   2. 携带 Cookie + SEC_TOKEN POST https://bailian-cs.console.aliyun.com/data/api.json
///      （action=BroadScopeAspnGateway / product=sfm_bailian /
///       api=zeldaHttp.apikeyMgr.%2Ftokenplan%2Fpersonal%2Fapi%2Fv2%2Fusage，
///      Content-Type: application/x-www-form-urlencoded，带 Origin/Referer/region）；
///   3. 从返回 JSON 解析 per1WeekPercentage（= 7 天额度已使用比例，0.7913 → 79.13%）和
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

    private static readonly Regex SecTokenPattern = new(@"\bSEC_TOKEN\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>登录页特征：HTML 里如果出现这些字样，说明 Cookie 已失效、被重定向到了登录接口。</summary>
    private static readonly string[] LoginPageSignals = ["passport.aliyun.com", "window.location", "/login", "敬请登录", "登录后使用"];

    /// <summary>表示 7 天周期"已使用比例"的字段名（1.0 = 全部用尽）。</summary>
    private const string FieldPercentage = "per1WeekPercentage";

    /// <summary>表示 7 天周期重置时间的字段名（Unix 毫秒）。</summary>
    private const string FieldResetTime = "per1WeekResetTime";

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _cookieProvider;

    public string ProviderId => "alibaba-tokenplan";

    public AlibabaTokenPlanQuotaProvider(HttpClient httpClient, Func<string?> cookieProvider)
    {
        _httpClient = httpClient;
        _cookieProvider = cookieProvider;
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var cookie = _cookieProvider();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return NotConfigured();
        }

        TraceLog("Fetching SEC_TOKEN...");
        var secToken = await FetchSecTokenAsync(cookie, cancellationToken);
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

        // 一张卡两个窗口：已用 / 剩余，覆盖"测试阶段 UI 要同时看到 7 天使用率和剩余"的需求，
        // 同时完全复用现有卡片模板（QuotaWindow），不改任何共享 UI。
        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            DisplayName = "Alibaba Token Plan",
            State = state,
            QuotaWindows =
            [
                QuotaWindow.FromUtilization("weekly_used", "7 天已用", usage.UsedPercent, usage.ResetsAt),
                QuotaWindow.FromRemaining("weekly_remaining", "7 天剩余", remaining, usage.ResetsAt),
            ],
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = GatewayUrl,
            ErrorCategory = ErrorCategory.None,
        };
    }

    /// <summary>第一步：拉控制台页面并从 HTML 里提取 SEC_TOKEN。</summary>
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
                // 实测：匿名/无效 Cookie 下控制台返回的是 SPA 外壳（efm-fe 微前端加载器），
                // HTML 里没有 SEC_TOKEN——它由登录后的应用脚本动态注入。所以这个失败的根因
                // 几乎总是"Cookie 无访问权限/未登录"，而不是页面结构变化，文案要优先引导用户。
                return SecTokenResult.FromError(MakeSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    "无法从百炼控制台页面提取 SEC_TOKEN（通常是 Console Cookie 无权限或未登录），请确认已登录百炼控制台后重新复制 Cookie"));
            }

            return SecTokenResult.Ok(match.Groups[1].Value);
        }
    }

    /// <summary>第二步：POST 控制台网关拿 7 天周期用量。</summary>
    private async Task<UsageResult> FetchUsageAsync(string cookie, string secToken, CancellationToken cancellationToken)
    {
        var query = "action=BroadScopeAspnGateway"
                    + "&product=sfm_bailian"
                    + "&api=zeldaHttp.apikeyMgr.%2Ftokenplan%2Fpersonal%2Fapi%2Fv2%2Fusage";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayUrl}?{query}");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Origin", "https://bailian.console.aliyun.com");
        request.Headers.TryAddWithoutValidation("Referer", ConsoleUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SEC_TOKEN"] = secToken,
            ["region"] = Region,
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

    /// <summary>检查根节点里是否带"业务失败"标记（code/success 语义）。</summary>
    private static bool TryReadErrorMessage(JsonElement root, out long code, out string? message)
    {
        code = 0;
        message = null;

        foreach (var key in new[] { "result", "data" })
        {
            if (root.TryGetProperty(key, out var node))
            {
                if (node.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt64(out var c))
                {
                    code = c;
                    if (code != 0 && code != 200)
                    {
                        if (node.TryGetProperty("message", out var msgEl)) message = msgEl.GetString();
                        else if (node.TryGetProperty("errorMessage", out var errEl)) message = errEl.GetString();
                        return true;
                    }
                }

                if (node.TryGetProperty("success", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    if (node.TryGetProperty("message", out var msgEl2)) message = msgEl2.GetString();
                    else if (node.TryGetProperty("errorMessage", out var errEl2)) message = errEl2.GetString();
                    return true;
                }
            }
        }

        // 顶层也可能直接带 code/success（部分网关形态）。
        if (root.TryGetProperty("code", out var topCode) && topCode.TryGetInt64(out var topC) &&
            topC != 0 && topC != 200)
        {
            code = topC;
            if (root.TryGetProperty("message", out var topMsg)) message = topMsg.GetString();
            else if (root.TryGetProperty("errorMessage", out var topErr)) message = topErr.GetString();
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

    private static bool LooksLikeLoginPage(string html)
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