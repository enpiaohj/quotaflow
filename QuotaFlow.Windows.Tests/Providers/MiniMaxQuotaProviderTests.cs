using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class MiniMaxQuotaProviderTests
{
    private static MiniMaxQuotaProvider CreateProvider() =>
        new(new HttpClient(), () => "fixture-key-not-real");

    [Fact]
    public void ParseResponse_FiveHourWindow_UsesRemainingPercentDirectly()
    {
        // MiniMax 给的是"剩余百分比"，不能像 Claude 那样再做一次 100-x 反转。
        var json = """
            {
              "base_resp": { "status_code": 0, "status_msg": "" },
              "model_remains": [
                { "model_name": "general", "current_interval_remaining_percent": 26, "end_time": 1798000000000, "current_weekly_status": 3 },
                { "model_name": "video", "current_interval_remaining_percent": 0 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        var fiveHour = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal("five_hour", fiveHour.Id);
        Assert.Equal(26, fiveHour.RemainingPercent);
    }

    [Fact]
    public void ParseResponse_WeeklyStatusNotActive_WeeklyWindowOmitted()
    {
        // current_weekly_status != 1 表示该套餐没有周限额，不应该展示恒为 100% 的假窗口。
        var json = """
            {
              "base_resp": { "status_code": 0 },
              "model_remains": [
                { "model_name": "general", "current_interval_remaining_percent": 50, "current_weekly_status": 3, "current_weekly_remaining_percent": 100 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.DoesNotContain(snapshot.QuotaWindows, w => w.Id == "weekly_limit");
    }

    [Fact]
    public void ParseResponse_WeeklyStatusActive_WeeklyWindowIncluded()
    {
        var json = """
            {
              "base_resp": { "status_code": 0 },
              "model_remains": [
                { "model_name": "general", "current_interval_remaining_percent": 50, "current_weekly_status": 1, "current_weekly_remaining_percent": 38, "weekly_end_time": 1798500000000 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        var weekly = Assert.Single(snapshot.QuotaWindows, w => w.Id == "weekly_limit");
        Assert.Equal(38, weekly.RemainingPercent);
    }

    [Fact]
    public void ParseResponse_WeeklyStatusExhausted_WeeklyWindowStillIncluded()
    {
        // 真实抓包：周额度用尽时 current_weekly_status 是 2（不是 1），
        // current_weekly_remaining_percent 仍是真实的 0%——早期实现只认 status==1，
        // 会把这个真实的"已用尽"数据当成"没有周限额"隐藏掉，看起来就像"minimax 只有 5 小时额度"。
        var json = """
            {
              "base_resp": { "status_code": 0 },
              "model_remains": [
                { "model_name": "general", "current_interval_remaining_percent": 100, "current_weekly_status": 2, "current_weekly_remaining_percent": 0, "weekly_end_time": 1798500000000 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        var weekly = Assert.Single(snapshot.QuotaWindows, w => w.Id == "weekly_limit");
        Assert.Equal(0, weekly.RemainingPercent);
        Assert.Equal(ProviderState.Exhausted, snapshot.State);
    }

    [Fact]
    public void ParseResponse_ZeroRemaining_ReturnsExhausted()
    {
        var json = """
            {
              "base_resp": { "status_code": 0 },
              "model_remains": [
                { "model_name": "general", "current_interval_remaining_percent": 0, "current_weekly_status": 3 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
    }

    [Fact]
    public void ParseResponse_SkipsVideoModel_OnlyUsesGeneral()
    {
        var json = """
            {
              "base_resp": { "status_code": 0 },
              "model_remains": [
                { "model_name": "video", "current_interval_remaining_percent": 0 }
              ]
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
    }

    [Fact]
    public void ParseResponse_BusinessErrorCode_ReturnsProviderError()
    {
        var json = """
            { "base_resp": { "status_code": 1004, "status_msg": "invalid api key" } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Contains("invalid api key", snapshot.UserGuidance);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoApiKey_ReturnsNotConfigured()
    {
        var provider = new MiniMaxQuotaProvider(new HttpClient(), () => null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Equal(ErrorCategory.NotConfigured, snapshot.ErrorCategory);
    }
}
