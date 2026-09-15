using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 火山方舟 Coding Plan（个人版）额度查询，架构上同时预留 Agent Plan。
///
/// 原生 Provider，不是"自定义平台"的一个配置项——火山方舟额度查询不是普通 Bearer Token +
/// JSON API，而是火山引擎<b>控制面 OpenAPI</b>（<c>open.volcengineapi.com</c>），需要
/// AccessKey ID / Secret Access Key 做 <see cref="VolcengineSigner"/> 动态签名，
/// 自定义平台架构（<c>CustomPlatformProvider</c>）目前只支持 Bearer Token / 自定义请求头。
///
/// 接口依据（该控制面网关没有面向个人开发者的公开文档，依据两份独立开源实现交叉核对，
/// 详见 <see cref="VolcengineSigner"/> 类型文档）：
///   1. Action：<c>GetCodingPlanUsage</c>（主功能）；账号未订阅 Coding Plan（返回空用量数组）
///      时回退查询 <c>GetAFPUsage</c>（Agent Plan）——与账号真实只会订阅其中一种套餐的情况一致，
///      两者互斥探测，不并发合并展示（没有证据表明一个账号会同时持有两种套餐）。
///   2. Coding Plan 响应 <c>Result.QuotaUsage</c> 是数组，每项含 <c>Level</c>
///      （<c>session</c>/<c>weekly</c>/<c>monthly</c>）/ <c>Percent</c>（<b>已使用</b>百分比，
///      0~100）/ <c>ResetTime</c>；<b>按 Level 字段匹配三个窗口，不按数组下标</b>，防止服务端
///      调整顺序后错位。
///   3. Agent Plan 响应把三档分别放在 <c>Result.AFPFiveHour</c> / <c>AFPWeekly</c> /
///      <c>AFPMonthly</c> 三个对象里，各含 <c>Used</c> / <c>Quota</c>（绝对值，单位未公开确认，
///      不当作展示用的绝对额度）和 <c>ResetTime</c>。
///   4. 两个接口的 <c>ResetTime</c> 单位不统一：Coding Plan 是 Unix 秒，Agent Plan 是 Unix
///      毫秒——这不是本实现的假设，两份参考实现都独立发现并处理了这一差异。用数值量级区分
///      （见 <see cref="ParseResetEpoch"/>），不按接口来源写死单位。
///   5. 套餐类型（Lite/Pro）<b>在这两个接口的响应里都不存在</b>，无法从 API 自动识别——
///      本实现不猜测、不显示 Pro/Lite 字样。
///
/// 安全约束：Secret Access Key 只经构造注入的委托取得（Windows 凭据管理器），本类绝不把
/// AccessKey/SecretKey 写日志、写异常消息或 <see cref="ProviderSnapshot"/> 的任何字段；
/// 错误信息里出现的 Access Key（脱敏后）只保留前 8 位 + 末 4 位，与其它平台的引导文案一致，
/// 不含足以复原凭据的信息。
/// </summary>
public sealed class VolcengineArkProvider : IQuotaProvider
{
    public const string DisplayNameConst = "火山方舟 Coding Plan";

    private static readonly string[] KnownLevels = ["session", "weekly", "monthly"];

