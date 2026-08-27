using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 用户在设置页手动添加的自定义平台（OpenCode / GO 等）的查询 Provider。
/// 传输与错误分类骨架照抄 <see cref="MiniMaxQuotaProvider"/>：15s 超时、401/403/429/非 2xx
/// 分别归类、传输层异常统一走 <see cref="HttpErrorClassifier"/>——保证单平台失败绝不波及
/// 其他平台。
///
/// 数据语义由 <see cref="CustomPlatformSettings.DataKind"/> 决定：
/// <list type="bullet">
/// <item><description>UtilizationPercent / RemainingPercent：取到的值换算成额度窗口
/// （<c>remaining = 100 - value</c> 或原样），沿用与 MiniMax 一致的剩余阈值映射。</description></item>
/// <item><description>Balance：取到的值作为余额金额，<c>&gt; 0 → Available</c>，
/// <c>&lt;= 0 → Exhausted</c>——不臆造"偏低"之类的中间阈值。</description></item>
/// </list>
/// 取值路径（<see cref="CustomPlatformSettings.ValuePath"/>）取不到、非法或类型不符时返回
/// ProviderError + ResponseFormat + 点名路径的引导文案，**绝不把缺失数据显示成 0% / 0.00**。
/// </summary>
public sealed class CustomPlatformProvider : IQuotaProvider
{
    private readonly HttpClient _httpClient;
    private readonly CustomPlatformSettings _definition;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string _dataSourceUrl;

    public string ProviderId => _definition.Id;

    /// <param name="httpClient">共享 HttpClient。</param>
    /// <param name="definition">用户配置的平台定义；Id 固定为 <c>custom-{n}</c>。</param>
    /// <param name="apiKeyProvider">返回该平台当前配置的 API Key；未配置时返回 null。</param>
    public CustomPlatformProvider(HttpClient httpClient, CustomPlatformSettings definition, Func<string?> apiKeyProvider)
    {
        _httpClient = httpClient;
        _definition = definition;
        _apiKeyProvider = apiKeyProvider;
        _dataSourceUrl = definition.Endpoint.Trim();
    }

    public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _apiKeyProvider();
        if (_definition.AuthKind != CustomAuthKind.None && string.IsNullOrWhiteSpace(apiKey))
        {
            return NotConfigured();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _dataSourceUrl);

        switch (_definition.AuthKind)
        {
            case CustomAuthKind.BearerKey:
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                break;
            case CustomAuthKind.CustomHeader:
                if (string.IsNullOrWhiteSpace(_definition.HeaderName))
                {
                    return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                        $"自定义平台「{_definition.Name}」选择了自定义请求头，但未填写请求头名称");
                }

                request.Headers.TryAddWithoutValidation(_definition.HeaderName, apiKey);
                break;
            case CustomAuthKind.None:
                break;
            default:
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    $"自定义平台「{_definition.Name}」的鉴权方式无效");
        }

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
                    $"自定义平台「{_definition.Name}」的 API Key 无效或已被拒绝，请在设置页重新填写");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BuildSnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "查询过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
                    $"自定义平台「{_definition.Name}」接口返回异常状态（HTTP {(int)response.StatusCode}）");
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
                $"自定义平台「{_definition.Name}」接口返回的数据不是有效 JSON，可能是接口发生了变化");
        }

        using (doc)
        {
            var root = doc.RootElement;

            var valueElement = JsonPathResolver.TryResolve(root, _definition.ValuePath);
            if (valueElement is not { } value)
            {
                return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    $"自定义平台「{_definition.Name}」在取值路径 \"{_definition.ValuePath}\" 上没有取到数据，" +
                    "请检查接口返回结构和路径写法（支持点号属性与 [n] 数组下标）");
            }

            DateTimeOffset? resetsAt = null;
            if (!string.IsNullOrWhiteSpace(_definition.ResetsAtPath))
            {
                var resetsEl = JsonPathResolver.TryResolve(root, _definition.ResetsAtPath!);
                if (resetsEl is not null)
                {
                    if (!JsonPathResolver.TryGetDateTimeOffset(resetsEl, out var parsedResets))
                    {
                        return BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                            $"自定义平台「{_definition.Name}」的窗口重置时间路径 \"{_definition.ResetsAtPath}\"" +
                            "取到的值无法解析为时间，请检查接口返回结构");
                    }

                    resetsAt = parsedResets;
                }
                // 重置时间路径在本次响应里没取到值：视为无重置时间，不阻断整张卡片。
            }

            return _definition.DataKind switch
            {
                CustomDataKind.UtilizationPercent => BuildUtilizationWindow(value, resetsAt),
                CustomDataKind.RemainingPercent => BuildRemainingWindow(value, resetsAt),
                CustomDataKind.Balance => BuildBalance(value),
                _ => BuildSnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                    $"自定义平台「{_definition.Name}」的数据语义配置无效"),
            };
        }
    }

    private ProviderSnapshot BuildUtilizationWindow(JsonElement value, DateTimeOffset? resetsAt)
    {
        if (!JsonPathResolver.TryGetDouble(value, out var utilization))
        {
            return PathValueNotNumeric();
        }

        var window = QuotaWindow.FromUtilization("quota", "额度", utilization, resetsAt);
        return BuildWindowsSnapshot([window]);
    }

    private ProviderSnapshot BuildRemainingWindow(JsonElement value, DateTimeOffset? resetsAt)
    {
        if (!JsonPathResolver.TryGetDouble(value, out var remaining))
        {
            return PathValueNotNumeric();
        }

        var window = QuotaWindow.FromRemaining("quota", "额度", remaining, resetsAt);
        return BuildWindowsSnapshot([window]);
    }

    private ProviderSnapshot BuildBalance(JsonElement value)
    {
        if (!JsonPathResolver.TryGetDecimal(value, out var amount))
        {
            return PathValueNotNumeric();
        }

        // 余额不臆造"偏低"之类中间阈值：有余额就 Available，一分不剩就是 Exhausted。
        var state = amount > 0 ? ProviderState.Available : ProviderState.Exhausted;
        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            DisplayName = _definition.Name,
            State = state,
            Balance = new BalanceMetric(amount, _definition.Currency),
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = _dataSourceUrl,
            ErrorCategory = ErrorCategory.None,
            UserGuidance = amount > 0 ? null : "余额已用尽，请充值后继续使用",
        };
    }

    private ProviderSnapshot BuildWindowsSnapshot(IReadOnlyList<QuotaWindow> windows)
    {
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
            DisplayName = _definition.Name,
            State = state,
            QuotaWindows = windows,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = _dataSourceUrl,
            ErrorCategory = ErrorCategory.None,
        };
    }

    private ProviderSnapshot PathValueNotNumeric() => BuildSnapshot(
        ProviderState.ProviderError, ErrorCategory.ResponseFormat,
        $"自定义平台「{_definition.Name}」的取值路径 \"{_definition.ValuePath}\" 取到的是非数字，" +
        "请检查接口返回结构是否与数据语义匹配");

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = _definition.Name,
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = $"请在设置页填写自定义平台「{_definition.Name}」的 API Key",
    };

    private ProviderSnapshot BuildSnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = _definition.Name,
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };
}
