namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 额度窗口重置时间的解析方式。<see cref="Auto"/> 与 <see cref="Absolute"/> 目前走同一套解析逻辑
/// （<see cref="Services.JsonPathResolver.TryGetDateTimeOffset"/> 的 epoch/ISO 启发式），
/// 区分两者主要是给用户一个更明确的语义标签；<see cref="RelativeSeconds"/> 是唯一真正不同的路径——
/// 取到的是"距重置还有多少秒"，需要用响应到达时刻 + 秒数换算成绝对时间。
/// </summary>
public enum CustomResetTimeKind
{
    /// <summary>自动识别：数字按 epoch 毫秒/秒启发式，字符串按 ISO 8601 解析。</summary>
    Auto,

    /// <summary>明确声明取到的是绝对时间（解析规则同 Auto）。</summary>
    Absolute,

    /// <summary>取到的是"还剩多少秒"（resetInSec 语义），按响应到达时刻 + 秒数换算。</summary>
    RelativeSeconds,
}

/// <summary>
/// 自定义平台的一个额度窗口定义（v1.1.0 起：一个平台可以有多个窗口，同一次接口响应里
/// 分别取值，而不是把"5 小时/每周/每月"拆成三个平台）。非敏感，随 <see cref="CustomPlatformSettings"/>
/// 一起 DPAPI 加密持久化；不含任何密钥。
/// </summary>
public sealed class CustomQuotaWindowSettings
{
    /// <summary>
    /// 窗口稳定标识（如 <c>window-1</c>），设置页新增时分配，之后不变。用作
    /// <see cref="QuotaWindow.Id"/>；显示用的是 <see cref="Name"/>。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>窗口显示名称，例如 "5 小时"、"每周"。同一平台内不允许重复（设置页保存时校验）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据语义：已使用/剩余百分比、余额、已使用/剩余数值（配合额度上限）。</summary>
    public CustomDataKind DataKind { get; set; } = CustomDataKind.UtilizationPercent;

    /// <summary>取值用的点号 JSON 路径（如 <c>usage.rolling.percent</c>、<c>data.items[0].quota</c>）。</summary>
    public string ValuePath { get; set; } = string.Empty;

    /// <summary>可选：窗口重置时间的 JSON 路径。</summary>
    public string? ResetsAtPath { get; set; }

    /// <summary>重置时间的解析方式，默认自动识别。</summary>
    public CustomResetTimeKind ResetTimeKind { get; set; } = CustomResetTimeKind.Auto;

    /// <summary>
    /// 可选：额度上限的 JSON 路径（<see cref="CustomDataKind.UsedValue"/> /
    /// <see cref="CustomDataKind.RemainingValue"/> 用）。与 <see cref="FixedLimit"/> 同时填写时，
    /// 本字段优先；解析失败时该窗口单独报错，不回退到 FixedLimit（避免用一个隐性默认值掩盖配置错误）。
    /// </summary>
    public string? LimitPath { get; set; }

    /// <summary>可选：固定额度上限，LimitPath 未填时使用。</summary>
    public double? FixedLimit { get; set; }

    /// <summary>可选：数值单位/币种展示（<see cref="CustomDataKind.Balance"/> 时作为币种，如 "CNY"）。</summary>
    public string? Unit { get; set; }

    /// <summary>排序序号：决定该窗口在卡片里的显示顺序（越小越靠前）。</summary>
    public int SortOrder { get; set; }
}
