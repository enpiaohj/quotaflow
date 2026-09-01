using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="RateLimitCountdown"/> 的测试。
///
/// 对应一个真实的观感问题：限流时提示写着「将在 3 分钟后自动重试」，但那是收到 429 那一刻
/// 算出的静态文字，七八分钟后它还写着"3 分钟后"，而旁边「上次更新」的分钟数一直在涨——
/// 用户看到的是「数字在变旧、承诺没兑现」，很自然以为程序卡住了。
/// </summary>
public class RateLimitCountdownTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Describe_CountsDownAsTimePasses()
    {
        // 核心行为：同一个 RetryAfter，随着 now 推进，读数必须变小。
        var until = Now.AddMinutes(10);

        Assert.Equal("约 10 分钟后自动重试", RateLimitCountdown.Describe(until, Now));
        Assert.Equal("约 4 分钟后自动重试", RateLimitCountdown.Describe(until, Now.AddMinutes(6)));
        Assert.Equal("约 30 秒后自动重试", RateLimitCountdown.Describe(until, Now.AddMinutes(9).AddSeconds(30)));
    }

    [Fact]
    public void Describe_AfterDeadline_SaysImminentNotZeroOrNegative()
    {
        Assert.Equal("即将自动重试", RateLimitCountdown.Describe(Now.AddMinutes(-5), Now));
        Assert.Equal("即将自动重试", RateLimitCountdown.Describe(Now, Now));
    }

    [Fact]
    public void Describe_NoServerRetryAfter_ReturnsNullInsteadOfInventingANumber()
    {
        // 服务端没给时间就不编——宁可说得笼统，也不能显示一个猜出来的数字。
        Assert.Null(RateLimitCountdown.Describe(null, Now));
    }

    [Fact]
    public void Describe_LongWait_UsesHours()
    {
        Assert.Equal("约 2.5 小时后自动重试", RateLimitCountdown.Describe(Now.AddMinutes(150), Now));
    }

    [Fact]
    public void Describe_SubMinute_NeverShowsZeroSeconds()
    {
        Assert.Equal("约 1 秒后自动重试", RateLimitCountdown.Describe(Now.AddMilliseconds(200), Now));
    }

    [Fact]
    public void BuildGuidance_WithRetryAfter_IncludesCountdownAndExplainsStaleData()
    {
        var text = RateLimitCountdown.BuildGuidance(Now.AddMinutes(3), Now);

        Assert.Contains("约 3 分钟后自动重试", text, StringComparison.Ordinal);
        // 同时说明"现在看到的数字是上次的"，否则用户会怀疑数据准不准。
        Assert.Contains("上次查到的数据", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGuidance_WithoutRetryAfter_StillExplainsWhatIsHappening()
    {
        var text = RateLimitCountdown.BuildGuidance(null, Now);

        Assert.Contains("稍后会自动重试", text, StringComparison.Ordinal);
        Assert.Contains("上次查到的数据", text, StringComparison.Ordinal);
        Assert.DoesNotContain("分钟", text, StringComparison.Ordinal); // 没有依据就不给数字
    }
}
