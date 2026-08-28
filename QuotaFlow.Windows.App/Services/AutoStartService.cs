using System.Diagnostics;
using Microsoft.Win32;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 开机自动启动开关，写入当前用户的 Run 注册表项（不需要管理员权限，不装计划任务）。
/// 默认关闭，只有用户在设置页主动开启才会写入。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaFlow";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exePath))
        {
            // --autostart 标记这次启动是 Windows 登录时自动拉起的，不是用户手动双击/从开始菜单打开——
            // App.xaml.cs 据此决定要不要在启动时主动展示一次面板（开机自启不该突然弹窗打扰用户，
            // 手动启动则应该给个"确实打开了"的反馈）。
            key.SetValue(ValueName, $"\"{exePath}\" --autostart");
        }
    }
}
