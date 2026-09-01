using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 查询 Claude Pro/Max/Claude Code 订阅额度。
///
/// 复用 Claude Code CLI 本机已登录的 OAuth 凭据（~/.claude/.credentials.json），
/// 不要求用户在 QuotaFlow 内重新输入账号密码。
///
/// 接口 GET https://api.anthropic.com/api/oauth/usage 是非公开接口，路径和字段结构
/// 参考自 cc-switch（MIT License, https://github.com/farion1231/cc-switch，
/// src-tauri/src/services/subscription.rs）并结合本机实测响应确认，未来可能随官方调整而变化，
/// 因此对未知字段采用宽松解析：已知窗口优先按名字取，缺失的窗口直接跳过而不是伪造成 0%。
/// 服务端新出现的未知窗口（例如 nimbus_quill）默认隐藏——这类新窗口对多数用户是 0% 使用、
/// 无重置时间的空额度，原样透传成英文名展示既看不懂也占地方；只有 <see cref="_includeUnknownWindows"/>
/// 返回 true（用户在设置里显式开启）时才展示，并统一换成中文标签、保留原始 id 便于排查。
/// </summary>
public sealed class ClaudeQuotaProvider : IQuotaProvider
{
    /// <summary>Claude 用量接口的内置默认地址；用户可在设置页覆盖。</summary>
    public const string DefaultUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private readonly string _dataSourceUrl;
    private static readonly string[] KnownTierOrder =
        ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_omelette"];

    private static readonly IReadOnlyDictionary<string, string> TierDisplayNames = new Dictionary<string, string>
    {
        ["five_hour"] = "5 小时",
        ["seven_day"] = "7 天",
        ["seven_day_opus"] = "7 天 (Opus)",
        ["seven_day_sonnet"] = "7 天 (Sonnet)",
        ["seven_day_omelette"] = "7 天 (Design)", // Claude Design 的设计额度
    };

    private readonly HttpClient _httpClient;
    private readonly ClaudeCredentialReader _credentialReader;
    private readonly Func<bool> _includeUnknownWindows;

    public string ProviderId => "claude";

    /// <param name="endpointOverride">覆盖用量接口地址；空/未填时使用内置默认。</param>
    public ClaudeQuotaProvider(HttpClient httpClient, ClaudeCredentialReader? credentialReader = null,
        Func<bool>? includeUnknownWindows = null, string? endpointOverride = null)
    {
        _httpClient = httpClient;
        _credentialReader = credentialReader ?? new ClaudeCredentialReader();
        _includeUnknownWindows = includeUnknownWindows ?? (() => false);
        _dataSourceUrl = EndpointResolver.Resolve(endpointOverride, DefaultUsageUrl);
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var credential = _credentialReader.Read();

        switch (credential.Status)
        {
            case CredentialStatus.NotFound:
                return NotConfigured();

            case CredentialStatus.ParseError:
                return ProviderErrorSnapshot(credential.Message ?? "无法解析本机凭据");

            case CredentialStatus.Expired:
            {
                // 本地时间判断的"过期"可能因时钟偏差而误判，实际调用一次再确认——
                // 与 cc-switch 的策略一致：过期状态下也尝试调用 API。
                var attempt = await QueryAsync(credential.AccessToken!, cancellationToken);
                if (attempt.State != ProviderState.AuthenticationExpired)
                {
                    return attempt;
                }

                return AuthExpired("Claude Code 本机凭据已过期，请在 Claude Code 中重新登录");
            }

            case CredentialStatus.Valid:
                return await QueryAsync(credential.AccessToken!, cancellationToken);

            default:
                return ProviderErrorSnapshot("未知的凭据状态");
        }
    }

