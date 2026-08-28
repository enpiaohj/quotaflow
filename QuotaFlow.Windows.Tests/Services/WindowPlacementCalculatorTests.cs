using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

public class WindowPlacementCalculatorTests
{
    private static readonly MonitorInfo Primary = new("\\\\.\\DISPLAY1", 0, 0, 1920, 1040, 96, 96, IsPrimary: true);
    private static readonly MonitorInfo Secondary = new("\\\\.\\DISPLAY2", 1920, 0, 1280, 720, 96, 96, IsPrimary: false);

    [Fact]
    public void Correct_NoSavedPlacement_PlacesNearWorkAreaBottomRightOfPrimary()
    {
        var result = WindowPlacementCalculator.Correct(null, [Primary], 320, 200);

        Assert.Equal(Primary.DeviceName, result.MonitorDeviceName);
        Assert.Equal(320, result.WidthDip);
        Assert.Equal(200, result.HeightDip);
        // 贴右下角留白，不是贴死在边缘。
        Assert.True(result.LeftDip < Primary.WorkAreaWidth && result.LeftDip > Primary.WorkAreaWidth - 320 - 100);
        Assert.True(result.TopDip < Primary.WorkAreaHeight && result.TopDip > Primary.WorkAreaHeight - 200 - 100);
    }

    [Fact]
    public void Correct_SavedOnOriginalMonitor_RestoresExactPosition()
    {
        var saved = new SavedWindowPlacement
        {
            MonitorDeviceName = Secondary.DeviceName,
            LeftDip = 2000,
            TopDip = 100,
            WidthDip = 300,
            HeightDip = 400,
        };

        var result = WindowPlacementCalculator.Correct(saved, [Primary, Secondary], 320, 200);

        Assert.Equal(Secondary.DeviceName, result.MonitorDeviceName);
        Assert.Equal(2000, result.LeftDip);
        Assert.Equal(100, result.TopDip);
        Assert.Equal(300, result.WidthDip);
        Assert.Equal(400, result.HeightDip);
    }

    [Fact]
    public void Correct_OriginalMonitorMissing_FallsBackToPrimary()
    {
        var saved = new SavedWindowPlacement
        {
            MonitorDeviceName = "\\\\.\\DISPLAY-DISCONNECTED",
            LeftDip = 2500,
            TopDip = 100,
            WidthDip = 300,
            HeightDip = 400,
        };

        // 只剩主显示器，副屏已经拔掉了。
        var result = WindowPlacementCalculator.Correct(saved, [Primary], 320, 200);

        Assert.Equal(Primary.DeviceName, result.MonitorDeviceName);
        // 原坐标（2500,100）落在主屏工作区之外，必须被 clamp 回来，不能凭空出现在屏幕外。
        Assert.InRange(result.LeftDip, Primary.WorkAreaLeft, Primary.WorkAreaLeft + Primary.WorkAreaWidth - result.WidthDip);
        Assert.InRange(result.TopDip, Primary.WorkAreaTop, Primary.WorkAreaTop + Primary.WorkAreaHeight);
    }

    [Fact]
    public void Correct_MissingMonitor_NoPrimaryFlagged_FallsBackToFirstInList()
    {
        var noPrimaryFlag = Primary with { IsPrimary = false };
        var saved = new SavedWindowPlacement { MonitorDeviceName = "gone", LeftDip = 0, TopDip = 0, WidthDip = 300, HeightDip = 200 };

        var result = WindowPlacementCalculator.Correct(saved, [noPrimaryFlag], 320, 200);

        Assert.Equal(noPrimaryFlag.DeviceName, result.MonitorDeviceName);
    }

    [Theory]
    [InlineData(-5000, -5000)] // 完全跑到左上角屏幕外
    [InlineData(50000, 50000)] // 完全跑到右下角屏幕外
    [InlineData(-100, 500)]    // 只有左边缘跑出去
    public void Correct_OffScreenSavedPosition_NeverStaysOffScreen(double badLeft, double badTop)
    {
        var saved = new SavedWindowPlacement { MonitorDeviceName = Primary.DeviceName, LeftDip = badLeft, TopDip = badTop, WidthDip = 320, HeightDip = 200 };

        var result = WindowPlacementCalculator.Correct(saved, [Primary], 320, 200);

        // 窗口的左上角（标题栏所在处）必须落在工作区可见范围内。
        Assert.InRange(result.LeftDip, Primary.WorkAreaLeft, Primary.WorkAreaLeft + Primary.WorkAreaWidth - result.WidthDip);
        Assert.InRange(result.TopDip, Primary.WorkAreaTop, Primary.WorkAreaTop + Primary.WorkAreaHeight - 32);
    }

