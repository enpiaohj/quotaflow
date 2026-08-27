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
    public void ParseResponse_UnknownWindow_HiddenByDefault()
    {
        // 服务端新增了未预先建模的窗口（真实联调遇到过的 nimbus_quill 就是这种：0% 使用、
        // 无重置时间）。默认不展示——避免面板上冒出看不懂的英文名。
        var json = """
            {
              "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" },
              "nimbus_quill": { "utilization": 0 }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "five_hour");
        Assert.DoesNotContain(snapshot.QuotaWindows, w => w.Id == "nimbus_quill");
    }

    [Fact]
    public void ParseResponse_UnknownWindow_ShownWhenEnabled_WithChineseLabel()
    {
        // 用户在设置里显式开启"显示未识别窗口"后才展示，且统一换成中文标签、保留原始 id。
        var json = """
            {
              "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" },
              "nimbus_quill": { "utilization": 0 }
            }
            """;

        var provider = new ClaudeQuotaProvider(new HttpClient(), includeUnknownWindows: () => true);
        var snapshot = provider.ParseResponse(json);

        var unknown = Assert.Single(snapshot.QuotaWindows, w => w.Id == "nimbus_quill");
        Assert.Contains("其他额度", unknown.DisplayName);
    }

    [Fact]
    public void ParseResponse_OnlyUnknownWindow_WhenHidden_ReturnsProviderError()
    {
        // 全部都是未识别窗口且默认隐藏时，确实没有可展示的东西，按"无可识别窗口"处理。
        var snapshot = CreateProvider().ParseResponse("""{ "nimbus_quill": { "utilization": 0 } }""");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    [Fact]
    public void ParseResponse_SevenDayOmelette_IsRecognizedAsDesignWindow()
    {
        // 官方新增的 Claude Design 周额度窗口，应被识别而不是当作未知窗口隐藏。
        var json = """
            { "seven_day_omelette": { "utilization": 20, "resets_at": "2026-08-30T10:00:00Z" } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        var window = Assert.Single(snapshot.QuotaWindows);
        Assert.Equal("seven_day_omelette", window.Id);
        Assert.Contains("Design", window.DisplayName);
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
    [InlineData(100, ProviderState.Exhausted)] // utilization 100 -> 剩余 0%，已用尽
    [InlineData(95, ProviderState.Critical)]   // utilization 95 -> 剩余 5%
    [InlineData(15, ProviderState.Available)]  // utilization 15 -> 剩余 85%
    public void ParseResponse_StateReflectsWorstRemainingWindow(double utilization, ProviderState expected)
    {
        var json = $$"""
            { "five_hour": { "utilization": {{utilization}}, "resets_at": null } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(expected, snapshot.State);
    }
}
