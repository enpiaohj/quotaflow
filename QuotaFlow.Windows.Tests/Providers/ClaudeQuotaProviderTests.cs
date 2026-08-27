using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class ClaudeQuotaProviderTests
{
    private static ClaudeQuotaProvider CreateProvider() => new(new HttpClient());

    [Fact]
    public void ParseResponse_AllKnownWindowsPresent_ProducesAllWindows()
    {
        var json = """
            {
              "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" },
              "seven_day": { "utilization": 69, "resets_at": "2026-08-30T10:00:00Z" }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(2, snapshot.QuotaWindows.Count);
        var fiveHour = snapshot.QuotaWindows.Single(w => w.Id == "five_hour");
        Assert.Equal(90, fiveHour.RemainingPercent); // utilization 10 -> remaining 90
    }

    [Fact]
    public void ParseResponse_MissingWindow_IsHiddenNotFakedAsZero()
    {
        // 只返回 five_hour，缺失的 seven_day / seven_day_opus / seven_day_sonnet 不应该出现在结果里，
        // 更不能伪造成 0%。
        var json = """
            { "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Single(snapshot.QuotaWindows);
        Assert.Equal("five_hour", snapshot.QuotaWindows[0].Id);
    }

    [Fact]
    public void ParseResponse_UnknownWindow_IsStillSurfaced()
    {
        // 服务端新增了一个未预先建模的窗口名，不应该被丢弃——这在真实联调中已经遇到过
        // （官方接口曾返回过一个未文档化的窗口）。
        var json = """
            {
              "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" },
              "some_new_window": { "utilization": 0 }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "some_new_window");
    }

    [Fact]
    public void ParseResponse_ExtraUsageField_IsNotMisreadAsAWindow()
    {
        var json = """
            {
              "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" },
              "extra_usage": { "is_enabled": true, "monthly_limit": 100, "used_credits": 5, "utilization": 5, "currency": "USD" }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.DoesNotContain(snapshot.QuotaWindows, w => w.Id == "extra_usage");
    }

    [Fact]
    public void ParseResponse_NoRecognizableWindow_ReturnsProviderError()
    {
        var snapshot = CreateProvider().ParseResponse("{}");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_InvalidJson_ReturnsProviderErrorWithoutThrowing()
    {
        var snapshot = CreateProvider().ParseResponse("not json");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Theory]
    [InlineData(95, ProviderState.Critical)]  // utilization 95 -> 剩余 5%
    [InlineData(15, ProviderState.Available)] // utilization 15 -> 剩余 85%
    public void ParseResponse_StateReflectsWorstRemainingWindow(double utilization, ProviderState expected)
    {
        var json = $$"""
            { "five_hour": { "utilization": {{utilization}}, "resets_at": null } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(expected, snapshot.State);
    }
}
