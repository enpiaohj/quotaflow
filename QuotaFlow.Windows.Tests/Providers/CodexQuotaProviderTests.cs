using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class CodexQuotaProviderTests
{
    private static CodexQuotaProvider CreateProvider() => new(new HttpClient());

    [Fact]
    public void ParseResponse_MapsPrimaryAndSecondaryWindows()
    {
        var json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 9, "limit_window_seconds": 18000, "reset_at": 1798000000 },
                "secondary_window": { "used_percent": 34, "limit_window_seconds": 604800, "reset_at": 1798500000 }
              }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(2, snapshot.QuotaWindows.Count);
        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "five_hour" && w.UsedPercent == 9);
        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "seven_day" && w.UsedPercent == 34);
    }

    [Fact]
    public void ParseResponse_OnlyPrimaryWindow_SecondaryOmittedNotFaked()
    {
        var json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 9, "limit_window_seconds": 18000, "reset_at": 1798000000 }
              }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Single(snapshot.QuotaWindows);
    }

    [Theory]
    [InlineData(18_000, "five_hour")]
    [InlineData(604_800, "seven_day")]
    [InlineData(2_592_000, "30_day")]
    [InlineData(3_600, "1_hour")]
    [InlineData(86_400, "1_day")]
    public void WindowSecondsMapToExpectedTierNames(long seconds, string expectedId)
    {
        var json = $$"""
            { "rate_limit": { "primary_window": { "used_percent": 1, "limit_window_seconds": {{seconds}} } } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(expectedId, snapshot.QuotaWindows[0].Id);
    }

    [Fact]
    public void ParseResponse_ZeroRemaining_ReturnsExhausted()
    {
        var json = """
            { "rate_limit": { "primary_window": { "used_percent": 100, "limit_window_seconds": 18000, "reset_at": 1798000000 } } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
    }

    [Fact]
    public void ParseResponse_NoRateLimitField_ReturnsProviderError()
    {
        var snapshot = CreateProvider().ParseResponse("{}");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }
}
