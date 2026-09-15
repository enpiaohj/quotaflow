using System.Net;
using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Tests.TestDoubles;

namespace QuotaFlow.Windows.Tests.Providers;

/// <summary>
/// 火山方舟 Coding Plan / Agent Plan Provider 测试。只用假凭据
/// <c>AKLT_TEST_ONLY_123456</c> / <c>TEST_SECRET_NEVER_VALID</c>，不涉及真实网络请求
/// （<see cref="RecordingHttpMessageHandler"/> 桩掉了 HTTP 层）。
/// </summary>
public class VolcengineArkProviderTests
{
    private const string FakeAccessKeyId = "AKLT_TEST_ONLY_123456";
    private const string FakeSecretAccessKey = "TEST_SECRET_NEVER_VALID";

    private static VolcengineArkProvider CreateProvider(
        RecordingHttpMessageHandler handler, string? accessKeyId = FakeAccessKeyId, string? secretAccessKey = FakeSecretAccessKey,
        string? displayNameOverride = null) =>
        new(new HttpClient(handler), () => accessKeyId, () => secretAccessKey, displayNameOverride: displayNameOverride);

    private static JsonElement ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- 未配置 ----

    [Fact]
    public async Task GetSnapshotAsync_NoAccessKeyId_ReturnsNotConfiguredWithoutRequest()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = CreateProvider(handler, accessKeyId: null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Equal(ErrorCategory.NotConfigured, snapshot.ErrorCategory);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoSecretAccessKey_ReturnsNotConfiguredWithoutRequest()
    {
        var handler = new RecordingHttpMessageHandler();
        var provider = CreateProvider(handler, secretAccessKey: null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Empty(handler.RequestedUris);
    }

    // ---- Coding Plan 解析：按 Level 匹配，不按数组下标 ----

    [Fact]
    public void ParseCodingPlan_MatchesByLevelField_NotArrayIndex()
    {
        // 顺序刻意打乱（monthly 在最前面），验证不是按数组下标 [0]/[1]/[2] 取值。
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "monthly", "Percent": 11, "ResetTime": 1798700000 },
                { "Level": "session", "Percent": 23, "ResetTime": 1798000000 },
                { "Level": "weekly", "Percent": 18, "ResetTime": 1798300000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Equal(3, windows.Count);
        // 输出顺序固定为 session -> weekly -> monthly，与输入数组顺序无关。
        Assert.Equal("session", windows[0].Id);
        Assert.Equal(23, windows[0].UsedPercent);
        Assert.Equal("weekly", windows[1].Id);
        Assert.Equal(18, windows[1].UsedPercent);
        Assert.Equal("monthly", windows[2].Id);
        Assert.Equal(11, windows[2].UsedPercent);
    }

    [Fact]
    public void ParseCodingPlan_ArrayOrderChanged_StillMapsCorrectly()
    {
        // 服务端把顺序换成 weekly/monthly/session（回归防御：早期实现若按下标取值，
        // 这里会把 weekly 的值错误地当成 session）。
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "weekly", "Percent": 40, "ResetTime": 1798300000 },
                { "Level": "monthly", "Percent": 50, "ResetTime": 1798700000 },
                { "Level": "session", "Percent": 60, "ResetTime": 1798000000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Equal(60, windows.Single(w => w.Id == "session").UsedPercent);
        Assert.Equal(40, windows.Single(w => w.Id == "weekly").UsedPercent);
        Assert.Equal(50, windows.Single(w => w.Id == "monthly").UsedPercent);
    }

    [Fact]
    public void ParseCodingPlan_MissingSessionWindow_OnlyWeeklyAndMonthlyReturned()
    {
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "weekly", "Percent": 18, "ResetTime": 1798300000 },
                { "Level": "monthly", "Percent": 11, "ResetTime": 1798700000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Equal(2, windows.Count);
        Assert.DoesNotContain(windows, w => w.Id == "session");
    }

    [Fact]
    public void ParseCodingPlan_MissingWeeklyWindow_OnlySessionAndMonthlyReturned()
    {
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "session", "Percent": 23, "ResetTime": 1798000000 },
                { "Level": "monthly", "Percent": 11, "ResetTime": 1798700000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Equal(2, windows.Count);
        Assert.DoesNotContain(windows, w => w.Id == "weekly");
    }

    [Fact]
    public void ParseCodingPlan_MissingMonthlyWindow_OnlySessionAndWeeklyReturned()
    {
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "session", "Percent": 23, "ResetTime": 1798000000 },
                { "Level": "weekly", "Percent": 18, "ResetTime": 1798300000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Equal(2, windows.Count);
        Assert.DoesNotContain(windows, w => w.Id == "monthly");
    }

    [Fact]
    public void ParseCodingPlan_EmptyQuotaUsage_ReturnsNoWindows()
    {
        var result = ParseResult("""{ "QuotaUsage": [] }""");

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Empty(windows);
    }

    [Fact]
    public void ParseCodingPlan_UnknownLevel_SkippedWithoutGuessing()
    {
        var result = ParseResult("""
            { "QuotaUsage": [
                { "Level": "daily", "Percent": 5, "ResetTime": 1798000000 },
                { "Level": "session", "Percent": 23, "ResetTime": 1798000000 }
            ] }
            """);

        var windows = VolcengineArkProvider.ParseCodingPlan(result);

        Assert.Single(windows);
        Assert.Equal("session", windows[0].Id);
    }

    // ---- Percent 边界（已使用百分比语义，不是剩余）----

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(23, 23, 77)]
    [InlineData(150, 100, 0)] // 服务端异常值裁剪到 100
    [InlineData(-10, 0, 100)] // 服务端异常值裁剪到 0
    public void ParseCodingPlan_PercentBoundary_ClampsAndComputesRemaining(
        double rawPercent, double expectedUsed, double expectedRemaining)
    {
        var result = ParseResult($$"""{ "QuotaUsage": [ { "Level": "session", "Percent": {{rawPercent}}, "ResetTime": 1798000000 } ] }""");

        var window = Assert.Single(VolcengineArkProvider.ParseCodingPlan(result));

        Assert.Equal(expectedUsed, window.UsedPercent);
        Assert.Equal(expectedRemaining, window.RemainingPercent);
    }

    // ---- ResetTimestamp：秒 vs 毫秒 ----

    [Fact]
    public void ParseResetEpoch_SecondsMagnitude_InterpretedAsSeconds()
    {
        // 1798000000（10 位）远小于毫秒判别阈值，按秒解释。
        var resets = VolcengineArkProvider.ParseResetEpoch(1798000000);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1798000000), resets);
    }

    [Fact]
    public void ParseResetEpoch_MillisecondsMagnitude_InterpretedAsMilliseconds()
    {
        // 1798000000000（13 位）远大于阈值，按毫秒解释。
        var resets = VolcengineArkProvider.ParseResetEpoch(1798000000000);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798000000000), resets);
    }

    [Fact]
    public void ParseResetEpoch_NullOrNonPositive_ReturnsNullNotFabricated()
    {
        Assert.Null(VolcengineArkProvider.ParseResetEpoch(null));
        Assert.Null(VolcengineArkProvider.ParseResetEpoch(0));
        Assert.Null(VolcengineArkProvider.ParseResetEpoch(-1));
    }

    // ---- Agent Plan 解析 ----

    [Fact]
    public void ParseAgentPlan_ComputesUsedPercentFromUsedAndQuota()
    {
        var result = ParseResult("""
            {
              "AFPFiveHour": { "Used": 30, "Quota": 100, "ResetTime": 1798000000000 },
              "AFPWeekly": { "Used": 200, "Quota": 1000, "ResetTime": 1798300000000 },
              "AFPMonthly": { "Used": 0, "Quota": 0 }
            }
            """);

        var windows = VolcengineArkProvider.ParseAgentPlan(result);

        // AFPMonthly 的 Quota=0 视为未订阅这一档，跳过——不展示 0/0。
        Assert.Equal(2, windows.Count);
        Assert.Equal(30, Assert.Single(windows, w => w.Id == "session").UsedPercent);
        Assert.Equal(20, Assert.Single(windows, w => w.Id == "weekly").UsedPercent);
    }

    [Fact]
    public void ParseAgentPlan_AllZeroQuota_ReturnsNoWindows()
    {
        var result = ParseResult("""
            { "AFPFiveHour": { "Used": 0, "Quota": 0 }, "AFPWeekly": { "Used": 0, "Quota": 0 }, "AFPMonthly": { "Used": 0, "Quota": 0 } }
            """);

        Assert.Empty(VolcengineArkProvider.ParseAgentPlan(result));
    }

    // ---- 无套餐订阅（Coding 与 Agent 均返回空）----

    [Fact]
    public async Task GetSnapshotAsync_NoSubscriptionOnEitherPlan_ReturnsNoPlanError()
    {
        // Coding 与 Agent 两次请求都会命中同一个 handler，返回同一份"两个接口都没有套餐行"的响应体。
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """{ "ResponseMetadata": {}, "Result": { "QuotaUsage": [] } }""",
        };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.NoPlan, snapshot.ErrorCategory);
        Assert.Equal(2, handler.RequestedUris.Count); // 依次探测 Coding、再探测 Agent
        Assert.Contains("GetCodingPlanUsage", handler.RequestedUris[0].Query);
        Assert.Contains("GetAFPUsage", handler.RequestedUris[1].Query);
    }

    [Fact]
    public async Task GetSnapshotAsync_CodingPlanHasWindows_DoesNotProbeAgentPlan()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """
                { "ResponseMetadata": {}, "Result": { "QuotaUsage": [
                    { "Level": "session", "Percent": 23, "ResetTime": 1798000000 }
                ] } }
                """,
        };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.Available, snapshot.State);
        Assert.Single(handler.RequestedUris); // Coding Plan 已有数据，不再探测 Agent Plan
    }

    // ---- 403 / 业务错误映射 ----

    [Fact]
    public async Task GetSnapshotAsync_HttpForbidden_ReturnsAuthenticationExpiredWithChineseGuidance()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "{}" };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.AuthenticationExpired, snapshot.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, snapshot.ErrorCategory);
        Assert.DoesNotContain("403", snapshot.UserGuidance); // 不能把原始 HTTP 状态码直接抛给用户
        Assert.Contains("Access Key", snapshot.UserGuidance);
    }

    [Fact]
    public void ClassifyBusinessError_SignatureError_MapsToSignatureGuidance()
    {
        var snapshot = VolcengineArkProvider.ClassifyBusinessError(
            HttpStatusCode.OK, "SignatureDoesNotMatch", "the signature you provided does not match");

        Assert.Equal(ProviderState.AuthenticationExpired, snapshot.State);
        Assert.Contains("签名校验失败", snapshot.UserGuidance);
    }

    [Fact]
    public void ClassifyBusinessError_ClockSkew_MapsToClockGuidance()
    {
        var snapshot = VolcengineArkProvider.ClassifyBusinessError(
            HttpStatusCode.OK, "RequestTimeTooSkewed", "request time too skewed");

        Assert.Equal(ErrorCategory.AuthenticationExpired, snapshot.ErrorCategory);
        Assert.Contains("系统时间偏差", snapshot.UserGuidance);
    }

    [Fact]
    public void ClassifyBusinessError_PermissionDenied_MapsToPermissionGuidance()
    {
        var snapshot = VolcengineArkProvider.ClassifyBusinessError(
            HttpStatusCode.OK, "AccessDenied", "you are not authorized to perform this action");

        Assert.Contains("无权读取", snapshot.UserGuidance);
    }

    [Fact]
    public void ClassifyBusinessError_RateLimited_MapsToRateLimitedState()
    {
        var snapshot = VolcengineArkProvider.ClassifyBusinessError(
            HttpStatusCode.OK, "QpsLimitExceeded", "too many requests");

        Assert.Equal(ProviderState.RateLimited, snapshot.State);
        Assert.Equal(ErrorCategory.RateLimited, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_TooManyRequests_ReturnsRateLimited()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "{}" };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.RateLimited, snapshot.State);
        Assert.Equal(ErrorCategory.RateLimited, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_InvalidJson_ReturnsResponseFormatError()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = "not-json-at-all" };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_MissingResultField_ReturnsResponseFormatErrorWithShape()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = """{ "SomeOtherField": 1 }""" };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
        Assert.Contains("Result", snapshot.UserGuidance);
    }

    // ---- AK/SK 绝不出现在任何输出里 ----

    [Fact]
    public async Task GetSnapshotAsync_ErrorPath_NeverLeaksAccessKeyOrSecret()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "{}" };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.DoesNotContain(FakeAccessKeyId, snapshot.UserGuidance ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretAccessKey, snapshot.UserGuidance ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecretAccessKey, snapshot.DataSource ?? string.Empty, StringComparison.Ordinal);
        foreach (var header in handler.RequestedHeaders)
        {
            Assert.All(header.Values, v => Assert.DoesNotContain(FakeSecretAccessKey, v, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_SuccessPath_NeverLeaksSecretAccessKeyInAnyHeaderOrUrl()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """
                { "ResponseMetadata": {}, "Result": { "QuotaUsage": [
                    { "Level": "session", "Percent": 23, "ResetTime": 1798000000 }
                ] } }
                """,
        };
        var provider = CreateProvider(handler);

        await provider.GetSnapshotAsync();

        Assert.All(handler.RequestedUris, uri => Assert.DoesNotContain(FakeSecretAccessKey, uri.ToString(), StringComparison.Ordinal));
        foreach (var header in handler.RequestedHeaders)
        {
            Assert.All(header.Values, v => Assert.DoesNotContain(FakeSecretAccessKey, v, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("AKLT_TEST_ONLY_123456", "AKLT****3456")]
    [InlineData("SHORT", "*****")]
    [InlineData("", "")]
    public void MaskAccessKeyId_MasksMiddleSection(string input, string expected)
    {
        Assert.Equal(expected, VolcengineArkProvider.MaskAccessKeyId(input));
    }

    // ---- 成功路径整体状态判定 ----

    [Fact]
    public async Task GetSnapshotAsync_WorstWindowExhausted_ReturnsExhaustedState()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """
                { "ResponseMetadata": {}, "Result": { "QuotaUsage": [
                    { "Level": "session", "Percent": 100, "ResetTime": 1798000000 },
                    { "Level": "weekly", "Percent": 10, "ResetTime": 1798000000 }
                ] } }
                """,
        };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
        Assert.Equal("volcengine-ark", snapshot.ProviderId);
        Assert.Equal("火山方舟 Coding Plan", snapshot.DisplayName);
    }

    // ---- 套餐显示名覆盖（官方接口不返回 Lite/Pro，用户可自行填写）----

    [Fact]
    public async Task GetSnapshotAsync_DisplayNameOverride_UsedInSuccessSnapshot()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """
                { "ResponseMetadata": {}, "Result": { "QuotaUsage": [
                    { "Level": "session", "Percent": 23, "ResetTime": 1798000000 }
                ] } }
                """,
        };
        var provider = CreateProvider(handler, displayNameOverride: "Coding Plan Pro");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("Coding Plan Pro", snapshot.DisplayName);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoDisplayNameOverride_UsesGenericDefaultNotForcedPro()
    {
        var handler = new RecordingHttpMessageHandler
        {
            ResponseBody = """
                { "ResponseMetadata": {}, "Result": { "QuotaUsage": [
                    { "Level": "session", "Percent": 23, "ResetTime": 1798000000 }
                ] } }
                """,
        };
        var provider = CreateProvider(handler);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("火山方舟 Coding Plan", snapshot.DisplayName);
        Assert.DoesNotContain("Pro", snapshot.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain("Lite", snapshot.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_DisplayNameOverride_AlsoAppliesToErrorSnapshot()
    {
        var handler = new RecordingHttpMessageHandler { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "{}" };
        var provider = CreateProvider(handler, displayNameOverride: "Coding Plan Pro");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("Coding Plan Pro", snapshot.DisplayName);
    }
}
