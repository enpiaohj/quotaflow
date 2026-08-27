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
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
    }
}
