namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 余额类指标（DeepSeek 等按 Token 计费的平台），与百分比类的 QuotaWindow 是两种不同的展示形态，
/// 不强行抹平成同一种数据结构。
/// </summary>
/// <param name="Amount">可用余额金额。</param>
/// <param name="Currency">币种（如 CNY / USD），原样透传服务端返回值，不做假设性转换。</param>
/// <param name="GrantedAmount">赠送余额，若服务端有区分。</param>
/// <param name="ToppedUpAmount">充值余额，若服务端有区分。</param>
public sealed record BalanceMetric(
    decimal Amount,
    string Currency,
    decimal? GrantedAmount = null,
    decimal? ToppedUpAmount = null);