    private static readonly Dictionary<string, string> LevelDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["session"] = "5 小时",
        ["weekly"] = "本周",
        ["monthly"] = "本月",
    };

    /// <summary>Unix 秒/毫秒判别阈值：按此量级读作秒是公元 5138 年，读作毫秒是 1973 年——
    /// 任何真实的额度重置时间都不会落在错误的一侧，取自参考实现并独立复核过这个论证。</summary>
    private const double EpochUnitThreshold = 1e11;

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _accessKeyIdProvider;
    private readonly Func<string?> _secretAccessKeyProvider;
    private readonly string _region;
    private readonly string _displayName;

    public string ProviderId => "volcengine-ark";

    /// <param name="accessKeyIdProvider">返回当前配置的 Access Key ID；未配置时返回 null。</param>
    /// <param name="secretAccessKeyProvider">返回当前配置的 Secret Access Key；未配置时返回 null。</param>
    /// <param name="region">火山方舟地域，默认 <see cref="VolcengineSigner.DefaultRegion"/>（cn-beijing）。</param>
    /// <param name="displayNameOverride">
    /// 用户在设置页手动填写的套餐显示名覆盖（如"Coding Plan Pro"）；官方接口不返回套餐类型，
    /// 默认只能展示通用的 <see cref="DisplayNameConst"/>，为空/空白时使用默认值，不强行显示 Pro/Lite。
    /// </param>
    public VolcengineArkProvider(
        HttpClient httpClient,
        Func<string?> accessKeyIdProvider,
        Func<string?> secretAccessKeyProvider,
        string? region = null,
        string? displayNameOverride = null)
    {
        _httpClient = httpClient;
        _accessKeyIdProvider = accessKeyIdProvider;
        _secretAccessKeyProvider = secretAccessKeyProvider;
        _displayName = string.IsNullOrWhiteSpace(displayNameOverride) ? DisplayNameConst : displayNameOverride.Trim();
        _region = string.IsNullOrWhiteSpace(region) ? VolcengineSigner.DefaultRegion : region.Trim();
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var accessKeyId = _accessKeyIdProvider();
        var secretAccessKey = _secretAccessKeyProvider();
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            return NotConfigured(_displayName);
        }

        var coding = await CallActionAsync(accessKeyId, secretAccessKey, "GetCodingPlanUsage", cancellationToken);
        if (coding.Error is not null)
        {
            return coding.Error;
        }

        var codingWindows = ParseCodingPlan(coding.Result);
        if (codingWindows.Count > 0)
        {
            return BuildSuccessSnapshot(codingWindows);
        }

        // Coding Plan 没有返回任何窗口（账号未订阅该套餐）：回退探测 Agent Plan。
        // 只有在 Coding Plan 请求本身成功（拿到了合法响应，只是没有额度行）时才继续——
        // 如果上面已经因为网络/鉴权失败返回了 Error，就不会走到这里，不会对同一组坏凭据
        // 再打第二次请求。
        var agent = await CallActionAsync(accessKeyId, secretAccessKey, "GetAFPUsage", cancellationToken);
        if (agent.Error is not null)
        {
            return agent.Error;
        }

        var agentWindows = ParseAgentPlan(agent.Result);
        if (agentWindows.Count > 0)
        {
            return BuildSuccessSnapshot(agentWindows);
        }

        return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.NoPlan,
            "未检测到有效的火山方舟 Coding Plan 或 Agent Plan 订阅，请确认该 Access Key 所属账号已开通套餐",
            _displayName);
    }

    private ProviderSnapshot BuildSuccessSnapshot(List<QuotaWindow> windows)
    {
        var worst = windows.Where(w => !w.IsError).Select(w => (double?)w.RemainingPercent).DefaultIfEmpty(null).Min();
        var state = worst switch
        {
            null => ProviderState.ProviderError,
            <= 0 => ProviderState.Exhausted,
            < 10 => ProviderState.Critical,
            < 30 => ProviderState.Low,
            _ => ProviderState.Available,
        };

        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            DisplayName = _displayName,
            State = state,
            QuotaWindows = windows,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = $"https://{VolcengineSigner.Host}/ (GetCodingPlanUsage/GetAFPUsage)",
            ErrorCategory = ErrorCategory.None,
        };
    }

    /// <summary>
    /// 解析 Coding Plan 的 <c>Result.QuotaUsage</c>。按 <c>Level</c> 字段匹配已知的三档
    /// （session/weekly/monthly），不认识的 level 原样跳过（服务端新增窗口不应该让整个
    /// 解析失败，未来如需展示可以再加映射）；固定按 session → weekly → monthly 的顺序
    /// 输出，与服务端返回的数组顺序无关。
    /// </summary>
    internal static List<QuotaWindow> ParseCodingPlan(JsonElement result)
    {
        var byLevel = new Dictionary<string, QuotaWindow>(StringComparer.OrdinalIgnoreCase);

        JsonElement array = default;
        var found = false;
        foreach (var key in new[] { "QuotaUsage", "Usages", "Details" })
        {
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(key, out var el) &&
                el.ValueKind == JsonValueKind.Array)
            {
                array = el;
                found = true;
                break;
            }
        }

        if (!found)
        {
            return [];
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var level = ReadStringAny(item, "Level", "Type", "Period")?.ToLowerInvariant();
            if (level is null || !LevelDisplayNames.TryGetValue(level, out var displayName))
            {
                continue; // 未知窗口：不猜测它属于哪一档，宁可不展示也不能张冠李戴。
            }

            var percentUsed = ReadDoubleAny(item, "Percent", "UsedPercent", "UsagePercent") ?? 0;
            var resetsAt = ParseResetEpoch(ReadDoubleAny(item, "ResetTime", "ResetTimestamp"));

            byLevel[level] = QuotaWindow.FromUtilization(level, displayName, percentUsed, resetsAt);
        }

        return KnownLevels.Where(byLevel.ContainsKey).Select(l => byLevel[l]).ToList();
    }

    /// <summary>
    /// 解析 Agent Plan 的 <c>Result.AFPFiveHour</c> / <c>AFPWeekly</c> / <c>AFPMonthly</c>。
    /// 单个窗口的 <c>Quota</c>（总额度）为 0 视为"未订阅这一档"，跳过——与账号完全没有 Agent
    /// Plan 时的表现一致，不展示 0/0。
    /// </summary>
    internal static List<QuotaWindow> ParseAgentPlan(JsonElement result)
    {
        var mapping = new (string Field, string Level)[]
        {
            ("AFPFiveHour", "session"),
            ("AFPWeekly", "weekly"),
            ("AFPMonthly", "monthly"),
        };

        var windows = new List<QuotaWindow>();
        foreach (var (field, level) in mapping)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(field, out var win) ||
                win.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var quota = ReadDoubleAny(win, "Quota", "Total", "Limit") ?? 0;
            if (quota <= 0)
            {
                continue;
            }

            var used = ReadDoubleAny(win, "Used", "UsedCount", "Consumed") ?? 0;
            var percentUsed = Math.Clamp(used / quota * 100.0, 0.0, 100.0);
            var resetsAt = ParseResetEpoch(ReadDoubleAny(win, "ResetTime", "ResetTimestamp"));

            windows.Add(QuotaWindow.FromUtilization(level, LevelDisplayNames[level], percentUsed, resetsAt));
        }

        return windows;
    }

    /// <summary>见类型文档第 4 条：两个接口的时间戳单位不一致，按数值量级区分秒/毫秒，
    /// 不按调用的是哪个 Action 写死。空/非正数视为"未提供"，不臆造重置时间。</summary>
    internal static DateTimeOffset? ParseResetEpoch(double? raw)
    {
        if (raw is not { } value || value <= 0 || !double.IsFinite(value))
        {
            return null;
        }

        return value > EpochUnitThreshold
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
            : DateTimeOffset.FromUnixTimeSeconds((long)value);
    }

    private static string? ReadStringAny(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var el))
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    return el.GetString();
                }

                if (el.ValueKind == JsonValueKind.Number)
                {
                    return el.ToString();
                }
            }
        }

        return null;
    }

    private static double? ReadDoubleAny(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            {
                return d;
            }

            if (el.ValueKind == JsonValueKind.String &&
                double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var sd))
            {
                return sd;
            }
        }

        return null;
    }

    /// <summary>发起一次已签名的控制面 OpenAPI 调用并做统一的传输层/业务层错误分类。</summary>
    private async Task<ActionResult> CallActionAsync(
        string accessKeyId, string secretAccessKey, string action, CancellationToken cancellationToken)
    {
        VolcengineSigner.SignedRequest signed;
        try
        {
            signed = VolcengineSigner.BuildSignedRequest(accessKeyId, secretAccessKey, action, _region);
        }
        catch (Exception)
        {
            // 签名构造本身失败（理论上只会是参数为空，已在调用方拦过），不暴露内部异常细节。
            return ActionResult.FromError(BuildSnapshot(ProviderState.ProviderError, ErrorCategory.Unknown,
                "构造火山方舟签名请求失败", _displayName));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, signed.Url)
        {
            Content = new StringContent(string.Empty),
        };
        foreach (var (key, value) in signed.Headers)
        {
            if (key == "Content-Type")
            {
                continue; // Content-Type 由 StringContent 的 Headers 承载，不能再加到 request.Headers。
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }

        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", VolcengineSigner.ContentType);

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
            return ActionResult.FromError(BuildSnapshot(state, category, guidance, _displayName));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ActionResult.FromError(BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited,
                    "火山方舟控制面接口请求过于频繁，请稍后重试", _displayName));
            }

            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
                return ActionResult.FromError(BuildSnapshot(state, category, guidance, _displayName));
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                if (!response.IsSuccessStatusCode)
                {
                    // 响应体不是合法 JSON 的非 2xx（如网关直接吐纯文本错误页），仍然按状态码分类，
                    // 401/403 走鉴权引导，而不是笼统的"服务器错误"。
                    return ActionResult.FromError(ClassifyBusinessError(response.StatusCode, string.Empty, string.Empty, _displayName));
                }

                return ActionResult.FromError(BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "火山方舟控制面接口返回的不是有效 JSON，接口可能已变化", _displayName));
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("ResponseMetadata", out var meta) &&
                    meta.TryGetProperty("Error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
                {
                    var code = ReadStringAny(errEl, "Code") ?? string.Empty;
                    var message = ReadStringAny(errEl, "Message") ?? string.Empty;
                    return ActionResult.FromError(ClassifyBusinessError(response.StatusCode, code, message, _displayName));
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ActionResult.FromError(ClassifyBusinessError(response.StatusCode, string.Empty, string.Empty, _displayName));
                }

                if (!root.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Object)
                {
                    return ActionResult.FromError(BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                        MissingFieldGuidance("Result", raw), _displayName));
                }

                return ActionResult.Ok(result.Clone());
            }
        }
    }

    /// <summary>
    /// 把控制面网关的 <c>ResponseMetadata.Error.Code</c> 翻译成用户可读的引导文案，
    /// 覆盖认证失败/签名错误/时间偏差/权限不足几类（文档 §12），而不是笼统的"出错了"。
    /// Code/Message 本身来自服务端，不是密钥，可以安全展示。
    /// </summary>
    internal static ProviderSnapshot ClassifyBusinessError(
        HttpStatusCode statusCode, string code, string message, string displayName = DisplayNameConst)
    {
        var lowered = code.ToLowerInvariant();
        var isAuthLike = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                          ContainsAny(lowered, "auth", "signature", "accessdenied", "denied", "unauthorized",
                              "forbidden", "credential", "token", "accesskey", "invalid", "skew");

        if (isAuthLike)
        {
            var guidance = lowered switch
            {
                _ when ContainsAny(lowered, "signature") =>
                    "签名校验失败，请检查 Secret Access Key 是否正确，若确认无误可能是本机系统时间偏差导致",
                _ when ContainsAny(lowered, "expired", "clockskew", "requesttimetooskewed") =>
                    "系统时间偏差过大，无法通过火山方舟 API 签名验证，请校对本机系统时间后重试",
                _ when ContainsAny(lowered, "denied", "forbidden", "permission", "accessdenied") =>
                    "当前 Access Key 无权读取火山方舟套餐信息，请检查该 IAM 用户是否具备读取权限",
                _ => "认证失败，请检查 Access Key ID / Secret Access Key 是否正确",
            };

            return BuildSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired, guidance, displayName);
        }

        if (ContainsAny(lowered, "toomany", "throttl", "qpslimit", "ratelimit"))
        {
            return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "火山方舟控制面接口请求过于频繁，请稍后重试", displayName);
        }

        var sanitizedMessage = message.Length > 200 ? message[..200] : message;
        var guidanceText = string.IsNullOrWhiteSpace(code)
            // code 为空说明这不是网关返回的结构化业务错误，而是响应体没有 ResponseMetadata.Error
            // 信封时按 HTTP 状态码兜底的情形——展示状态码而不是空括号。
            ? $"火山方舟控制面接口返回异常状态（HTTP {(int)statusCode}）"
            : string.IsNullOrWhiteSpace(sanitizedMessage)
                ? $"火山方舟接口返回业务错误（{code}）"
                : $"火山方舟接口返回业务错误（{code}）：{sanitizedMessage}";
        return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError, guidanceText, displayName);
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static string MissingFieldGuidance(string expectedField, string raw)
    {
        var shape = JsonShapeDescriber.Summarize(raw);
        var suffix = shape is null ? string.Empty : $"；本次实际收到的字段为：{shape}";
        return $"火山方舟接口返回的 JSON 中缺少 {expectedField} 字段（接口可能已变化）{suffix}";
    }

    private static ProviderSnapshot NotConfigured(string displayName = DisplayNameConst) => new()
    {
        ProviderId = "volcengine-ark",
        DisplayName = displayName,
        State = ProviderState.NotConfigured,
        DataSource = $"https://{VolcengineSigner.Host}/",
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "请在设置页填写火山方舟 Access Key ID 与 Secret Access Key",
    };

    private static ProviderSnapshot BuildSnapshot(
        ProviderState state, ErrorCategory category, string guidance, string displayName = DisplayNameConst) => new()
    {
        ProviderId = "volcengine-ark",
        DisplayName = displayName,
        State = state,
        DataSource = $"https://{VolcengineSigner.Host}/",
        ErrorCategory = category,
        UserGuidance = guidance,
    };

    /// <summary>
    /// 把 Access Key ID 脱敏成 <c>前 4 位****后 4 位</c>（如 <c>AKLT****3456</c>），用于诊断信息/
    /// 连接状态展示——Access Key ID 本身不是 Secret，但为了避免完整值出现在日志/截图里被
    /// 直接复制使用，仍然只展示可辨识的局部。太短（≤8 位）时全部掩盖，不露出任何真实字符。
    /// 注意：这个方法只处理 Access Key ID；Secret Access Key 任何时候都不经过这个方法或任何其他
    /// 方式显示，只能整体替换成固定掩码或完全不显示。
    /// </summary>
    internal static string MaskAccessKeyId(string accessKeyId)
    {
        if (string.IsNullOrEmpty(accessKeyId))
        {
            return string.Empty;
        }

        return accessKeyId.Length <= 8
            ? new string('*', accessKeyId.Length)
            : $"{accessKeyId[..4]}****{accessKeyId[^4..]}";
    }

    private readonly record struct ActionResult(JsonElement Result, ProviderSnapshot? Error)
    {
        public static ActionResult Ok(JsonElement result) => new(result, null);
        public static ActionResult FromError(ProviderSnapshot error) => new(default, error);
    }
}