    [Fact]
    public void Correct_SavedSizeLargerThanWorkArea_ShrinksToFit()
    {
        var saved = new SavedWindowPlacement { MonitorDeviceName = Secondary.DeviceName, LeftDip = 1950, TopDip = 10, WidthDip = 5000, HeightDip = 5000 };

        var result = WindowPlacementCalculator.Correct(saved, [Primary, Secondary], 320, 200);

        Assert.True(result.WidthDip <= Secondary.WorkAreaWidth);
        Assert.True(result.HeightDip <= Secondary.WorkAreaHeight);
    }

    [Fact]
    public void Correct_ZeroOrNegativeSavedSize_FallsBackToDefaultSize()
    {
        var saved = new SavedWindowPlacement { MonitorDeviceName = Primary.DeviceName, LeftDip = 100, TopDip = 100, WidthDip = 0, HeightDip = -10 };

        var result = WindowPlacementCalculator.Correct(saved, [Primary], 320, 200);

        Assert.Equal(320, result.WidthDip);
        Assert.Equal(200, result.HeightDip);
    }

    [Fact]
    public void Correct_RecordsTargetMonitorDpi()
    {
        var highDpiMonitor = Primary with { DpiX = 144, DpiY = 144 }; // 150% 缩放
        var saved = new SavedWindowPlacement { MonitorDeviceName = highDpiMonitor.DeviceName, LeftDip = 100, TopDip = 100, WidthDip = 320, HeightDip = 200 };

        var result = WindowPlacementCalculator.Correct(saved, [highDpiMonitor], 320, 200);

        Assert.Equal(144, result.SavedDpiX);
        Assert.Equal(144, result.SavedDpiY);
    }

    [Fact]
    public void Correct_NoMonitorsAtAll_ReturnsConservativeFallback_DoesNotThrow()
    {
        var result = WindowPlacementCalculator.Correct(null, [], 320, 200);

        Assert.Null(result.MonitorDeviceName);
        Assert.Equal(0, result.LeftDip);
        Assert.Equal(0, result.TopDip);
        Assert.True(result.WidthDip > 0);
        Assert.True(result.HeightDip > 0);
    }

    [Fact]
    public void Correct_TitleBarAlwaysStaysVisible_EvenWhenHeightExceedsWorkArea()
    {
        // 高度远超工作区（比如从超高分辨率屏幕移到小屏幕）：标题栏那部分必须仍然可见，
        // 不能因为窗口整体太高就把 top 算到工作区外面去。
        var saved = new SavedWindowPlacement { MonitorDeviceName = Secondary.DeviceName, LeftDip = 1950, TopDip = 5000, WidthDip = 300, HeightDip = 3000 };

        var result = WindowPlacementCalculator.Correct(saved, [Primary, Secondary], 320, 200);

        Assert.InRange(result.TopDip, Secondary.WorkAreaTop, Secondary.WorkAreaTop + Secondary.WorkAreaHeight);
    }

    // ---- FindNearestMonitor（拖动吸附用）----

    [Fact]
    public void FindNearestMonitor_PointInsideWorkArea_ReturnsContainingMonitor()
    {
        var result = WindowPlacementCalculator.FindNearestMonitor([Primary, Secondary], 2200, 300);

        Assert.Equal(Secondary.DeviceName, result!.DeviceName);
    }

    [Fact]
    public void FindNearestMonitor_PointOutsideAllWorkAreas_ReturnsClosestByDistance()
    {
        // 远远超出两块屏幕范围，但离主屏中心更近。
        var result = WindowPlacementCalculator.FindNearestMonitor([Primary, Secondary], -500, 500);

        Assert.Equal(Primary.DeviceName, result!.DeviceName);
    }

    [Fact]
    public void FindNearestMonitor_EmptyList_ReturnsNull()
    {
        var result = WindowPlacementCalculator.FindNearestMonitor([], 0, 0);

        Assert.Null(result);
    }
}
