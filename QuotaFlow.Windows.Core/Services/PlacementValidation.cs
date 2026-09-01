using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 窗口位置是否可以安全落盘。
///
/// 存在的理由是一次真实事故：WPF 的 <c>Window.Width/Height</c> 在窗口尚未显式设定尺寸时是
/// <see cref="double.NaN"/>，<c>Left/Top</c> 在窗口定位前同样可能非有限值。这样的位置一旦进入
/// 设置对象，<c>System.Text.Json</c> 序列化时会抛 <see cref="ArgumentException"/>
/// （"values such as positive and negative infinity cannot be written as valid JSON"，NaN 同样命中）。
/// 而设置存储对写盘失败是静默降级的，于是表现为：
/// <b>界面上设置改了、也生效了，磁盘却一个字节没写，重启后全部丢失，且没有任何提示。</b>
///
/// 实测复现：勾选「紧凑布局」、切换「始终置顶」，界面立刻变化，重启后全部回退。
/// 所以位置在写进状态之前必须先过这一关，非有限值直接丢弃（保留上一次的有效位置），
/// 绝不能让一个坏值把整份设置的持久化拖垮。
/// </summary>
public static class PlacementValidation
{
    /// <summary>位置的每个数值都必须是有限值，且尺寸为正数。</summary>
    public static bool IsPersistable(SavedWindowPlacement? placement)
    {
        if (placement is null)
        {
            return false;
        }

        return double.IsFinite(placement.LeftDip)
               && double.IsFinite(placement.TopDip)
               && double.IsFinite(placement.WidthDip) && placement.WidthDip > 0
               && double.IsFinite(placement.HeightDip) && placement.HeightDip > 0
               && double.IsFinite(placement.SavedDpiX) && placement.SavedDpiX > 0
               && double.IsFinite(placement.SavedDpiY) && placement.SavedDpiY > 0;
    }

    /// <summary>
    /// 把整份窗口显示设置里不可落盘的位置清掉，返回是否做过清理。
    /// 作为持久化前的最后一道防线：无论坏值来自哪条路径，都不该让它阻断整份设置的保存。
    /// </summary>
    public static bool RemoveUnpersistablePlacements(WindowDisplaySettings display)
    {
        ArgumentNullException.ThrowIfNull(display);

        var cleaned = false;

        if (display.FloatingPlacement is not null && !IsPersistable(display.FloatingPlacement))
        {
            display.FloatingPlacement = null;
            cleaned = true;
        }

        if (display.DesktopPlacement is not null && !IsPersistable(display.DesktopPlacement))
        {
            display.DesktopPlacement = null;
            cleaned = true;
        }

        // 不透明度同理：非有限值一样会让序列化失败，回落到不透明。
        if (!double.IsFinite(display.Opacity) || display.Opacity <= 0)
        {
            display.Opacity = 1.0;
            cleaned = true;
        }

        return cleaned;
    }
}
