namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 一次查询得到的、某个平台的完整展示快照。四个平台共用这一个类型，但通过
/// <see cref="QuotaWindows"/> / <see cref="Balance"/> 两个可选字段保留差异——
/// Claude/Codex/MiniMax 走额度窗口，DeepSeek 走余额，不强行抹平成同一种数字。
/// </summary>
public sealed record ProviderSnapshot
{
    /// <summary>平台标识，例如 "claude" "codex" "minimax" "deepseek"。</summary>
    public required string ProviderId { get; init; }

    /// <summary>展示名称，例如 "Claude"。</summary>
    public required string DisplayName { get; init; }

    /// <summary>当前展示状态，驱动 UI 的颜色/图标/文案。</summary>
    public required ProviderState State { get; init; }

    /// <summary>
    /// 额度窗口列表（Claude/Codex/MiniMax 使用）。不存在的窗口不出现在列表中，
    /// 而不是用 0% 的条目占位。DeepSeek 场景下该列表为空。
    /// </summary>
    public IReadOnlyList<QuotaWindow> QuotaWindows { get; init; } = [];

    /// <summary>余额指标（DeepSeek 使用）。Claude/Codex/MiniMax 场景下为 null。</summary>
    public BalanceMetric? Balance { get; init; }

    /// <summary>本次快照对应数据的最后更新时间；用于渲染"N 分钟前更新"。</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>数据来源说明，例如 "api.anthropic.com/api/oauth/usage"，用于诊断/关于页展示。</summary>
    public string? DataSource { get; init; }

    /// <summary>失败原因分类；State 为非错误状态时为 None。</summary>
    public ErrorCategory ErrorCategory { get; init; } = ErrorCategory.None;

    /// <summary>面向用户的引导文案，例如"请在 Claude Code 重新登录"。绝不包含 Token/Authorization 内容。</summary>
    public string? UserGuidance { get; init; }

    /// <summary>是否命中的是缓存数据（配合 State == Stale 使用）。</summary>
    public bool IsFromCache { get; init; }

    /// <summary>
    /// 快照中"最紧张"的剩余百分比（多个窗口取最小值），用于托盘图标/汇总视图判断整体健康度。
    /// 只统计成功解析的窗口（<see cref="QuotaWindow.IsError"/> == false）——某个窗口取值失败时，
    /// 它的占位 0% 绝不能被当成"这个平台只剩 0% 了"。全部窗口都失败或没有任何窗口时返回 null，
    /// 调用方不得当作 0% 处理。
    /// </summary>
    public double? WorstRemainingPercent
    {
        get
        {
            var healthy = QuotaWindows.Where(w => !w.IsError).ToList();
            return healthy.Count == 0 ? null : healthy.Min(w => w.RemainingPercent);
        }
    }
}
