using System.Drawing;
using System.Runtime.InteropServices;
using QuotaFlow.Windows.Core.Assets;

namespace QuotaFlow.Windows.App.Services;

/// <summary>整体健康度徽标颜色，叠加在托盘图标右下角，做到"看一眼任务栏就知道要不要点开面板"。</summary>
public enum TrayBadge
{
    None,
    Good,
    Warn,
    Bad,
}

/// <summary>
/// 在内存里画出托盘图标，避免额外打包一份 .ico 资源文件。绘制逻辑复用
/// <see cref="AppIconRenderer"/>（与静态 .ico 文件生成工具共用同一套设计），
/// 这里只负责叠加托盘专属的右下角状态徽标、以及转换成 Win32 HICON。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>创建图标；返回值里的 <c>NativeHandle</c> 必须在替换/退出时调用 <see cref="Destroy"/> 释放。</summary>
    public static (Icon Icon, IntPtr NativeHandle) Create(TrayBadge badge = TrayBadge.None)
    {
        Color? badgeColor = badge switch
        {
            TrayBadge.Good => Color.FromArgb(255, 15, 157, 88),
            TrayBadge.Warn => Color.FromArgb(255, 249, 168, 37),
            TrayBadge.Bad => Color.FromArgb(255, 217, 48, 37),
            _ => null,
        };

        using var bitmap = AppIconRenderer.Render(32, ringFraction: 0.75, badgeColor);
        var hIcon = bitmap.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    /// <summary>释放 <see cref="Create"/> 返回的原生图标句柄，避免长期运行的托盘进程发生 GDI 句柄泄漏。</summary>
    public static void Destroy(IntPtr nativeHandle)
    {
        if (nativeHandle != IntPtr.Zero)
        {
            DestroyIcon(nativeHandle);
        }
    }
}
