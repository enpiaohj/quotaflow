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

    private static CustomPlatformSettings Definition(Action<CustomPlatformSettings>? configure = null)
    {
        var d = new CustomPlatformSettings
        {
            Id = "custom-1",
            Name = "OpenCode",
            Endpoint = Endpoint,
            DataKind = CustomDataKind.RemainingPercent,
            ValuePath = "data.quota_left",
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

    // ---- HTTP 状态分类 ----

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

    // ---- 数据语义 ----

    [Fact]
    public void ParseResponse_RemainingPercent_ConvertsToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 62 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal("quota", window.Id);
        Assert.Equal("额度", window.DisplayName);
        Assert.Equal(62, window.RemainingPercent);
        Assert.Equal(38, window.UsedPercent);
        Assert.Equal(ProviderState.Available, snapshot.State);
    }

    [Fact]
    public void ParseResponse_UtilizationPercent_ConvertsToRemaining()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.DataKind = CustomDataKind.UtilizationPercent;
                d.ValuePath = "data.usage";
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
                d.DataKind = CustomDataKind.UtilizationPercent;
                d.ValuePath = "data.usage";
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
    public void ParseResponse_BalancePositive_ReturnsAvailable()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.DataKind = CustomDataKind.Balance;
                d.ValuePath = "data.balance";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "balance": 12.34 } }""");

        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.Empty(snapshot.QuotaWindows);
        var balance = Assert.IsType<BalanceMetric>(snapshot.Balance);
        Assert.Equal(12.34m, balance.Amount);
        Assert.Equal("CNY", balance.Currency);
    }

    [Fact]
    public void ParseResponse_BalanceZero_ReturnsExhausted()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d =>
            {
                d.DataKind = CustomDataKind.Balance;
                d.ValuePath = "data.balance";
            }));

        var snapshot = provider.ParseResponse("""{ "data": { "balance": 0 } }""");

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
        Assert.Equal(0m, snapshot.Balance!.Amount);
    }

    // ---- 路径错误不臆造 0% ----

    [Fact]
    public void ParseResponse_MissingPath_ReturnsResponseFormat()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": {} }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
        Assert.Contains("data.quota_left", snapshot.UserGuidance);
        Assert.True(snapshot.QuotaWindows.Count == 0);
    }

    [Fact]
    public void ParseResponse_NonNumericValue_ReturnsResponseFormat()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(), Definition());

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": "unlimited" } }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_InvalidResetsAt_ReturnsResponseFormat()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.ResetsAtPath = "data.resets_at"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at": "not-a-time" } }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_ValidResetsAt_IsAttachedToWindow()
    {
        var provider = CreateProvider(new RecordingHttpMessageHandler(),
            Definition(d => d.ResetsAtPath = "data.resets_at_ms"));

        var snapshot = provider.ParseResponse("""{ "data": { "quota_left": 50, "resets_at_ms": 1798000000000 } }""");

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798000000000), window.ResetsAt);
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
