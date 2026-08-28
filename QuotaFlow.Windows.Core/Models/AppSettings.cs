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

/// <summary>面板顶部日期/时间显示格式（设置页"日期显示格式"）。</summary>
public enum ClockDisplayFormat
{
    /// <summary>2026-08-27 周四 · 第35周 · 14:30:05（完整：日期+周几+第几周+实时时间）。</summary>
    Full,

    /// <summary>2026-08-27 周四 · 14:30:05（日期+周几+实时时间）。</summary>
    DateWeekdayTime,

    /// <summary>2026-08-27 · 14:30:05（日期+实时时间）。</summary>
    DateTime,

    /// <summary>14:30:05（仅实时时间）。</summary>
    TimeOnly,
}

/// <summary>
/// 额度窗口百分比的显示语义：QuotaWindow 内部一直同时保存已用/剩余两个百分比，这里只是
/// 决定 UI 上念哪一个数字，不影响任何平台的查询/解析逻辑。面板顶部可一键切换，全局生效。
/// </summary>
public enum QuotaDisplaySemantic
{
    /// <summary>显示剩余百分比（默认，此前一直是唯一行为）。</summary>
    Remaining,

    /// <summary>显示已使用百分比。</summary>
    Used,
}

/// <summary>
/// 显示/隐藏面板全局快捷键的修饰键组合。故意不直接用 WPF 的 <c>System.Windows.Input.ModifierKeys</c>——
/// Core 项目不依赖任何 UI 框架，键位组合在这里只是纯数据，实际按键捕获/注册（Win32
/// RegisterHotKey）都在 App 层的 Services/GlobalHotkeyService 完成。
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// 非敏感的应用设置（不含任何密钥），经 <see cref="Services.AppSettingsStore"/> 以 DPAPI 加密信封
/// 持久化到磁盘。密钥仍只走 Windows 凭据管理器，本模型不承载任何密钥字段。
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

    /// <summary>面板顶部日期/时间显示格式，默认完整模式（日期+周几+第几周+实时时间）。</summary>
    public ClockDisplayFormat ClockDisplayFormat { get; set; } = ClockDisplayFormat.Full;

    /// <summary>
    /// 额度窗口百分比显示"已用"还是"剩余"，默认剩余（与升级前行为一致）。面板上可直接切换，
    /// 立即持久化生效，纯展示层设置，不影响查询/解析。
    /// </summary>
    public QuotaDisplaySemantic QuotaDisplaySemantic { get; set; } = QuotaDisplaySemantic.Remaining;

    /// <summary>MiniMax 站点区域，默认国内站。</summary>
    public MiniMaxRegion MiniMaxRegion { get; set; } = MiniMaxRegion.China;

    /// <summary>
    /// 是否在面板上显示服务端新出现、尚未识别的额度窗口（例如 nimbus_quill）。
    /// 默认关闭：这类新窗口大多是 0% 使用、无重置时间的空额度，原样透传成英文名展示既看不懂也占地方。
    /// </summary>
    public bool ShowUnknownWindows { get; set; }

    /// <summary>覆盖 Claude 用量接口地址；空/未填时使用内置默认地址。</summary>
    public string? ClaudeEndpointOverride { get; set; }

    /// <summary>覆盖 Codex 用量接口地址；空/未填时使用内置默认地址。</summary>
    public string? CodexEndpointOverride { get; set; }

    /// <summary>
    /// 覆盖 MiniMax 用量接口地址（完整 URL，优先级高于区域域名）；空/未填时按区域域名构建。
    /// </summary>
    public string? MiniMaxEndpointOverride { get; set; }

    /// <summary>覆盖 DeepSeek 余额接口地址；空/未填时使用内置默认地址。</summary>
    public string? DeepSeekEndpointOverride { get; set; }

    /// <summary>
    /// 面板平台的展示顺序（ProviderId 列表，从上到下）。空/未填时使用内置默认顺序
    /// （Claude → Codex → MiniMax → DeepSeek）；配置里未出现的平台按默认顺序排在末尾。
    /// </summary>
    public string[]? PlatformOrder { get; set; }

    /// <summary>
    /// 用户在设置页手动添加的自定义平台定义列表。纯新增字段（默认空列表），不 bump schemaVersion；
    /// 读取方一律用 <c>?? []</c> 兜底，防手改配置出现 <c>customPlatforms: null</c>。
    /// </summary>
    public List<CustomPlatformSettings> CustomPlatforms { get; set; } = [];

    /// <summary>是否启用"显示/隐藏面板"全局快捷键，默认开启（默认组合见下）。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>快捷键的修饰键组合，默认 Alt。</summary>
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Alt;

    /// <summary>
    /// 快捷键的主键，存成字符串（对应 WPF <c>System.Windows.Input.Key</c> 枚举的 <c>ToString()</c>，
    /// 如 "Z"、"F5"），只在 App 层解析——Core 不依赖 WPF。默认 "Z"（Alt+Z）。
    /// </summary>
    public string HotkeyKey { get; set; } = "Z";

    public static readonly int[] AllowedRefreshIntervals = [5, 10, 15, 30];
}
