namespace QuotaFlow.Windows.Core.Models;

/// <summary>自定义平台的鉴权方式。</summary>
public enum CustomAuthKind
{
    /// <summary>Authorization: Bearer &lt;key&gt;。</summary>
    BearerKey,

    /// <summary>把 key 放进指定名称的请求头（如 X-API-Key）。</summary>
    CustomHeader,

    /// <summary>接口无需鉴权，不要求填写 Key。</summary>
    None,
}

/// <summary>自定义平台返回的数据语义，决定解析成额度窗口还是余额。</summary>
public enum CustomDataKind
{
    /// <summary>JSON 路径取到的是"已使用百分比"（remaining = 100 - value）。</summary>
    UtilizationPercent,

    /// <summary>JSON 路径取到的是"剩余百分比"（原样使用）。</summary>
    RemainingPercent,

    /// <summary>JSON 路径取到的是余额金额（配合 Currency 展示）。</summary>
    Balance,
}

/// <summary>
/// 用户在设置页手动添加的自定义平台定义（非敏感，不含任何密钥）。随 <see cref="AppSettings"/>
/// 一起 DPAPI 加密持久化；API Key 仍只走 Windows 凭据管理器，永不进入本模型。
/// </summary>
public sealed class CustomPlatformSettings
{
    /// <summary>
    /// 稳定标识，新增时分配为 <c>custom-{n}</c>（自增），之后**永不改变**。
    /// 用于 ProviderId、凭据键名和面板顺序；用户看到的显示名是 <see cref="Name"/>。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，例如 "OpenCode"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>查询接口完整 URL（必填）。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>鉴权方式，默认 Bearer Key。</summary>
    public CustomAuthKind AuthKind { get; set; } = CustomAuthKind.BearerKey;

    /// <summary>AuthKind == CustomHeader 时的请求头名称（例如 X-API-Key）。</summary>
    public string? HeaderName { get; set; }

    /// <summary>数据语义，默认"已使用百分比"。</summary>
    public CustomDataKind DataKind { get; set; } = CustomDataKind.UtilizationPercent;

    /// <summary>取值用的点号 JSON 路径（如 <c>data.usage_percent</c>、<c>data.items[0].quota</c>）。</summary>
    public string ValuePath { get; set; } = string.Empty;

    /// <summary>可选：窗口重置时间的 JSON 路径（支持 epoch 毫秒/秒数字或 ISO 字符串）。</summary>
    public string? ResetsAtPath { get; set; }

    /// <summary>DataKind == Balance 时的币种，默认 CNY。</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>Windows 凭据管理器里的键名；凭据读写双方都从它派生，避免两处维护漂移。</summary>
    public string CredentialKeyName => $"custom:{Id}:ApiKey";
}
