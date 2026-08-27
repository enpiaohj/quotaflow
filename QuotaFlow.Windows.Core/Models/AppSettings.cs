namespace QuotaFlow.Windows.Core.Models;

/// <summary>跟随系统 / 浅色 / 深色。</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>MiniMax 站点区域。</summary>
public enum MiniMaxRegion
{
    China,
    International,
}

/// <summary>
/// 非敏感的应用设置（不含任何密钥），持久化到普通 JSON 文件即可（文档 §7 只对密钥有硬性要求）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>自动刷新间隔（分钟）：5/10/15/30，默认 5。</summary>
    public int AutoRefreshIntervalMinutes { get; set; } = 5;

    /// <summary>启动时是否立即刷新。</summary>
    public bool RefreshOnStartup { get; set; } = true;

    /// <summary>是否开机自动启动，默认关闭。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>主题模式，默认跟随系统。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>MiniMax 站点区域，默认国内站。</summary>
    public MiniMaxRegion MiniMaxRegion { get; set; } = MiniMaxRegion.China;

    /// <summary>
    /// 是否在面板上显示服务端新出现、尚未识别的额度窗口（例如 nimbus_quill）。
    /// 默认关闭：这类新窗口大多是 0% 使用、无重置时间的空额度，原样透传成英文名展示既看不懂也占地方。
    /// </summary>
    public bool ShowUnknownWindows { get; set; }

    public static readonly int[] AllowedRefreshIntervals = [5, 10, 15, 30];
}
