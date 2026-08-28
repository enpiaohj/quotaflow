using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 用户在设置页手动添加的自定义平台（OpenCode GO 等）的查询 Provider。
/// 传输与错误分类骨架照抄 <see cref="MiniMaxQuotaProvider"/>：15s 超时、401/403/429/非 2xx
/// 分别归类、传输层异常统一走 <see cref="HttpErrorClassifier"/>——保证单平台失败绝不波及
/// 其他平台。
///
/// v1.1.0：一个平台只发一次请求，从同一份 JSON 响应里按
/// <see cref="CustomPlatformSettings.QuotaWindows"/> 逐个取值，解析成多个 <see cref="QuotaWindow"/>——
/// 不再要求把"5 小时/每周/每月"配成三个平台。任意一个窗口取值失败只影响它自己
/// （<see cref="QuotaWindow.FromError"/> 占位，UI 显示"数据不可用"），不阻断其它窗口，也不阻断
/// 已经解析成功的余额。只有当"所有窗口都失败 + 没有余额"时，整个平台这次刷新才算失败
/// （<see cref="ProviderState.ProviderError"/>）。
///
/// <see cref="CustomDataKind.Balance"/> 是已知的例外：<see cref="ProviderSnapshot"/> 只支持单个
/// <see cref="ProviderSnapshot.Balance"/>，因此一个平台里只有第一个成功解析的余额窗口会生效；
/// 其余余额窗口（无论是配置了多个、还是解析失败）都不会额外产生自己的余额展示——失败的会退化成
/// 一行"数据不可用"，成功但排在后面的会被静默忽略。这是本次改造的已知限制，详见发布说明。
/// </summary>
public sealed class CustomPlatformProvider : IQuotaProvider
{
    private readonly HttpClient _httpClient;
    private readonly CustomPlatformSettings _definition;
    private readonly Func<string?> _apiKeyProvider;
    private readonly string _dataSourceUrl;

