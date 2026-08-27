namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 平台快照的展示状态。UI 据此决定颜色、图标和文案，颜色本身不能是唯一的状态表达。
/// </summary>
public enum ProviderState
{
    /// <summary>正在查询中，尚未拿到任何数据（也没有可用缓存）。</summary>
    Loading,

    /// <summary>数据新鲜且额度充足（剩余 &gt; 30%）。</summary>
    Available,

    /// <summary>剩余额度偏低（10%~30%），需要用户关注。</summary>
    Low,

    /// <summary>剩余额度告急（&lt; 10%）。</summary>
    Critical,

    /// <summary>用户尚未配置该平台所需的凭据（如 MiniMax/DeepSeek 未填 API Key）。</summary>
    NotConfigured,

    /// <summary>本机 OAuth 凭据已过期或被服务端拒绝，需要用户重新登录对应 CLI。</summary>
    AuthenticationExpired,

    /// <summary>网络不可达、超时、DNS/TLS 失败等瞬时传输错误。</summary>
    NetworkError,

    /// <summary>触发服务端限流（HTTP 429）。</summary>
    RateLimited,

    /// <summary>服务端返回非 2xx 或响应体格式超出预期，非鉴权类错误。</summary>
    ProviderError,

    /// <summary>展示的是缓存数据，且缓存已超过合理新鲜度窗口。</summary>
    Stale,
}
