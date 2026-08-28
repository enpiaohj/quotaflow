namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 窗口的三种显示模式，是窗口行为的主状态。置顶、位置锁定、布局密度、透明度、材质都是
/// 独立于模式的属性（见 <see cref="WindowDisplaySettings"/>），不用一堆互相矛盾的布尔值推断当前模式。
/// </summary>
public enum WindowPresentationMode
{
    /// <summary>从系统托盘附近弹出，不可拖动，点击外部/Esc 收起，不在任务栏显示。既有默认行为。</summary>
    TrayPopup,

    /// <summary>脱离托盘锚点的悬浮窗口：可拖动、可置顶、可锁定位置，点击外部/Esc 不关闭。</summary>
    Floating,

    /// <summary>长期放在桌面角落的看板：默认紧凑布局、默认不置顶、鼠标移入才显示工具栏。</summary>
    DesktopPanel,
}

/// <summary>
/// 窗口背景材质。真实能力（系统版本、远程桌面、高对比度）由
/// App 层的 <c>WindowMaterialService</c> 检测；检测不支持时安全降级为 <see cref="Solid"/>，
/// 绝不能只设置一个半透明颜色就冒充 Mica/Acrylic。
/// </summary>
public enum WindowMaterial
{
    /// <summary>跟随系统当前的材质偏好。</summary>
    System,
    Mica,
    MicaAlt,
    Acrylic,

    /// <summary>纯色背景：不支持材质、远程桌面或高对比度模式下的兜底选项。</summary>
    Solid,
}

/// <summary>
/// 悬浮/桌面看板模式下保存的窗口位置与尺寸。不只存裸像素坐标——同时记录所在显示器和保存时的
/// DPI，恢复时才能正确判断"原显示器是否还在""要不要按新 DPI 重新换算"，而不是直接把旧坐标
/// 糊到当前屏幕上（可能整个跑到屏幕外）。
/// </summary>
public sealed class SavedWindowPlacement
{
    /// <summary>保存时所在显示器的设备名（如 <c>\\.\DISPLAY1</c>）；找不到时按§7规则回退主显示器。</summary>
    public string? MonitorDeviceName { get; set; }

    public double LeftDip { get; set; }
    public double TopDip { get; set; }
    public double WidthDip { get; set; }
    public double HeightDip { get; set; }

    /// <summary>保存时该显示器的 DPI（96 = 100%），用于恢复时判断是否跨 DPI 显示器移动过。</summary>
    public double SavedDpiX { get; set; } = 96.0;
    public double SavedDpiY { get; set; } = 96.0;

    public DateTimeOffset LastUpdatedAt { get; set; }
}

/// <summary>
/// 独立于显示模式的窗口行为状态。持久化到 <see cref="AppSettings.WindowDisplay"/>，跟其它
/// 非敏感设置一样走 DPAPI 加密信封，不涉及任何密钥。
///
/// 置顶（<see cref="IsAlwaysOnTop"/>）与位置锁定（<see cref="IsPositionLocked"/>）是两个正交概念，
/// 不用一个含义模糊的"固定"代替：置顶决定窗口是否盖在别的窗口上面，锁定决定窗口能不能被拖动。
/// </summary>
public sealed class WindowDisplaySettings
{
    /// <summary>当前显示模式。加载到未定义的枚举值（配置损坏/来自更新版本）时安全回退到 TrayPopup。</summary>
    public WindowPresentationMode Mode { get; set; } = WindowPresentationMode.TrayPopup;

    public bool IsAlwaysOnTop { get; set; } = true;
    public bool IsPositionLocked { get; set; }
    public bool IsCompactLayout { get; set; }

    /// <summary>整体透明度，0.7～1.0（对应 70%～100%）；低于 0.7 会被 clamp，不允许更透。</summary>
    public double Opacity { get; set; } = 1.0;

    public WindowMaterial Material { get; set; } = WindowMaterial.System;

    /// <summary>拖动时是否吸附屏幕工作区边缘（按住 Shift 临时关闭，不影响这个持久化设置本身）。</summary>
    public bool SnapToEdges { get; set; } = true;

    /// <summary>启动后是否恢复上次的显示模式；关闭时启动后总是 TrayPopup。</summary>
    public bool RestoreLastModeOnStartup { get; set; }

    /// <summary>桌面看板模式下，鼠标移入时是否临时把透明度提升到 100% 增强可读性。</summary>
    public bool EnhanceReadabilityOnHover { get; set; } = true;

    /// <summary>悬浮模式的位置/尺寸；null 表示从未进入过该模式，进入时套用该模式的推荐默认值。</summary>
    public SavedWindowPlacement? FloatingPlacement { get; set; }

    /// <summary>桌面看板模式的位置/尺寸；null 表示从未进入过该模式。</summary>
    public SavedWindowPlacement? DesktopPlacement { get; set; }
}