    private static readonly Dictionary<ProviderState, int> SeverityRank = new()
    {
        [ProviderState.Available] = 0,
        [ProviderState.Low] = 1,
        [ProviderState.Critical] = 2,
        [ProviderState.Exhausted] = 3,
    };

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
        // 没有任何窗口可解析（迁移前的空配置、或配置文件被手改坏）——不发请求，按未配置处理，
        // 绝不发一次没有意义的请求再报一个"全部窗口失败"的错误。
        if (_definition.QuotaWindows.Count == 0)
        {
            return NotConfigured();
        }

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
                    return BuildEmptySnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                        $"自定义平台「{_definition.Name}」选择了自定义请求头，但未填写请求头名称");
                }

                request.Headers.TryAddWithoutValidation(_definition.HeaderName, apiKey);
                break;
            case CustomAuthKind.None:
                break;
            default:
                return BuildEmptySnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
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
            return BuildEmptySnapshot(state, category, guidance);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return BuildEmptySnapshot(ProviderState.AuthenticationExpired, ErrorCategory.AuthenticationExpired,
                    $"自定义平台「{_definition.Name}」的 API Key 无效或已被拒绝，请在设置页重新填写");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return BuildEmptySnapshot(ProviderState.RateLimited, ErrorCategory.RateLimited, "查询过于频繁，请稍后再试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return BuildEmptySnapshot(ProviderState.ProviderError, ErrorCategory.ServerError,
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
                return BuildEmptySnapshot(state, category, guidance);
            }

            return ParseResponse(raw);
        }
    }

    /// <summary>
    /// 解析一次响应里的所有额度窗口。只发一次请求（调用方保证）、同一份 <see cref="JsonDocument"/>
    /// 供所有窗口共用；单个窗口取值/换算失败不影响其它窗口。
    /// </summary>
    internal ProviderSnapshot ParseResponse(string raw)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return BuildEmptySnapshot(ProviderState.ProviderError, ErrorCategory.ResponseFormat,
                $"自定义平台「{_definition.Name}」接口返回的数据不是有效 JSON，可能是接口发生了变化");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow; // 一次响应里所有 resetInSec 窗口共用同一个"现在"，避免窗口间产生毫秒级漂移。

            var windows = new List<QuotaWindow>();
            BalanceMetric? balance = null;

            foreach (var windowDef in _definition.QuotaWindows.OrderBy(w => w.SortOrder))
            {
                if (windowDef.DataKind == CustomDataKind.Balance)
                {
                    var (parsedBalance, error) = TryParseBalance(root, windowDef);
                    if (parsedBalance is not null)
                    {
                        // ProviderSnapshot 只支持单个 Balance：只取第一个成功解析的余额窗口（类注释里的已知限制）。
                        balance ??= parsedBalance;
                    }
                    else
                    {
                        windows.Add(QuotaWindow.FromError(windowDef.Id, windowDef.Name, error!));
                    }

                    continue;
                }

                windows.Add(ParseQuotaWindow(root, windowDef, now));
            }

            return BuildSnapshot(windows, balance);
        }
    }

    private QuotaWindow ParseQuotaWindow(JsonElement root, CustomQuotaWindowSettings windowDef, DateTimeOffset now)
    {
        var valueElement = JsonPathResolver.TryResolve(root, windowDef.ValuePath);
        if (valueElement is not { } value)
        {
            return QuotaWindow.FromError(windowDef.Id, windowDef.Name,
                $"取值路径 \"{windowDef.ValuePath}\" 没有取到数据");
        }

        if (!JsonPathResolver.TryGetDouble(value, out var raw))
        {
            return QuotaWindow.FromError(windowDef.Id, windowDef.Name,
                $"取值路径 \"{windowDef.ValuePath}\" 取到的是非数字");
        }

        if (!TryResolveResetsAt(root, windowDef, now, out var resetsAt, out var resetError))
        {
            return QuotaWindow.FromError(windowDef.Id, windowDef.Name, resetError!);
        }

        switch (windowDef.DataKind)
        {
            case CustomDataKind.UtilizationPercent:
                return QuotaWindow.FromUtilization(windowDef.Id, windowDef.Name, raw, resetsAt);

            case CustomDataKind.RemainingPercent:
                return QuotaWindow.FromRemaining(windowDef.Id, windowDef.Name, raw, resetsAt);

            case CustomDataKind.UsedValue:
            case CustomDataKind.RemainingValue:
                if (!TryResolveLimit(root, windowDef, out var limit, out var limitError))
                {
                    return QuotaWindow.FromError(windowDef.Id, windowDef.Name, limitError!);
                }

                var percent = raw / limit * 100.0;
                return windowDef.DataKind == CustomDataKind.UsedValue
                    ? QuotaWindow.FromUtilization(windowDef.Id, windowDef.Name, percent, resetsAt)
                    : QuotaWindow.FromRemaining(windowDef.Id, windowDef.Name, percent, resetsAt);

            default:
                return QuotaWindow.FromError(windowDef.Id, windowDef.Name, "数据语义配置无效");
        }
    }

    /// <summary>
    /// 解析窗口重置时间。路径未填 / 本次响应取不到值都视为"这个窗口不显示重置时间"，不算错误；
    /// 取到值但解析不出时间才算该窗口出错。<see cref="CustomResetTimeKind.RelativeSeconds"/>
    /// 走"剩余秒数 + now"换算，其余两种走 <see cref="JsonPathResolver.TryGetDateTimeOffset"/> 的
    /// epoch/ISO 启发式。
    /// </summary>
    private static bool TryResolveResetsAt(JsonElement root, CustomQuotaWindowSettings windowDef, DateTimeOffset now,
        out DateTimeOffset? resetsAt, out string? error)
    {
        resetsAt = null;
        error = null;

        if (string.IsNullOrWhiteSpace(windowDef.ResetsAtPath))
        {
            return true;
        }

        var element = JsonPathResolver.TryResolve(root, windowDef.ResetsAtPath);
        if (element is null)
        {
            return true; // 本次响应没取到值：视为无重置时间，不阻断这个窗口。
        }

        if (windowDef.ResetTimeKind == CustomResetTimeKind.RelativeSeconds)
        {
            if (!JsonPathResolver.TryGetDouble(element, out var seconds))
            {
                error = $"窗口重置时间路径 \"{windowDef.ResetsAtPath}\" 取到的值不是数字（剩余秒数）";
                return false;
            }

            resetsAt = now.AddSeconds(seconds);
            return true;
        }

        if (!JsonPathResolver.TryGetDateTimeOffset(element, out var parsed))
        {
            error = $"窗口重置时间路径 \"{windowDef.ResetsAtPath}\" 取到的值无法解析为时间";
            return false;
        }

        resetsAt = parsed;
        return true;
    }

    /// <summary>
    /// 解析"已使用/剩余数值"换算百分比所需的额度上限：LimitPath 非空时优先且唯一生效
    /// （解析失败不回退到 FixedLimit，避免用一个隐性默认值掩盖配置错误）；否则用 FixedLimit；
    /// 两者都没配或上限 &lt;= 0 都算该窗口配置错误。
    /// </summary>
    private static bool TryResolveLimit(JsonElement root, CustomQuotaWindowSettings windowDef, out double limit, out string? error)
    {
        limit = 0;
        error = null;

        if (!string.IsNullOrWhiteSpace(windowDef.LimitPath))
        {
            var element = JsonPathResolver.TryResolve(root, windowDef.LimitPath);
            if (element is not { } value || !JsonPathResolver.TryGetDouble(value, out limit))
            {
                error = $"额度上限路径 \"{windowDef.LimitPath}\" 没有取到有效数字";
                return false;
            }
        }
        else if (windowDef.FixedLimit is { } fixedLimit)
        {
            limit = fixedLimit;
        }
        else
        {
            error = "该窗口的数据语义需要额度上限，但既未填固定上限也未填上限路径";
            return false;
        }

        if (limit <= 0)
        {
            error = "额度上限必须大于 0";
            return false;
        }

        return true;
    }

    private static (BalanceMetric? balance, string? error) TryParseBalance(JsonElement root, CustomQuotaWindowSettings windowDef)
    {
        var valueElement = JsonPathResolver.TryResolve(root, windowDef.ValuePath);
        if (valueElement is not { } value)
        {
            return (null, $"取值路径 \"{windowDef.ValuePath}\" 没有取到数据");
        }

        if (!JsonPathResolver.TryGetDecimal(value, out var amount))
        {
            return (null, $"取值路径 \"{windowDef.ValuePath}\" 取到的是非数字");
        }

        var currency = string.IsNullOrWhiteSpace(windowDef.Unit) ? "CNY" : windowDef.Unit;
        return (new BalanceMetric(amount, currency), null);
    }

    /// <summary>
    /// 汇总本次解析结果：平台整体状态取"成功窗口里最紧张的百分比"与"余额自身状态"两者中更严重的一个
    /// （null 表示该来源没有意见，不参与比较）。两者都是 null（所有窗口失败 + 没有余额）时，
    /// 这次刷新对整个平台判定为失败，但仍然把各窗口的错误行带上，方便用户定位具体是哪个窗口配错了。
    /// </summary>
    private ProviderSnapshot BuildSnapshot(List<QuotaWindow> windows, BalanceMetric? balance)
    {
        var healthy = windows.Where(w => !w.IsError).ToList();
        ProviderState? windowState = healthy.Count > 0 ? StateFromWorstPercent(healthy.Min(w => w.RemainingPercent)) : null;
        ProviderState? balanceState = balance switch
        {
            null => null,
            { Amount: > 0 } => ProviderState.Available,
            _ => ProviderState.Exhausted,
        };

        var state = MoreSevere(windowState, balanceState);
        if (state is null)
        {
            return new ProviderSnapshot
            {
                ProviderId = ProviderId,
                DisplayName = _definition.Name,
                State = ProviderState.ProviderError,
                QuotaWindows = windows,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                DataSource = _dataSourceUrl,
                ErrorCategory = ErrorCategory.ResponseFormat,
                UserGuidance = $"自定义平台「{_definition.Name}」的全部额度窗口都未能正确解析，请检查各窗口的取值路径配置",
            };
        }

        return new ProviderSnapshot
        {
            ProviderId = ProviderId,
            DisplayName = _definition.Name,
            State = state.Value,
            QuotaWindows = windows,
            Balance = balance,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            DataSource = _dataSourceUrl,
            ErrorCategory = ErrorCategory.None,
        };
    }

    private static ProviderState StateFromWorstPercent(double worst) => worst switch
    {
        <= 0 => ProviderState.Exhausted,
        < 10 => ProviderState.Critical,
        < 30 => ProviderState.Low,
        _ => ProviderState.Available,
    };

    private static ProviderState? MoreSevere(ProviderState? a, ProviderState? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return SeverityRank[a.Value] >= SeverityRank[b.Value] ? a : b;
    }

    private ProviderSnapshot NotConfigured() => new()
    {
        ProviderId = ProviderId,
        DisplayName = _definition.Name,
        State = ProviderState.NotConfigured,
        DataSource = _dataSourceUrl,
        ErrorCategory = ErrorCategory.NotConfigured,
        UserGuidance = $"请在设置页填写自定义平台「{_definition.Name}」的 API Key",
    };

    private ProviderSnapshot BuildEmptySnapshot(ProviderState state, ErrorCategory category, string guidance) => new()
    {
        ProviderId = ProviderId,
        DisplayName = _definition.Name,
        State = state,
        DataSource = _dataSourceUrl,
        ErrorCategory = category,
        UserGuidance = guidance,
    };
}
