using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 把真实的 <see cref="Screen"/> 信息转换成 Core 层认识的 <see cref="MonitorInfo"/>（DIP 单位、
/// 按每个显示器自己的 DPI 独立换算——多屏混合 DPI 场景下不能全局套用同一个缩放比例）。
/// 位置修正的纯数学都在 Core 的 <c>WindowPlacementCalculator</c>，这里只负责把 Win32/WinForms 的
/// 原始数据翻译成那套纯逻辑认识的形状。
/// </summary>
public static class MonitorService
{
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public static IReadOnlyList<MonitorInfo> GetAllMonitors()
    {
        var result = new List<MonitorInfo>();
        foreach (var screen in Screen.AllScreens)
        {
            var (dpiX, dpiY) = GetDpiForBounds(screen.Bounds);
            var scaleX = dpiX / 96.0;
            var scaleY = dpiY / 96.0;
            result.Add(new MonitorInfo(
                screen.DeviceName,
                screen.WorkingArea.Left / scaleX,
                screen.WorkingArea.Top / scaleY,
                screen.WorkingArea.Width / scaleX,
                screen.WorkingArea.Height / scaleY,
                dpiX,
                dpiY,
                screen.Primary));
        }

        return result;
    }

    private static (double DpiX, double DpiY) GetDpiForBounds(System.Drawing.Rectangle bounds)
    {
        try
        {
            var rect = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) == 0)
            {
                return (dpiX, dpiY);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Shcore.dll 的 GetDpiForMonitor 只在 Windows 8.1+ 存在；本项目最低支持版本远高于此，
            // 这里只是防御式兜底，不应该在真实环境触发。
        }

        return (96.0, 96.0);
    }

    /// <summary>给定已经显示（有真实句柄）的窗口，返回它当前所在显示器的设备名。</summary>
    public static string? GetCurrentMonitorDeviceName(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero ? null : Screen.FromHandle(handle).DeviceName;
    }

    /// <summary>窗口当前所在显示器的 DPI——用窗口自己的句柄查询，比按屏幕矩形反查更直接可靠，
    /// 因为窗口本身已经是 Per-Monitor-V2 DPI 感知的（见 app.manifest）。</summary>
    public static (double DpiX, double DpiY) GetDpiForWindow(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        return (dpi.PixelsPerInchX, dpi.PixelsPerInchY);
    }
}
