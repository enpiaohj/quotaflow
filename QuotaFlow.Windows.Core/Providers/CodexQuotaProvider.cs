using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 查询 ChatGPT/Codex 订阅额度。
///
/// 复用 Codex CLI 本机已登录的 OAuth 凭据（~/.codex/auth.json，仅 auth_mode == "chatgpt"
/// 时有效）。明确不使用 OpenAI Admin Key——它和 ChatGPT/Codex 订阅额度不是同一体系
/// （文档 §3.2 硬性要求）。
///
/// 接口 GET https://chatgpt.com/backend-api/wham/usage 是非公开接口，路径和字段结构
/// 参考自 cc-switch（MIT License）并结合本机实测响应确认。
/// </summary>
public sealed class CodexQuotaProvider : IQuotaProvider
{
    /// <summary>Codex 用量接口的内置默认地址；用户可在设置页覆盖。</summary>
    public const string DefaultUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private readonly string _dataSourceUrl;

    private readonly HttpClient _httpClient;
    private readonly CodexCredentialReader _credentialReader;

    public string ProviderId => "codex";

    /// <param name="endpointOverride">覆盖用量接口地址；空/未填时使用内置默认。</param>
    public CodexQuotaProvider(HttpClient httpClient, CodexCredentialReader? credentialReader = null,
        string? endpointOverride = null)
    {
        _httpClient = httpClient;
        _credentialReader = credentialReader ?? new CodexCredentialReader();
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
                var attempt = await QueryAsync(credential.AccessToken!, credential.AccountId, cancellationToken);
                if (attempt.State != ProviderState.AuthenticationExpired)
                {
                    return attempt;
                }

                return AuthExpired("Codex 本机凭据已过期，请在 Codex CLI 中重新登录");
            }

            case CredentialStatus.Valid:
                return await QueryAsync(credential.AccessToken!, credential.AccountId, cancellationToken);

            default:
                return ProviderErrorSnapshot("未知的凭据状态");
        }
    }

    private async Task<ProviderSnapshot> QueryAsync(string accessToken, string? accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _dataSourceUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("User-Agent", "codex-cli");
        request.Headers.Add("Accept", "application/json");
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.Add("ChatGPT-Account-Id", accountId);
        }

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
                return AuthExpired("Codex 本机凭据已过期或被拒绝，请在 Codex CLI 中重新登录");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "查询过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"Codex 用量接口返回异常状态（HTTP {(int)response.StatusCode}）");
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
                "Codex 用量接口返回的数据格式无法识别，可能是接口发生了变化");
        }

        using (doc)
        {
            var windows = new List<QuotaWindow>();

            if (doc.RootElement.TryGetProperty("rate_limit", out var rateLimit))
            {
                foreach (var key in new[] { "primary_window", "secondary_window" })
                {
                    if (rateLimit.TryGetProperty(key, out var windowEl) &&
                        TryParseWindow(windowEl, out var window))
                    {
                        windows.Add(window);
                    }
                }
            }

            if (windows.Count == 0)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "Codex 用量接口未返回任何可识别的额度窗口");
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
                DisplayName = "Codex",
                State = state,
                QuotaWindows = windows,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                DataSource = _dataSourceUrl,
                ErrorCategory = ErrorCategory.None,
            };
        }
    }

    internal static bool TryParseWindow(JsonElement element, out QuotaWindow window)
    {
        window = null!;

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("used_percent", out var usedEl) ||
            !usedEl.TryGetDouble(out var usedPercent))
        {
            return false;
        }

        string id = "unknown";
        string displayName = "未知窗口";
        if (element.TryGetProperty("limit_window_seconds", out var secondsEl) && secondsEl.TryGetInt64(out var seconds))
        {
            (id, displayName) = MapWindowSeconds(seconds);
        }

        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("reset_at", out var resetEl) && resetEl.TryGetInt64(out var resetUnixSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnixSeconds);
        }

        window = QuotaWindow.FromUtilization(id, displayName, usedPercent, resetsAt);
        return true;
    }

    private static (string Id, string DisplayName) MapWindowSeconds(long seconds) => seconds switch
    {
        18_000 => ("five_hour", "5 小时"),
        604_800 => ("seven_day", "7 天"),
        2_592_000 => ("30_day", "30 天"), // Codex 免费方案的月度滚动窗口
        _ when seconds % 86_400 == 0 => ($"{seconds / 86_400}_day", $"{seconds / 86_400} 天"),
        _ => ($"{seconds / 3600}_hour", $"{seconds / 3600} 小时"),
    };

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Codex",
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "未检测到 Codex CLI 本机 ChatGPT 登录，请先运行 Codex CLI 并使用 ChatGPT 账号登录",
    };

    private ProviderSnapshot AuthExpired(string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Codex",
        State = ProviderState.AuthenticationExpired,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.AuthenticationExpired,
        UserGuidance = guidance,
    };

    private ProviderSnapshot ProviderErrorSnapshot(string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Codex",
        State = ProviderState.ProviderError,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.ResponseFormat,
        UserGuidance = guidance,
    };

    private ProviderSnapshot BuildSnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "Codex",
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };
}
