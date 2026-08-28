using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 悬浮/桌面看板窗口的位置修正算法（对应产品文档 §7 的恢复流程）。纯函数、不依赖任何
/// WPF/Win32 类型，方便脱离真实窗口和真实显示器直接单测——这是文档明确要求优先做成
/// 可单元测试的一块："位置修正算法" 不应该只能靠 UI 自动化验证。
/// </summary>
public static class WindowPlacementCalculator
{
    private const double DefaultMargin = 16;
    private const double MinVisibleTitleBarHeight = 32;
    private const double MinWidth = 200;
    private const double MinHeight = 120;

    /// <summary>
    /// 算出一个当前一定可见、落在某个真实显示器工作区内的窗口位置。
    /// </summary>
    /// <param name="saved">上次保存的位置；null 表示从未进入过这个模式，用工作区右下角的默认位置。</param>
    /// <param name="monitors">当前所有显示器的工作区信息；不能为空（真实环境下至少有一个显示器）。</param>
    /// <param name="defaultWidth">saved 为 null，或 saved 里没有有效宽度时使用的默认宽度。</param>
    /// <param name="defaultHeight">同上，默认高度。</param>
    /// <returns>
    /// 修正后的位置：
    /// 1. 优先落在 saved.MonitorDeviceName 对应的显示器（原显示器）；
    /// 2. 原显示器不存在时回退主显示器，找不到主显示器时用列表第一个；
    /// 3. 宽高不超过目标工作区（不强改用户设定的尺寸，只在明显超出工作区时收窄）；
    /// 4. 左上角被 clamp 在工作区内，标题栏高度的区域始终可见——不会因为旧坐标导致窗口
    ///    永远出现在屏幕外。
    /// </returns>
    public static SavedWindowPlacement Correct(
        SavedWindowPlacement? saved,
        IReadOnlyList<MonitorInfo> monitors,
        double defaultWidth,
        double defaultHeight)
    {
        if (monitors.Count == 0)
        {
            // 理论上不会发生（真实系统至少有一块屏），防御式返回一个最保守的原点位置。
            return new SavedWindowPlacement
            {
                MonitorDeviceName = null,
                LeftDip = 0,
                TopDip = 0,
                WidthDip = Math.Max(defaultWidth, MinWidth),
                HeightDip = Math.Max(defaultHeight, MinHeight),
                LastUpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        var target = ResolveTargetMonitor(saved, monitors);

        var width = Math.Clamp(saved?.WidthDip is > 0 ? saved.WidthDip : defaultWidth, MinWidth, target.WorkAreaWidth);
        var height = Math.Clamp(saved?.HeightDip is > 0 ? saved.HeightDip : defaultHeight, MinHeight, target.WorkAreaHeight);

        double left, top;
        if (saved is null)
        {
            // 从未保存过：贴目标显示器工作区右下角（跟托盘弹出位置的直觉一致，用户找得到）。
            left = target.WorkAreaLeft + target.WorkAreaWidth - width - DefaultMargin;
            top = target.WorkAreaTop + target.WorkAreaHeight - height - DefaultMargin;
        }
        else
        {
            left = saved.LeftDip;
            top = saved.TopDip;
        }

        var minLeft = target.WorkAreaLeft;
        var maxLeft = Math.Max(minLeft, target.WorkAreaLeft + target.WorkAreaWidth - width);
        var minTop = target.WorkAreaTop;
        // 标题栏在窗口顶部：只要求"至少 MinVisibleTitleBarHeight 高的区域"落在工作区内，
        // 而不是整个窗口都必须可见——高度超过工作区剩余空间时允许下半截被裁掉。
        var maxTop = Math.Max(minTop, target.WorkAreaTop + target.WorkAreaHeight - Math.Min(height, MinVisibleTitleBarHeight));

        left = Math.Clamp(left, minLeft, maxLeft);
        top = Math.Clamp(top, minTop, maxTop);

        return new SavedWindowPlacement
        {
            MonitorDeviceName = target.DeviceName,
            LeftDip = left,
            TopDip = top,
            WidthDip = width,
            HeightDip = height,
            SavedDpiX = target.DpiX,
            SavedDpiY = target.DpiY,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 拖动时用来判断"现在离哪块屏幕的边缘够近该吸附"：优先返回工作区包含该点的显示器，
    /// 找不到（点落在两块屏幕的间隙/屏幕外）时退而返回中心点最近的那块。monitors 为空返回 null。
    /// </summary>
    public static MonitorInfo? FindNearestMonitor(IReadOnlyList<MonitorInfo> monitors, double pointX, double pointY)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (pointX >= monitor.WorkAreaLeft && pointX <= monitor.WorkAreaLeft + monitor.WorkAreaWidth &&
                pointY >= monitor.WorkAreaTop && pointY <= monitor.WorkAreaTop + monitor.WorkAreaHeight)
            {
                return monitor;
            }
        }

        return monitors.MinBy(m =>
        {
            var centerX = m.WorkAreaLeft + m.WorkAreaWidth / 2;
            var centerY = m.WorkAreaTop + m.WorkAreaHeight / 2;
            var dx = centerX - pointX;
            var dy = centerY - pointY;
            return dx * dx + dy * dy;
        });
    }

    private static MonitorInfo ResolveTargetMonitor(SavedWindowPlacement? saved, IReadOnlyList<MonitorInfo> monitors)
    {
        if (saved?.MonitorDeviceName is { } name)
        {
            var original = monitors.FirstOrDefault(m => m.DeviceName == name);
            if (original is not null)
            {
                return original;
            }
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }
}
