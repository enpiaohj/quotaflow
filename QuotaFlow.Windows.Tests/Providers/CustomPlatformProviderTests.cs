using System.Net;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Tests.TestDoubles;

namespace QuotaFlow.Windows.Tests.Providers;

public class CustomPlatformProviderTests
{
    private const string Endpoint = "https://opencode.example/api/usage";

    private static CustomPlatformProvider CreateProvider(
        RecordingHttpMessageHandler handler,
        CustomPlatformSettings definition,
        string? apiKey = "fixture-key-not-real") =>
        new(new HttpClient(handler), definition, () => apiKey);

    /// <summary>单窗口定义：默认剩余百分比语义，取 data.quota_left。configure 可改平台级或
    /// QuotaWindows[0] 的任意字段。</summary>
    private static CustomPlatformSettings Definition(Action<CustomPlatformSettings>? configure = null)
    {
        var d = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "OpenCode",
            Endpoint = Endpoint,
            QuotaWindows =
            [
                new CustomQuotaWindowSettings
                {
                    Id = "window-1",
                    Name = "额度",
                    DataKind = CustomDataKind.RemainingPercent,
                    ValuePath = "data.quota_left",
                    SortOrder = 0,
                },
            ],
        };
        configure?.Invoke(d);
        return d;
    }

    // ---- 鉴权配置 ----

    [Fact]
    public async Task GetSnapshotAsync_Bearer_NoKey_ReturnsNotConfigured()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = CreateProvider(handler, Definition(), apiKey: null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Equal(ErrorCategory.NotConfigured, snapshot.ErrorCategory);
        Assert.Empty(handler.RequestedUris); // 没 Key 不发起请求
    }

    [Fact]
    public async Task GetSnapshotAsync_CustomHeader_NoKey_ReturnsNotConfigured()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = CreateProvider(handler,
            Definition(d =>
            {
                d.AuthKind = CustomAuthKind.CustomHeader;
                d.HeaderName = "X-API-Key";
            }),
            apiKey: null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task GetSnapshotAsync_AuthNone_NoKeyStillQueries()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = """{ "data": { "quota_left": 88 } }""" };
        var provider = CreateProvider(handler,
            Definition(d => d.AuthKind = CustomAuthKind.None),
            apiKey: null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task GetSnapshotAsync_Bearer_SendsBearerHeader()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = """{ "data": { "quota_left": 88 } }""" };
        var provider = CreateProvider(handler, Definition());

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.Available, snapshot.State);
        var headers = Assert.Single(handler.RequestedHeaders);
        Assert.Equal("Bearer fixture-key-not-real", headers["Authorization"]);
        Assert.Equal(Endpoint, handler.RequestedUris.Single().ToString());
        Assert.Equal(Endpoint, snapshot.DataSource);
    }

    [Fact]
    public async Task GetSnapshotAsync_CustomHeader_SendsNamedHeader()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = """{ "data": { "quota_left": 88 } }""" };
        var provider = CreateProvider(handler,
            Definition(d =>
            {
                d.AuthKind = CustomAuthKind.CustomHeader;
                d.HeaderName = "X-API-Key";
            }));

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.Available, snapshot.State);
        var headers = Assert.Single(handler.RequestedHeaders);
        Assert.Equal("fixture-key-not-real", headers["X-API-Key"]);
        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task GetSnapshotAsync_CustomHeader_EmptyHeaderName_ReturnsProviderError()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = CreateProvider(handler,
            Definition(d =>
            {
                d.AuthKind = CustomAuthKind.CustomHeader;
                d.HeaderName = "  ";
            }));

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoWindowsConfigured_ReturnsNotConfiguredWithoutRequest()
    {
        var handler = new RecordingHttpMessageHandler();
        var definition = new CustomPlatformSettings { Id = "custom-1", Name = "Empty", Endpoint = Endpoint, QuotaWindows = [] };
        var provider = CreateProvider(handler, definition);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Empty(handler.RequestedUris);
    }

    // ---- HTTP 状态分类（传输层，与窗口数量无关）----

    [Fact]
    public async Task GetSnapshotAsync_Unauthorized_ReturnsAuthenticationExpired()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.Unauthorized };
        var provider = CreateProvider(handler, Definition());

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.AuthenticationExpired, snapshot.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_TooManyRequests_ReturnsRateLimited()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.TooManyRequests };
        var provider = CreateProvider(handler, Definition());

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.RateLimited, snapshot.State);
        Assert.Equal(ErrorCategory.RateLimited, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_ServerError_ReturnsProviderError()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.InternalServerError };
        var provider = CreateProvider(handler, Definition());

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ServerError, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_InvalidJson_ReturnsProviderError()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = "not json {{{" };
        var provider = CreateProvider(handler, Definition());

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    // ---- 单窗口数据语义 ----

    [Fact]
    public void ParseResponse_RemainingPercent_ConvertsToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 62 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal("window-1", window.Id);
        Assert.Equal("额度", window.DisplayName);
        Assert.Equal(62, window.RemainingPercent);
        Assert.Equal(38, window.UsedPercent);
        Assert.False(window.IsError);
        Assert.Equal(ProviderState.Available, snapshot.State);
    }

    [Fact]
    public void ParseResponse_UtilizationPercent_ConvertsToRemaining()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.UtilizationPercent;
                d.QuotaWindows[0].ValuePath = "data.usage";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "usage": 25 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(75, window.RemainingPercent); // remaining = 100 - used
        Assert.Equal(ProviderState.Available, snapshot.State);
    }

    [Fact]
    public void ParseResponse_UtilizationHundred_ReturnsExhausted()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.UtilizationPercent;
                d.QuotaWindows[0].ValuePath = "data.usage";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "usage": 100 } }""");

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
        Assert.Equal(0, Assert.Single(snapshot.QuotaWindows).RemainingPercent);
    }

    [Fact]
    public void ParseResponse_LowRemaining_ReturnsCritical()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 4 } }""");

        Assert.Equal(ProviderState.Critical, snapshot.State);
    }

    [Fact]
    public void ParseResponse_PercentAboveHundred_ClampsToHundred()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 150 } }""");

        Assert.Equal(100, Assert.Single(snapshot.QuotaWindows).RemainingPercent);
    }

    [Fact]
    public void ParseResponse_PercentNegative_ClampsToZero()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": -20 } }""");

        Assert.Equal(0, Assert.Single(snapshot.QuotaWindows).RemainingPercent);
    }

    [Fact]
    public void ParseResponse_BalancePositive_ReturnsAvailable()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.Balance;
                d.QuotaWindows[0].ValuePath = "data.balance";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "balance": 12.34 } }""");

        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.Empty(snapshot.QuotaWindows); // 余额成功解析时不产生窗口行，走专门的余额展示区
        var balance = Assert.IsType<BalanceMetric>(snapshot.Balance);
        Assert.Equal(12.34m, balance.Amount);
        Assert.Equal("CNY", balance.Currency); // Unit 未填时默认 CNY
    }

    [Fact]
    public void ParseResponse_BalanceZero_ReturnsExhausted()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.Balance;
                d.QuotaWindows[0].ValuePath = "data.balance";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "balance": 0 } }""");

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
        Assert.Equal(0m, snapshot.Balance!.Amount);
    }

    [Fact]
    public void ParseResponse_BalanceWindowFails_ProducesErrorRowAndFailsPlatform()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].Name = "余额";
                d.QuotaWindows[0].DataKind = CustomDataKind.Balance;
                d.QuotaWindows[0].ValuePath = "data.missing_balance";
            }));

        var snapshot = provider.ParseResponse("""{ "data": {} }""");

        // 唯一窗口是余额且解析失败：退化成一行"数据不可用"的错误窗口（已知限制：余额场景没有
        // 独立的错误展示区，借用窗口的错误行机制），且这次是平台唯一的数据来源，整体判定失败。
        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.True(window.IsError);
        Assert.Null(snapshot.Balance);
        Assert.Equal(ProviderState.ProviderError, snapshot.State);
    }

    // ---- 已使用/剩余数值 + 额度上限 ----

    [Fact]
    public void ParseResponse_UsedValueWithFixedLimit_ComputesPercent()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.UsedValue;
                d.QuotaWindows[0].ValuePath = "data.used";
                d.QuotaWindows[0].FixedLimit = 200;
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "used": 50 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.False(window.IsError);
        Assert.Equal(75, window.RemainingPercent); // 50/200=25% 已用 -> 75% 剩余
    }

    [Fact]
    public void ParseResponse_RemainingValueWithLimitPath_ComputesPercent()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.RemainingValue;
                d.QuotaWindows[0].ValuePath = "data.remaining";
                d.QuotaWindows[0].LimitPath = "data.limit";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "remaining": 30, "limit": 120 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(25, window.RemainingPercent); // 30/120=25%
    }

    [Fact]
    public void ParseResponse_LimitPathTakesPrecedenceOverFixedLimit()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.UsedValue;
                d.QuotaWindows[0].ValuePath = "data.used";
                d.QuotaWindows[0].LimitPath = "data.limit";
                d.QuotaWindows[0].FixedLimit = 999; // 应被忽略
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "used": 25, "limit": 100 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(75, window.RemainingPercent); // 25/100=25% 已用，不是 25/999
    }

    [Fact]
    public void ParseResponse_UsedValueWithoutAnyLimit_ReturnsWindowError()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].DataKind = CustomDataKind.UsedValue;
                d.QuotaWindows[0].ValuePath = "data.used";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "used": 50 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.True(window.IsError);
        Assert.Contains("额度上限", window.ErrorMessage);
        Assert.Equal(ProviderState.ProviderError, snapshot.State);
    }

    // ---- 路径错误不臆造 0% ----

    [Fact]
    public void ParseResponse_MissingPath_SoleWindowFails_PlatformReportsError()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": {} }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.True(window.IsError);
        Assert.Contains("data.quota_left", window.ErrorMessage);
    }

    [Fact]
    public void ParseResponse_NonNumericValue_ReturnsWindowError()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": "unlimited" } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.True(window.IsError);
        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_InvalidResetsAt_ReturnsWindowError()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.QuotaWindows[0].ResetsAtPath = "data.resets_at"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at": "not-a-time" } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.True(window.IsError);
        Assert.Equal(ProviderState.ProviderError, snapshot.State);
    }

    [Fact]
    public void ParseResponse_ValidResetsAt_EpochMilliseconds_IsAttachedToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.QuotaWindows[0].ResetsAtPath = "data.resets_at_ms"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at_ms": 1798000000000 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.False(window.IsError);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798000000000), window.ResetsAt);
    }

    [Fact]
    public void ParseResponse_ValidResetsAt_EpochSeconds_IsAttachedToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.QuotaWindows[0].ResetsAtPath = "data.resets_at"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at": 1798000000 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1798000000), window.ResetsAt);
    }

    [Fact]
    public void ParseResponse_ValidResetsAt_Iso8601String_IsAttachedToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.QuotaWindows[0].ResetsAtPath = "data.resets_at"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at": "2026-09-01T00:00:00Z" } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), window.ResetsAt);
    }

    [Fact]
    public void ParseResponse_RelativeSecondsResetsAt_ComputesFromResponseTime()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.QuotaWindows[0].ResetsAtPath = "data.reset_in_sec";
                d.QuotaWindows[0].ResetTimeKind = CustomResetTimeKind.RelativeSeconds;
            }));

        var before = DateTimeOffset.UtcNow;
        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "reset_in_sec": 3600 } }""");
        var after = DateTimeOffset.UtcNow;

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.False(window.IsError);
        Assert.NotNull(window.ResetsAt);
        Assert.InRange(window.ResetsAt!.Value, before.AddSeconds(3600), after.AddSeconds(3600));
    }

    [Fact]
    public void ParseResponse_ArrayIndexValuePath_ResolvesCorrectly()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.QuotaWindows[0].ValuePath = "data.items[0].quota"));

        var snapshot = provider.ParseResponse("""{ "data": { "items": [ { "quota": 66 } ] } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(66, window.RemainingPercent);
    }

    // ---- 多窗口：一次请求、独立解析、互不影响 ----

    [Fact]
    public async Task GetSnapshotAsync_MultipleWindows_SendsExactlyOneRequest()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """{ "usage": { "rolling": { "percent": 10 }, "weekly": { "percent": 20 }, "monthly": { "percent": 30 } } }""",
        };
        var definition = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "OpenCode GO",
            Endpoint = Endpoint,
            QuotaWindows =
            [
                new() { Id = "window-1", Name = "5 小时", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.rolling.percent", SortOrder = 0 },
                new() { Id = "window-2", Name = "每周", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.weekly.percent", SortOrder = 1 },
                new() { Id = "window-3", Name = "每月", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.monthly.percent", SortOrder = 2 },
            ],
        };
        var provider = CreateProvider(handler, definition);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Single(handler.RequestedUris); // 三个窗口，一次请求
        Assert.Equal(3, snapshot.QuotaWindows.Count);
    }

    [Fact]
    public void ParseResponse_OpenCodeGoExample_AllThreeWindowsParseCorrectly()
    {
        // 文档给出的 OpenCode GO 示例响应结构与字段映射，逐字核对。
        var definition = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "OpenCode GO",
            Endpoint = "https://opencode.ai/zen/go/v1/usage",
            AuthKind = CustomAuthKind.BearerKey,
            QuotaWindows =
            [
                new() { Id = "window-1", Name = "5 小时", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.rolling.percent", ResetsAtPath = "usage.rolling.resetsAt", ResetTimeKind = CustomResetTimeKind.Absolute, SortOrder = 0 },
                new() { Id = "window-2", Name = "每周", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.weekly.percent", ResetsAtPath = "usage.weekly.resetsAt", ResetTimeKind = CustomResetTimeKind.Absolute, SortOrder = 1 },
                new() { Id = "window-3", Name = "每月", DataKind = CustomDataKind.UtilizationPercent, ValuePath = "usage.monthly.percent", ResetsAtPath = "usage.monthly.resetsAt", ResetTimeKind = CustomResetTimeKind.Absolute, SortOrder = 2 },
            ],
        };
        var provider = CreateProvider(new RecordingHttpMessageHandler(), definition);

        const string raw = """
        {
          "usage": {
            "rolling": { "percent": 19.5, "resetsAt": "2026-08-28T12:00:00Z" },
            "weekly": { "percent": 29.7, "resetsAt": "2026-09-01T00:00:00Z" },
            "monthly": { "percent": 25.0, "resetsAt": "2026-09-01T00:00:00Z" }
          }
        }
        """;

        var snapshot = provider.ParseResponse(raw);

        Assert.Equal(3, snapshot.QuotaWindows.Count);
        Assert.All(snapshot.QuotaWindows, w => Assert.False(w.IsError));

        var five = snapshot.QuotaWindows[0];
        Assert.Equal("5 小时", five.DisplayName);
        Assert.Equal(80.5, five.RemainingPercent, 3);
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero), five.ResetsAt);

        var weekly = snapshot.QuotaWindows[1];
        Assert.Equal("每周", weekly.DisplayName);
        Assert.Equal(70.3, weekly.RemainingPercent, 3);

        var monthly = snapshot.QuotaWindows[2];
        Assert.Equal("每月", monthly.DisplayName);
        Assert.Equal(75.0, monthly.RemainingPercent, 3);

        Assert.Equal(ProviderState.Available, snapshot.State); // 最紧张窗口剩余 70.3% >= 30
        Assert.Equal("OpenCode GO", snapshot.DisplayName);
    }

    [Fact]
    public void ParseResponse_OneWindowFails_OthersStillSucceedAndOverallStateIgnoresFailedOne()
    {
        var definition = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "Test",
            Endpoint = Endpoint,
            QuotaWindows =
            [
                new() { Id = "window-1", Name = "A", DataKind = CustomDataKind.RemainingPercent, ValuePath = "data.a", SortOrder = 0 },
                new() { Id = "window-2", Name = "B", DataKind = CustomDataKind.RemainingPercent, ValuePath = "data.missing", SortOrder = 1 },
            ],
        };
        var provider = CreateProvider(new RecordingHttpMessageHandler(), definition);

        var snapshot = provider.ParseResponse("""{ "data": { "a": 80 } }""");

        Assert.Equal(2, snapshot.QuotaWindows.Count);
        var windowA = snapshot.QuotaWindows[0];
        var windowB = snapshot.QuotaWindows[1];

        Assert.False(windowA.IsError);
        Assert.Equal(80, windowA.RemainingPercent);

        Assert.True(windowB.IsError);
        Assert.Contains("data.missing", windowB.ErrorMessage);
        Assert.Equal("B", windowB.DisplayName); // 失败窗口仍然带着名字显示，不是被整个丢弃

        // 整体状态只看成功窗口（80% 剩余 -> Available），不因为 B 失败就整体报错。
        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.Equal(ErrorCategory.None, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_AllWindowsFail_PlatformReportsProviderErrorButKeepsPerWindowRows()
    {
        var definition = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "Test",
            Endpoint = Endpoint,
            QuotaWindows =
            [
                new() { Id = "window-1", Name = "A", DataKind = CustomDataKind.RemainingPercent, ValuePath = "data.missing_a", SortOrder = 0 },
                new() { Id = "window-2", Name = "B", DataKind = CustomDataKind.RemainingPercent, ValuePath = "data.missing_b", SortOrder = 1 },
            ],
        };
        var provider = CreateProvider(new RecordingHttpMessageHandler(), definition);

        var snapshot = provider.ParseResponse("""{ "data": {} }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
        Assert.Equal(2, snapshot.QuotaWindows.Count);
        Assert.All(snapshot.QuotaWindows, w => Assert.True(w.IsError));
    }

    // ---- 密钥绝不出现在错误信息/展示文案里 ----

    [Fact]
    public async Task ErrorGuidance_NeverContainsApiKey()
    {
        const string secretKey = "sk-super-secret-key-not-real";
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.Unauthorized };
        var provider = CreateProvider(handler, Definition(), apiKey: secretKey);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.DoesNotContain(secretKey, snapshot.UserGuidance ?? string.Empty);
        Assert.DoesNotContain(secretKey, snapshot.DataSource ?? string.Empty);
        Assert.DoesNotContain(secretKey, snapshot.DisplayName);
        foreach (var window in snapshot.QuotaWindows)
        {
            Assert.DoesNotContain(secretKey, window.ErrorMessage ?? string.Empty);
        }
    }

    // ---- 展示名与标识 ----

    [Fact]
    public void ProviderId_IsCustomId()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        Assert.Equal("custom-1", provider.ProviderId);
    }

    [Fact]
    public void Snapshot_DisplayName_IsConfiguredName()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50 } }""");

        Assert.Equal("OpenCode", snapshot.DisplayName);
    }
}
