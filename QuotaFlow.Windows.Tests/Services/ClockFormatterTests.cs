using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

public class ClockFormatterTests
{
    // 2026-08-27 是周四、ISO 第 35 周。
    private static readonly DateTimeOffset Sample = new(2026, 8, 27, 14, 30, 5, TimeSpan.Zero);

    [Fact]
    public void Full_IncludesDateWeekdayWeekAndTime()
    {
        Assert.Equal("2026-08-27 周四 · 第35周 · 14:30:05", ClockFormatter.Format(Sample, ClockDisplayFormat.Full));
    }

    [Fact]
    public void DateWeekdayTime_OmitsWeekNumber()
    {
        Assert.Equal("2026-08-27 周四 · 14:30:05", ClockFormatter.Format(Sample, ClockDisplayFormat.DateWeekdayTime));
    }

    [Fact]
    public void DateTime_OmitsWeekdayAndWeekNumber()
    {
        Assert.Equal("2026-08-27 · 14:30:05", ClockFormatter.Format(Sample, ClockDisplayFormat.DateTime));
    }

    [Fact]
    public void TimeOnly_OnlyShowsTime()
    {
        Assert.Equal("14:30:05", ClockFormatter.Format(Sample, ClockDisplayFormat.TimeOnly));
    }

    [Fact]
    public void FormatsSecondsWithLeadingZeros()
    {
        var t = new DateTimeOffset(2026, 8, 27, 9, 5, 7, TimeSpan.Zero);
        Assert.Equal("09:05:07", ClockFormatter.Format(t, ClockDisplayFormat.TimeOnly));
    }
}
