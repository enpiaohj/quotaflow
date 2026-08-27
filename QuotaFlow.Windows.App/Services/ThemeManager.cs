using Microsoft.Win32;
using QuotaFlow.Windows.Core.Models;
using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 负责在浅色/深色两套色板之间切换。App.xaml 里第 0 个合并字典固定是当前色板，
/// 切换时直接整体替换那一项，所有用 DynamicResource 引用颜色的控件会自动刷新，
/// 不需要每个 Window/UserControl 各自处理。
/// </summary>
public sealed class ThemeManager
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightThemeValueName = "AppsUseLightTheme";

    private ThemeMode _mode = ThemeMode.System;

    public event EventHandler? ThemeChanged;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        var useLight = mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => IsSystemLightTheme(),
        };

        var uri = new Uri(useLight ? "Themes/Colors.Light.xaml" : "Themes/Colors.Dark.xaml", UriKind.Relative);
        var newDictionary = new ResourceDictionary { Source = uri };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = newDictionary;
        }
        else
        {
            dictionaries.Add(newDictionary);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>跟随系统模式下，系统主题变化时需要调用这个方法重新应用一次。</summary>
    public void ReapplyIfFollowingSystem()
    {
        if (_mode == ThemeMode.System)
        {
            Apply(ThemeMode.System);
        }
    }

    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var value = key?.GetValue(LightThemeValueName);
            return value is not int i || i != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return true; // 读取失败时默认浅色，与文档"浅色模式优先"一致。
        }
    }
}
