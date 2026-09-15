namespace QuotaFlow.Windows.Core.Models;

/// <summary>
/// 失败原因的粗分类，用于渲染不同的用户提示文案，避免把所有失败都堆成一句"出错了"。
/// </summary>
public enum ErrorCategory
{
    /// <summary>未发生错误。</summary>
    None,

    /// <summary>尚未配置凭据/API Key。</summary>
    NotConfigured,

    /// <summary>401/403，鉴权失败或凭据过期。</summary>
    AuthenticationExpired,

    /// <summary>429，触发限流。</summary>
    RateLimited,

    /// <summary>网络不可达、连接被拒绝等。</summary>
    Network,

    /// <summary>请求超时。</summary>
    Timeout,

    /// <summary>DNS 解析失败。</summary>
    Dns,

    /// <summary>TLS/SSL 握手失败。</summary>
    Tls,

    /// <summary>HTTP 状态码非 2xx（且不属于鉴权/限流），或业务层错误码。</summary>
    ServerError,

    /// <summary>响应体不是预期的 JSON 结构，可能是上游接口发生了变化。</summary>
    ResponseFormat,

    /// <summary>其他未归类的错误。</summary>
    Unknown,

    /// <summary>凭据有效，但该账号未检测到任何已知套餐（如火山方舟 Coding Plan / Agent Plan 均未订阅）。</summary>
    NoPlan,
}
