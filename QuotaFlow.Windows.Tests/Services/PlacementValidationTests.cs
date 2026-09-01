using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="PlacementValidation"/> 的测试。
///
/// 对应一次真实事故：WPF 的 <c>Window.Width/Height</c> 在窗口尚未显式设定尺寸时是 NaN，
/// 这样的位置写进设置后，<c>System.Text.Json</c> 序列化直接抛 <see cref="ArgumentException"/>，
/// 而设置存储对写盘失败是静默降级的——表现为「界面上设置改了也生效了，磁盘一个字节没写，
/// 重启后全部丢失，且没有任何提示」。实测稳定复现：勾选紧凑布局、切换始终置顶，重启全回退。
/// </summary>
public class PlacementValidationTests
{
    private static SavedWindowPlacement Good() => new()
    {
        MonitorDeviceName = @"\\.\DISPLAY1",
        LeftDip = 100,
        TopDip = 200,
        WidthDip = 420,
        HeightDip = 560,
        SavedDpiX = 96,
        SavedDpiY = 96,
    };

    [Fact]
    public void IsPersistable_GoodPlacement_True() => Assert.True(PlacementValidation.IsPersistable(Good()));

    [Fact]
    public void IsPersistable_Null_False() => Assert.False(PlacementValidation.IsPersistable(null));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void IsPersistable_NonFiniteSize_False(double bad)
    {
        // NaN 是实测中真正出现的那个值（未显式设尺寸的 WPF 窗口）。
        var p = Good();
        p.WidthDip = bad;

        Assert.False(PlacementValidation.IsPersistable(p));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void IsPersistable_NonFinitePosition_False(double bad)
    {
        var p = Good();
        p.LeftDip = bad;

        Assert.False(PlacementValidation.IsPersistable(p));
    }

    [Fact]
    public void IsPersistable_ZeroDpi_False()
    {
        // RDP 会话实测：GetDpiForMonitor 返回 S_OK 但 DPI 为 0，后续换算会产生 Infinity。
        var p = Good();
        p.SavedDpiX = 0;

        Assert.False(PlacementValidation.IsPersistable(p));
    }

    [Fact]
    public void IsPersistable_ZeroSize_False()
    {
        var p = Good();
        p.HeightDip = 0;

        Assert.False(PlacementValidation.IsPersistable(p));
    }

    [Fact]
    public void RemoveUnpersistablePlacements_DropsBadKeepsGood()
    {
        var bad = Good();
        bad.WidthDip = double.NaN;

        var display = new WindowDisplaySettings { FloatingPlacement = bad, DesktopPlacement = Good() };

        Assert.True(PlacementValidation.RemoveUnpersistablePlacements(display));
        Assert.Null(display.FloatingPlacement);
        Assert.NotNull(display.DesktopPlacement); // 好的那个必须留着
    }

    [Fact]
    public void RemoveUnpersistablePlacements_AllGood_ReportsNoCleanup()
    {
        var display = new WindowDisplaySettings { FloatingPlacement = Good(), DesktopPlacement = Good() };

        Assert.False(PlacementValidation.RemoveUnpersistablePlacements(display));
        Assert.NotNull(display.FloatingPlacement);
    }

    [Fact]
    public void RemoveUnpersistablePlacements_NonFiniteOpacity_FallsBackToOpaque()
    {
        var display = new WindowDisplaySettings { Opacity = double.NaN };

        Assert.True(PlacementValidation.RemoveUnpersistablePlacements(display));
        Assert.Equal(1.0, display.Opacity);
    }

    /// <summary>
    /// 决定性验证：清理之后，整份设置必须能真正被 JSON 序列化——这正是当初失败的那一步。
    /// </summary>
    [Fact]
    public void AfterCleanup_SettingsCanActuallyBeSerialized()
    {
        var bad = Good();
        bad.WidthDip = double.NaN;
        var settings = new AppSettings
        {
            WindowDisplay = new WindowDisplaySettings { FloatingPlacement = bad, Opacity = double.NaN },
        };

        // 清理前：序列化抛异常（与线上失败一致）
        Assert.Throws<ArgumentException>(() => System.Text.Json.JsonSerializer.Serialize(settings));

        PlacementValidation.RemoveUnpersistablePlacements(settings.WindowDisplay);

        // 清理后：可以正常写盘
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.Contains("windowDisplay", json, StringComparison.OrdinalIgnoreCase);
    }
}
