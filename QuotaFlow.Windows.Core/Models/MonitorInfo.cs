namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 一个显示器的工作区信息（DIP 单位），供 <see cref="Services.WindowPlacementCalculator"/> 做
/// 纯数学的位置修正。App 层从真实的 <c>System.Windows.Forms.Screen</c>/DPI API 填充；
/// Core 层完全不知道 WPF/Win32，只处理这几个数字，因此这套修正算法可以脱离真实窗口直接单测。
/// </summary>
/// <param name="DeviceName">显示器设备名（如 <c>\\.\DISPLAY1</c>），用于跨会话匹配"原显示器"。</param>
/// <param name="WorkAreaLeft">工作区左上角 X（不含任务栏遮挡区域，已按 DIP 换算）。</param>
/// <param name="WorkAreaTop">工作区左上角 Y。</param>
/// <param name="WorkAreaWidth">工作区宽度。</param>
/// <param name="WorkAreaHeight">工作区高度。</param>
/// <param name="DpiX">该显示器的 X 轴 DPI（96 = 100%）。</param>
/// <param name="DpiY">该显示器的 Y 轴 DPI。</param>
/// <param name="IsPrimary">是否为主显示器——原显示器找不到时的回退目标。</param>
public sealed record MonitorInfo(
    string DeviceName,
    double WorkAreaLeft,
    double WorkAreaTop,
    double WorkAreaWidth,
    double WorkAreaHeight,
    double DpiX,
    double DpiY,
    bool IsPrimary);
