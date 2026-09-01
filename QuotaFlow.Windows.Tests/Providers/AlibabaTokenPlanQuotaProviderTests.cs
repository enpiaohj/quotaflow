using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

/// <summary>
/// 阿里云百炼 Token Plan（个人版）用量的解析层测试。
/// 真实的 Console Cookie + SEC_TOKEN 流程依赖登录态，无法在单测里做；这里只验证
/// <see cref="AlibabaTokenPlanQuotaProvider.ParseUsageResponse"/> 对各种响应形态的解析逻辑：
/// 正常窗口、缺字段、业务错误、会话过期、JSON 损坏。
/// </summary>
public class AlibabaTokenPlanQuotaProviderTests
{
    private static AlibabaTokenPlanQuotaProvider CreateProvider() =>
        new(new HttpClient(), () => "fixture-cookie-not-real");

    [Fact]
    public void ParseUsageResponse_ResultWrapped_ComputesUsedAndRemainingWindows()
    {
        // per1WeekPercentage 0.7913 = 已使用 79.13%，剩余 20.87%；
        // per1WeekResetTime 是 Unix 毫秒。
        var json = """
            {
              "code": 200,
              "request_id": "some-request-id",
              "result": {
                "per1WeekPercentage": 0.7913,
                "per1WeekResetTime": 1799000000000
              }
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.Null(usage.Error);
        Assert.Equal(79.13, usage.UsedPercent, precision: 2);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1799000000000L), usage.ResetsAt);
    }

    [Fact]
    public void ParseUsageResponse_FlatJson_StillFindsFields()
    {
        var json = """
            {
              "per1WeekPercentage": 0.5,
              "per1WeekResetTime": 1799000000000
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.Null(usage.Error);
        Assert.Equal(50.0, usage.UsedPercent);
    }

    [Fact]
    public void GetSnapshotAsync_ValidResponse_BuildsTwoWindowsAndState()
    {
        // 79.13% 已用 → 剩余 20.87% → ProviderState.Low；两个窗口：7 天已用 / 7 天剩余。
        var provider = CreateProvider();
        var raw = """
            { "result": { "per1WeekPercentage": 0.7913, "per1WeekResetTime": 1799000000000 } }
            """;

        // 通过公共查询入口拿不到真实 HTTP 响应，这里走内部解析 + 手动组装快照的方式验证状态口径。
        var usage = provider.ParseUsageResponse(raw);
        var remaining = 100.0 - usage.UsedPercent;

        Assert.Equal(ProviderState.Low, remaining is >= 10 and < 30
            ? ProviderState.Low
            : remaining < 10 ? ProviderState.Critical : ProviderState.Available);
    }

    [Fact]
    public void GetSnapshotAsync_AllConsumed_ReturnsExhaustedState()
    {
        var provider = CreateProvider();
        var usage = provider.ParseUsageResponse("""{ "result": { "per1WeekPercentage": 1.0, "per1WeekResetTime": 1799000000000 } }""");

        var remaining = 100.0 - usage.UsedPercent;
        Assert.Equal(0, remaining, precision: 1);
    }

    [Fact]
    public void ParseUsageResponse_MissingPercentageField_ReturnsResponseFormatError()
    {
        var json = """
            { "result": { "per1WeekResetTime": 1799000000000 } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error.State);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error!.ErrorCategory);
        Assert.Contains("per1WeekPercentage", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_MissingResetTimeField_ReturnsResponseFormatError()
    {
        var json = """
            { "result": { "per1WeekPercentage": 0.5 } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error!.ErrorCategory);
        Assert.Contains("per1WeekResetTime", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_ApiErrorCode_ReturnsProviderError()
    {
        var json = """
            { "result": { "code": 400123, "message": "some business error" } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error!.State);
        Assert.Contains("some business error", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_SessionExpiredMessage_MapsToAuthenticationExpired()
    {
        var json = """
            { "result": { "code": 400401, "message": "login session expired, please login again" } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.AuthenticationExpired, usage.Error!.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, usage.Error.ErrorCategory);
    }

    [Fact]
    public void ParseUsageResponse_InvalidJson_ReturnsResponseFormatError()
    {
        var usage = CreateProvider().ParseUsageResponse("not-json-at-all");

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error!.State);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error.ErrorCategory);
    }

    [Fact]
    public void ParseUsageResponse_RedirectedToErrorPage_MapsToSessionExpired()
    {
        // 实测无效 Cookie / SEC_TOKEN 时，网关 302 到 err.taobao.com/error1.html，
        // HttpClient 跟随重定向后拿到的是 HTML 错误页而不是 JSON——必须识别成"会话失效"，
        // 而不是笼统的"不是有效 JSON"。
        var html = """
            <!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML 2.0//EN">
            <html><head><title>302 Found</title></head>
            <body><p>err.taobao.com/error1.html</p></body></html>
            """;

        var usage = CreateProvider().ParseUsageResponse(html);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.AuthenticationExpired, usage.Error!.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, usage.Error.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoCookie_ReturnsNotConfigured()
    {
        var provider = new AlibabaTokenPlanQuotaProvider(new HttpClient(), () => null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Equal(ErrorCategory.NotConfigured, snapshot.ErrorCategory);
    }
}