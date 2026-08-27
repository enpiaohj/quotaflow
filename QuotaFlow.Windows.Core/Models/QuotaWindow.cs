namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 单个限速窗口，例如 Claude 的 "5 小时" / "7 天"，或 Codex 的 "primary_window"。
/// 不存在的窗口不应该被构造出来（调用方应直接跳过），而不是用 0% 占位。
/// </summary>
/// <param name="Id">
/// 窗口的稳定标识，跨平台尽量复用同一套命名（five_hour / seven_day / weekly_limit / monthly ...），
/// 便于 UI i18n 和 tray 分组复用同一份映射表。未知窗口原样保留服务端给出的 key。
/// </param>
/// <param name="DisplayName">展示用的名称，例如 "5 小时" "7 天"。</param>
/// <param name="RemainingPercent">
/// 剩余百分比，范围固定裁剪到 0..100。服务端返回的是"已使用"时，调用方必须换算：
/// remaining = 100 - utilization。
/// </param>
/// <param name="UsedPercent">已使用百分比，与 RemainingPercent 互补（RemainingPercent + UsedPercent == 100）。</param>
/// <param name="ResetsAt">窗口重置时间；服务端未提供时为 null，不得臆造。</param>
/// <param name="UsedValueUsd">部分平台（如 ZenMux）额外提供的已用美元额度，非必填。</param>
/// <param name="MaxValueUsd">部分平台额外提供的美元额度上限，非必填。</param>
public sealed record QuotaWindow(
    string Id,
    string DisplayName,
    double RemainingPercent,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    double? UsedValueUsd = null,
    double? MaxValueUsd = null)
{
    /// <summary>
    /// 由"已使用百分比"构造一个窗口，自动完成 remaining = 100 - utilization 的换算，
    /// 并把结果裁剪到 [0, 100]，防止服务端返回异常值（如轻微超过 100 或负数）污染 UI。
    /// </summary>
    public static QuotaWindow FromUtilization(
        string id,
        string displayName,
        double utilization,
        DateTimeOffset? resetsAt,
        double? usedValueUsd = null,
        double? maxValueUsd = null)
    {
        var used = Math.Clamp(utilization, 0.0, 100.0);
        var remaining = 100.0 - used;
        return new QuotaWindow(id, displayName, remaining, used, resetsAt, usedValueUsd, maxValueUsd);
    }

    /// <summary>
    /// 由"剩余百分比"构造一个窗口（MiniMax 等接口直接给剩余而非已用）。
    /// </summary>
    public static QuotaWindow FromRemaining(
        string id,
        string displayName,
        double remainingPercent,
        DateTimeOffset? resetsAt)
    {
        var remaining = Math.Clamp(remainingPercent, 0.0, 100.0);
        var used = 100.0 - remaining;
        return new QuotaWindow(id, displayName, remaining, used, resetsAt);
    }

    /// <summary>距离重置的剩余时长；没有重置时间或已过期时为 null。</summary>
    public TimeSpan? RemainingDuration(DateTimeOffset? now = null)
    {
        if (ResetsAt is null)
        {
            return null;
        }

        var reference = now ?? DateTimeOffset.UtcNow;
        var delta = ResetsAt.Value - reference;
        return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
    }
}
