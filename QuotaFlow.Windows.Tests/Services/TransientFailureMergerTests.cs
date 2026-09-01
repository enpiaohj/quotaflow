using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="TransientFailureMerger"/> 的测试。
///
/// 背景：限流返回的快照不含额度窗口，卡片按它渲染会把原本好好的额度数字整片抹掉、只剩一行
/// 报错，用户观感是"额度突然没了"。但那份数据几分钟前才刚拿到，配额本身也没变。
/// </summary>
public class TransientFailureMergerTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static ProviderSnapshot WithData(ProviderState state = ProviderState.Available) => new()
    {
        ProviderId = "claude",
        DisplayName = "Claude",
        State = state,
        LastUpdatedAt = Earlier,
        QuotaWindows = [QuotaWindow.FromRemaining("weekly", "7 天", 70, null)],
    };

    private static ProviderSnapshot Failure(ProviderState state, string guidance) => new()
    {
        ProviderId = "claude",
        DisplayName = "Claude",
        State = state,
        UserGuidance = guidance,
    };

    [Fact]
    public void Merge_RateLimitedAfterGoodData_KeepsQuotaWindows()
    {
        var result = TransientFailureMerger.Merge(WithData(), Failure(ProviderState.RateLimited, "查询过于频繁"));

        Assert.Single(result.QuotaWindows);
        Assert.Equal(70, result.QuotaWindows[0].RemainingPercent);
    }

    [Fact]
    public void Merge_RateLimitedAfterGoodData_AdoptsFailureStateAndGuidance()
    {
        // 保留数据不等于掩盖失败：状态和提示都必须是本次的，用户要能看出没刷新成功。
        var result = TransientFailureMerger.Merge(WithData(), Failure(ProviderState.RateLimited, "查询过于频繁"));

        Assert.Equal(ProviderState.RateLimited, result.State);
        Assert.Equal("查询过于频繁", result.UserGuidance);
    }

    [Fact]
    public void Merge_RateLimitedAfterGoodData_KeepsOriginalTimestampSoStalenessStaysHonest()
    {
        // 时间戳必须保持数据实际取得的时刻，界面上的"x 分钟前更新"才不会撒谎。
        var result = TransientFailureMerger.Merge(WithData(), Failure(ProviderState.RateLimited, "查询过于频繁"));

        Assert.Equal(Earlier, result.LastUpdatedAt);
    }

    [Fact]
    public void Merge_RateLimitedWithNoPreviousData_ReturnsIncomingUnchanged()
    {
        // 从来没拿到过数据时不能凭空造出额度——宁可显示错误，也不显示 0%。
        var incoming = Failure(ProviderState.RateLimited, "查询过于频繁");

        var result = TransientFailureMerger.Merge(null, incoming);

        Assert.Empty(result.QuotaWindows);
        Assert.Equal(ProviderState.RateLimited, result.State);
    }

    [Fact]
    public void Merge_AuthenticationExpired_DoesNotHideFailureBehindOldData()
    {
        // 需要用户处理的问题（重新登录）必须立刻暴露，不能被旧数据盖住。
        var result = TransientFailureMerger.Merge(WithData(), Failure(ProviderState.AuthenticationExpired, "请重新登录"));

        Assert.Empty(result.QuotaWindows);
        Assert.Equal(ProviderState.AuthenticationExpired, result.State);
    }

    [Fact]
    public void Merge_ProviderError_DoesNotHideFailureBehindOldData()
    {
        var result = TransientFailureMerger.Merge(WithData(), Failure(ProviderState.ProviderError, "接口异常"));

        Assert.Empty(result.QuotaWindows);
    }

    [Fact]
    public void Merge_SuccessfulRefresh_UsesNewDataNotOld()
    {
        var fresh = new ProviderSnapshot
        {
            ProviderId = "claude",
            DisplayName = "Claude",
            State = ProviderState.Available,
            LastUpdatedAt = Earlier.AddMinutes(5),
            QuotaWindows = [QuotaWindow.FromRemaining("weekly", "7 天", 42, null)],
        };

        var result = TransientFailureMerger.Merge(WithData(), fresh);

        Assert.Equal(42, result.QuotaWindows[0].RemainingPercent);
        Assert.Equal(Earlier.AddMinutes(5), result.LastUpdatedAt);
    }
}