    private async Task<ProviderSnapshot> QueryAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _dataSourceUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Add("Accept", "application/json");

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
            return BuildSnapshot(state, category, guidance);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return AuthExpired("Claude Code 本机凭据已过期或被拒绝，请在 Claude Code 中重新登录");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // 以服务端的 Retry-After 为准；它没给值时由 RefreshCoordinator 回退到默认冷却。
                var retryAfter = RetryAfterReader.Read(response.Headers, DateTimeOffset.UtcNow);
                var guidance = retryAfter is { } until
                    ? $"查询过于频繁，将在 {FormatWait(until - DateTimeOffset.UtcNow)}后自动重试"
                    : "查询过于频繁，稍后会自动重试";
                return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, guidance, retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"Claude 用量接口返回异常状态（HTTP {(int)response.StatusCode}）");
            }

            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                var (state, category, guidance) = HttpErrorClassifier.Classify(ex, cancellationToken);
                return BuildSnapshot(state, category, guidance);
            }

            return ParseResponse(raw);
        }
    }

    internal ProviderSnapshot ParseResponse(string raw)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                "Claude 用量接口返回的数据格式无法识别，可能是接口发生了变化");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var windows = new List<QuotaWindow>();

            // 已知窗口按固定顺序展示。
            foreach (var tier in KnownTierOrder)
            {
                if (root.TryGetProperty(tier, out var windowEl) && TryParseWindow(tier, windowEl, out var window))
                {
                    windows.Add(window);
                }
            }

            // 未知窗口（服务端新增字段）：默认隐藏，用户显式开启"显示未识别窗口"后才展示，
            // 且统一用中文标签带出原始 id，避免面板上出现 nimbus_quill 这种看不懂的英文名。
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "extra_usage" || KnownTierOrder.Contains(prop.Name))
                {
                    continue;
                }

                if (!_includeUnknownWindows() || !TryParseWindow(prop.Name, prop.Value, out var window))
                {
                    continue;
                }

                windows.Add(window with { DisplayName = $"其他额度（{prop.Name}）" });
            }

            if (windows.Count == 0)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "Claude 用量接口未返回任何可识别的额度窗口（如需查看未识别的新窗口，可在设置中开启显示）");
            }

            var worst = windows.Min(w => w.RemainingPercent);
            var state = worst switch
            {
                <= 0 => ProviderState.Exhausted,
                < 10 => ProviderState.Critical,
                < 30 => ProviderState.Low,
                _ => ProviderState.Available,
            };

            return new ProviderSnapshot
            {
                ProviderId = ProviderId,
                DisplayName = "Claude",
                State = state,
                QuotaWindows = windows,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                DataSource = _dataSourceUrl,
                ErrorCategory = ErrorCategory.None,
            };
        }
    }

    internal static bool TryParseWindow(string id, JsonElement element, out QuotaWindow window)
    {
        window = null!;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("utilization", out var utilEl) ||
            !utilEl.TryGetDouble(out var utilization))
        {
            return false;
        }

        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetsEl) &&
            resetsEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetsEl.GetString(), out var parsed))
        {
            resetsAt = parsed;
        }

        var displayName = TierDisplayNames.GetValueOrDefault(id, id);
        window = QuotaWindow.FromUtilization(id, displayName, utilization, resetsAt);
        return true;
    }

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Claude",
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "未检测到 Claude Code 本机登录，请先运行 Claude Code 并登录",
    };

    private ProviderSnapshot AuthExpired(string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Claude",
        State = ProviderState.AuthenticationExpired,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.AuthenticationExpired,
        UserGuidance = guidance,
    };

    private ProviderSnapshot ProviderErrorSnapshot(string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Claude",
        State = ProviderState.ProviderError,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.ResponseFormat,
        UserGuidance = guidance,
    };

    /// <summary>把等待时长说成人话，避免给用户看"00:03:00"这种机器格式。</summary>
    private static string FormatWait(TimeSpan wait)
    {
        if (wait < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} 秒";
        }

        return wait < TimeSpan.FromHours(1)
            ? $"{(int)Math.Ceiling(wait.TotalMinutes)} 分钟"
            : $"{wait.TotalHours:F1} 小时";
    }

    private ProviderSnapshot BuildSnapshot(ProviderState state, ErrorCategory category, string guidance,
        DateTimeOffset? retryAfter = null) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Claude",
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
        RetryAfter = retryAfter,
    };
}
