using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 查询 MiniMax Coding Plan 编程套餐额度。
///
/// 使用用户手动填写的 API Key（经 <see cref="Services.SecureCredentialStore"/> 存取，
/// 本类不关心存储细节，只通过 <c>apiKeyProvider</c> 委托拿到当前值）。
///
/// 接口 GET /v1/api/openplatform/coding_plan/remains 是非公开接口，路径和字段结构
/// 参考自 cc-switch（MIT License, src-tauri/src/services/coding_plan.rs）并结合本机
/// 实测响应确认。注意该接口的百分比字段是"剩余百分比"，与 Claude/Codex 的"已用百分比"
/// 语义相反，解析时不能直接套用同一个换算公式。
/// </summary>
public sealed class MiniMaxQuotaProvider : IQuotaProvider
{
    /// <summary>MiniMax 国内站点域名。</summary>
    public const string DomainCn = "api.minimaxi.com";

    /// <summary>MiniMax 国际站点域名。</summary>
    public const string DomainIntl = "api.minimax.io";

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string _domain;
    private readonly string _dataSourceUrl;

    public string ProviderId => "minimax";

    /// <param name="apiKeyProvider">返回当前配置的 API Key；未配置时返回 null。</param>
    /// <param name="domain">MiniMax 站点域名，默认国内站（<see cref="DomainCn"/>）。</param>
    public MiniMaxQuotaProvider(HttpClient httpClient, Func<string?> apiKeyProvider, string domain = DomainCn)
    {
        _httpClient = httpClient;
        _apiKeyProvider = apiKeyProvider;
        _domain = domain;
        _dataSourceUrl = $"https://{_domain}/v1/api/openplatform/coding_plan/remains";
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return NotConfigured();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _dataSourceUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
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
                return BuildSnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    "MiniMax API Key 无效或已被拒绝，请在设置页重新填写");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "查询过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"MiniMax 用量接口返回异常状态（HTTP {(int)response.StatusCode}）");
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
                "MiniMax 用量接口返回的数据格式无法识别，可能是接口发生了变化");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("base_resp", out var baseResp) &&
                baseResp.TryGetProperty("status_code", out var statusCodeEl) &&
                statusCodeEl.TryGetInt64(out var statusCode) &&
                statusCode != 0)
            {
                var msg = baseResp.TryGetProperty("status_msg", out var msgEl) ? msgEl.GetString() : "未知错误";
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"MiniMax 接口返回业务错误（code {statusCode}）：{msg}");
            }

            if (!root.TryGetProperty("model_remains", out var modelRemains) ||
                modelRemains.ValueKind != JsonValueKind.Array)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "MiniMax 用量接口未返回 model_remains 字段");
            }

            JsonElement? generalItem = null;
            foreach (var item in modelRemains.EnumerateArray())
            {
                if (item.TryGetProperty("model_name", out var nameEl) && nameEl.GetString() == "general")
                {
                    generalItem = item;
                    break;
                }
            }

            if (generalItem is not { } item2)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "MiniMax 用量接口未返回编程套餐（general）的额度数据");
            }

            var windows = new List<QuotaWindow>();

            if (item2.TryGetProperty("current_interval_remaining_percent", out var remainEl) &&
                remainEl.TryGetDouble(out var remainPercent))
            {
                DateTimeOffset? resetsAt = null;
                if (item2.TryGetProperty("end_time", out var endTimeEl) && endTimeEl.TryGetInt64(out var endTimeMs))
                {
                    resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(endTimeMs);
                }

                windows.Add(QuotaWindow.FromRemaining("five_hour", "5 小时", remainPercent, resetsAt));
            }

            // 周窗口仅当 current_weekly_status == 1 时才是激活状态；否则该套餐没有周限额，
            // 不应该展示一个恒为 100% 的假窗口。
            if (item2.TryGetProperty("current_weekly_status", out var weeklyStatusEl) &&
                weeklyStatusEl.TryGetInt64(out var weeklyStatus) && weeklyStatus == 1 &&
                item2.TryGetProperty("current_weekly_remaining_percent", out var weeklyRemainEl) &&
                weeklyRemainEl.TryGetDouble(out var weeklyRemainPercent))
            {
                DateTimeOffset? weeklyResetsAt = null;
                if (item2.TryGetProperty("weekly_end_time", out var weeklyEndEl) && weeklyEndEl.TryGetInt64(out var weeklyEndMs))
                {
                    weeklyResetsAt = DateTimeOffset.FromUnixTimeMilliseconds(weeklyEndMs);
                }

                windows.Add(QuotaWindow.FromRemaining("weekly_limit", "7 天", weeklyRemainPercent, weeklyResetsAt));
            }

            if (windows.Count == 0)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "MiniMax 用量接口未返回任何可识别的额度窗口");
            }

            var worst = windows.Min(w => w.RemainingPercent);
            var state = worst switch
            {
                < 10 => ProviderState.Critical,
                < 30 => ProviderState.Low,
                _ => ProviderState.Available,
            };

            return new ProviderSnapshot
            {
                ProviderId = ProviderId,
                DisplayName = "MiniMax",
                State = state,
                QuotaWindows = windows,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                DataSource = _dataSourceUrl,
                ErrorCategory = ErrorCategory.None,
            };
        }
    }

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = "MiniMax",
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "请在设置页填写 MiniMax API Key",
    };

    private ProviderSnapshot BuildSnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "MiniMax",
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };
}
