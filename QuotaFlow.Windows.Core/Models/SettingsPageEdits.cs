namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 设置页可编辑字段的纯数据快照。
///
/// 它的存在是为了把"设置页到底拥有哪些字段"这条规则从 WPF ViewModel 里剥出来，变成
/// Core 里可测试的数据。<see cref="AppSettings"/> 中<b>没有</b>出现在这里的字段
/// （<see cref="AppSettings.WindowDisplay"/>、<see cref="AppSettings.PlatformOrder"/>、
/// <see cref="AppSettings.QuotaDisplaySemantic"/>）由面板即时持久化，保存设置时必须原样保留。
///
/// 这条规则曾被破坏过一次且造成了数据丢失：早期实现用 <c>new AppSettings { ... }</c> 从零
/// 构造要写盘的对象，凡是没在初始化器里列出的字段都取默认值并被整体写盘，于是点一次
/// 「保存设置」就把显示模式、不透明度、置顶、各显示器记住的窗口位置全部重置。
/// 当时这段逻辑在 App 工程里，测试工程只引用 Core，无法覆盖。
/// </summary>
public sealed class SettingsPageEdits
{
    public int AutoRefreshIntervalMinutes { get; set; } = 5;
    public bool RefreshOnStartup { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public MiniMaxRegion MiniMaxRegion { get; set; } = MiniMaxRegion.China;
    public bool ShowUnknownWindows { get; set; }
    public ClockDisplayFormat ClockDisplayFormat { get; set; } = ClockDisplayFormat.Full;

    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
    public string? ProxyAddress { get; set; }

    public string? ClaudeEndpointOverride { get; set; }
    public string? CodexEndpointOverride { get; set; }
    public string? MiniMaxEndpointOverride { get; set; }
    public string? DeepSeekEndpointOverride { get; set; }

    public string? VolcengineArkRegion { get; set; }
    public string? VolcengineArkPlanDisplayNameOverride { get; set; }

    public bool HotkeyEnabled { get; set; } = true;
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Alt;
    public string HotkeyKey { get; set; } = "Z";

    /// <summary>被用户手动隐藏、不在面板显示的内置平台 ProviderId。</summary>
    public IReadOnlyList<string> HiddenPlatforms { get; set; } = [];

    /// <summary>设置页当前编辑中的自定义平台定义（尚未校验）。</summary>
    public IReadOnlyList<CustomPlatformSettings> CustomPlatforms { get; set; } = [];
}
