using System.Globalization;
using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 查询 DeepSeek 账户可用余额。
///
/// 使用用户手动填写的普通 API Key（经 <see cref="Services.SecureCredentialStore"/> 存取）。
/// 接口 GET https://api.deepseek.com/user/balance 是 DeepSeek 官方文档公开接口，
/// 响应结构同时用 cc-switch（MIT License）实现交叉确认。
/// </summary>
public sealed class DeepSeekBalanceProvider : IQuotaProvider
{
    /// <summary>DeepSeek 余额接口的内置默认地址；用户可在设置页覆盖。</summary>
    public const string DefaultUsageUrl = "https://api.deepseek.com/user/balance";
    private readonly string _dataSourceUrl;

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;

    public string ProviderId => "deepseek";

    /// <param name="endpointOverride">覆盖余额接口地址；空/未填时使用内置默认。</param>
    public DeepSeekBalanceProvider(HttpClient httpClient, Func<string?> apiKeyProvider,
        string? endpointOverride = null)
    {
        _httpClient = httpClient;
        _apiKeyProvider = apiKeyProvider;
        _dataSourceUrl = EndpointResolver.Resolve(endpointOverride, DefaultUsageUrl);
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
                    "DeepSeek API Key 无效或已被拒绝，请在设置页重新填写");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "查询过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"DeepSeek 余额接口返回异常状态（HTTP {(int)response.StatusCode}）");
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
                "DeepSeek 余额接口返回的数据格式无法识别，可能是接口发生了变化");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var isAvailable = root.TryGetProperty("is_available", out var availEl) &&
                               availEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? availEl.GetBoolean()
                : true;

            if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array ||
                infos.GetArrayLength() == 0)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "DeepSeek 余额接口未返回 balance_infos 字段");
            }

            // 官方接口目前每个 Key 只对应一种币种；取第一条作为主余额展示，
            // 若上游未来返回多币种，其余条目按 DataSource 相同处理，此处先不臆造合并逻辑。
            var first = infos[0];
            var currency = first.TryGetProperty("currency", out var currEl) ? currEl.GetString() ?? "CNY" : "CNY";
            var total = ParseDecimal(first, "total_balance");
            var granted = ParseDecimal(first, "granted_balance");
            var toppedUp = ParseDecimal(first, "topped_up_balance");

            if (total is null)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    "DeepSeek 余额接口未返回 total_balance 字段");
            }

            var balance = new BalanceMetric(total.Value, currency, granted, toppedUp);

            return new ProviderSnapshot
            {
                ProviderId = ProviderId,
                DisplayName = "DeepSeek",
                State = isAvailable ? ProviderState.Available : ProviderState.Critical,
                Balance = balance,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                DataSource = _dataSourceUrl,
                ErrorCategory = ErrorCategory.None,
                UserGuidance = isAvailable ? null : "账户余额不可用，请检查 DeepSeek 账户状态",
            };
        }
    }

    private static decimal? ParseDecimal(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = "DeepSeek",
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = "请在设置页填写 DeepSeek API Key",
    };

    private ProviderSnapshot BuildSnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = "DeepSeek",
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };
}
