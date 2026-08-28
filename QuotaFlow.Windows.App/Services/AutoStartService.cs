using System.Diagnostics;
using System.IO;
using System.Security;
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

    /// <summary>
    /// 写注册表 Run 项。返回是否真的达到了目标状态——调用方（设置页保存、托盘菜单勾选）都要用这个
    /// 结果给用户明确反馈，而不是假定"调用了就一定成功"：注册表写入在极少数环境下可能因权限
    /// （<see cref="UnauthorizedAccessException"/>/<see cref="SecurityException"/>）失败，另外
    /// 拿不到当前 exe 路径（理论上不会发生，但防御式处理）时开启操作也应该算失败，而不是静默
    /// 什么都没写却让用户以为已经开了。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                return false;
            }

            // --autostart 标记这次启动是 Windows 登录时自动拉起的，不是用户手动双击/从开始菜单打开——
            // App.xaml.cs 据此决定要不要在启动时主动展示一次面板（开机自启不该突然弹窗打扰用户，
            // 手动启动则应该给个"确实打开了"的反馈）。
            key.SetValue(ValueName, $"\"{exePath}\" --autostart");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
