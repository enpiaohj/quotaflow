using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 检测本机是否真的支持 Mica/Acrylic 系统材质，并尝试通过 DWM 应用。
///
/// 重要的架构限制（如实记录，不假装做到）：面板窗口用 <c>WindowStyle="None" AllowsTransparency="True"</c>
/// 实现圆角 + 自绘阴影（这是项目一开始就有的设计，README「已知风险」第 3 条已经写明"手写 Fluent
/// 风格模拟，未引入 WinUI3/WPF-UI 获取系统级亚克力效果"）。<c>AllowsTransparency="True"</c> 的 WPF
/// 窗口走的是逐像素 Alpha 的分层窗口（<c>UpdateLayeredWindow</c>），这与 DWM 的 Mica/Acrylic 合成
/// 天然冲突——即使 <see cref="TryApply"/> 里真的调用了 <c>DwmSetWindowAttribute</c>，这扇窗口上也
/// 不会出现真实的系统材质渲染效果。这里仍然做真实的系统能力检测（版本、远程桌面、高对比度）和真实
/// 的 DWM 调用尝试，不伪造检测结果；实际可见效果由调用方叠加不同的半透明背景画刷来近似——跟这个
/// 项目一直以来"手绘模拟系统材质"的做法一致，只是现在按材质种类做了区分，而不是心里知道不去戳破。
/// </summary>
public static class WindowMaterialService
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaSystemBackdropType = 38; // Windows 11 22621+
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int BackdropAuto = 0;
    private const int BackdropNone = 1;
    private const int BackdropMainWindow = 2; // Mica
    private const int BackdropTransientWindow = 3; // Acrylic
    private const int BackdropTabbedWindow = 4; // Mica Alt

    /// <summary>
    /// 真实系统能力检测：Windows 11 22621（2022 Update）起才有 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>；
    /// 远程桌面会话和高对比度模式下即使 OS 支持也不应该用材质（前者渲染质量差且可能不生效，
    /// 后者用户明确需要高可辨识度的不透明背景）。
    /// </summary>
    public static bool IsMaterialSupported()
    {
        if (SystemParameters.HighContrast)
        {
            return false;
        }

        if (System.Windows.Forms.SystemInformation.TerminalServerSession)
        {
            return false;
        }

        return GetWindowsBuildNumber() >= 22621;
    }

    private static int GetWindowsBuildNumber()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var raw = key?.GetValue("CurrentBuildNumber") as string;
            return int.TryParse(raw, out var build) ? build : 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 尝试把材质应用到窗口。<see cref="IsMaterialSupported"/> 为 false，或 <paramref name="material"/>
    /// 是 <see cref="WindowMaterial.Solid"/>/<see cref="WindowMaterial.System"/> 时不调用 DWM
    /// （System 交给 Windows 自己决定，Solid 本来就不需要材质）。调用失败（理论上只有极老/精简版
    /// Windows 会发生）不抛异常，静默返回 false，调用方继续用背景画刷近似。
    /// </summary>
    public static bool TryApply(Window window, WindowMaterial material)
    {
        if (material is WindowMaterial.Solid or WindowMaterial.System || !IsMaterialSupported())
        {
            return false;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var backdrop = material switch
        {
            WindowMaterial.Mica => BackdropMainWindow,
            WindowMaterial.MicaAlt => BackdropTabbedWindow,
            WindowMaterial.Acrylic => BackdropTransientWindow,
            _ => BackdropAuto,
        };

        try
        {
            return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0; // S_OK
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>让 DWM 深色标题栏跟应用当前主题一致（纯装饰性，失败无所谓）。</summary>
    public static void ApplyDarkModePreference(Window window, bool isDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = isDark ? 1 : 0;
        try
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 装饰性调用，失败忽略。
        }
    }
}
