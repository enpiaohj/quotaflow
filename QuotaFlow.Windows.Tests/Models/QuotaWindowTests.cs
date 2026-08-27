using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Tests.Models;

public class QuotaWindowTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(10, 90)]
    [InlineData(69, 31)]
    [InlineData(100, 0)]
    public void FromUtilization_ConvertsToRemainingCorrectly(double utilization, double expectedRemaining)
    {
        var window = QuotaWindow.FromUtilization("five_hour", "5 小时", utilization, null);

        Assert.Equal(expectedRemaining, window.RemainingPercent);
        Assert.Equal(utilization, window.UsedPercent);
    }

    [Theory]
    [InlineData(150)] // 服务端异常值：已用超过 100%
    [InlineData(-5)]  // 服务端异常值：负数
    public void FromUtilization_ClampsOutOfRangeValuesTo0To100(double utilization)
    {
        var window = QuotaWindow.FromUtilization("five_hour", "5 小时", utilization, null);

        Assert.InRange(window.RemainingPercent, 0, 100);
        Assert.InRange(window.UsedPercent, 0, 100);
    }

    [Fact]
    public void FromRemaining_UsesGivenValueDirectlyWithoutInverting()
    {
        // MiniMax 等接口直接给"剩余百分比"，不能像 Claude 那样再做一次 100-x 的反转。
        var window = QuotaWindow.FromRemaining("five_hour", "5 小时", 26.0, null);

        Assert.Equal(26.0, window.RemainingPercent);
        Assert.Equal(74.0, window.UsedPercent);
    }

    [Fact]
    public void RemainingDuration_ReturnsNull_WhenNoResetTime()
    {
        var window = new QuotaWindow("five_hour", "5 小时", 90, 10, null);

        Assert.Null(window.RemainingDuration());
    }

    [Fact]
    public void RemainingDuration_ReturnsZero_WhenResetTimeAlreadyPassed()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var window = new QuotaWindow("five_hour", "5 小时", 90, 10, now.AddHours(-1));

        Assert.Equal(TimeSpan.Zero, window.RemainingDuration(now));
    }

    [Fact]
    public void RemainingDuration_ComputesPositiveDelta()
    {
        var now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var resetsAt = now.AddHours(2).AddMinutes(20);
        var window = new QuotaWindow("five_hour", "5 小时", 90, 10, resetsAt);

        var remaining = window.RemainingDuration(now);

        Assert.NotNull(remaining);
        Assert.Equal(TimeSpan.FromMinutes(140), remaining.Value);
    }
}
