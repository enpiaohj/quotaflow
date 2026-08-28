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

/// <summary>
/// 单个额度窗口的数据语义（v1.1.0 起从平台级下放到窗口级——同一平台的不同窗口可以各自选择）。
/// 新增成员一律追加在末尾：本枚举以序号（而非名称）序列化落盘，插队会让旧配置错位。
/// </summary>
public enum CustomDataKind
{
    /// <summary>JSON 路径取到的是"已使用百分比"（remaining = 100 - value）。</summary>
    UtilizationPercent,

    /// <summary>JSON 路径取到的是"剩余百分比"（原样使用）。</summary>
    RemainingPercent,

    /// <summary>JSON 路径取到的是余额金额（配合 Unit 展示币种）。</summary>
    Balance,

    /// <summary>JSON 路径取到的是"已使用数值"，需配合 LimitPath/FixedLimit 换算成百分比。</summary>
    UsedValue,

    /// <summary>JSON 路径取到的是"剩余数值"，需配合 LimitPath/FixedLimit 换算成百分比。</summary>
    RemainingValue,
}

/// <summary>
/// 用户在设置页手动添加的自定义平台定义（非敏感，不含任何密钥）。随 <see cref="AppSettings"/>
/// 一起 DPAPI 加密持久化；API Key 仍只走 Windows 凭据管理器，永不进入本模型。
///
/// v1.1.0：数据模型由"一个平台 → 一个额度指标"改为"一个平台 → 一次接口请求 → 多个额度窗口"
/// （见 <see cref="QuotaWindows"/>）。<see cref="ValuePath"/> / <see cref="ResetsAtPath"/> /
/// <see cref="DataKind"/> / <see cref="Currency"/> 四个字段保留仅用于加载 v1.0.5 旧配置时一次性
/// 迁移进 <c>QuotaWindows[0]</c>（见 <see cref="Services.AppSettingsStore"/>）；新代码不应再写它们，
/// 迁移完成后会被清空。
/// </summary>
public sealed class CustomPlatformSettings
{
    /// <summary>
    /// 稳定标识，新增时分配为 <c>custom-{n}</c>（自增），之后**永不改变**。
    /// 用于 ProviderId、凭据键名和面板顺序；用户看到的显示名是 <see cref="Name"/>。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，例如 "OpenCode GO"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>查询接口完整 URL（必填）。同一平台只发一次请求，所有窗口从同一份响应里取值。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>鉴权方式，默认 Bearer Key。</summary>
    public CustomAuthKind AuthKind { get; set; } = CustomAuthKind.BearerKey;

    /// <summary>AuthKind == CustomHeader 时的请求头名称（例如 X-API-Key）。</summary>
    public string? HeaderName { get; set; }

    /// <summary>
    /// 该平台的额度窗口列表，按 <see cref="CustomQuotaWindowSettings.SortOrder"/> 显示。
    /// 正常配置下至少有一个窗口；空列表只会在"v1.0.5 旧配置尚未迁移"或"配置文件被手改坏"时出现，
    /// <see cref="Providers.CustomPlatformProvider"/> 对空列表按未配置处理，绝不当成 0% 展示。
    /// </summary>
    public List<CustomQuotaWindowSettings> QuotaWindows { get; set; } = [];

    /// <summary>Windows 凭据管理器里的键名；凭据读写双方都从它派生，避免两处维护漂移。</summary>
    public string CredentialKeyName => $"custom:{Id}:ApiKey";

    // ---- 以下四个字段仅用于迁移 v1.0.5 旧格式，新代码不应再读写 ----

    /// <summary>[旧字段，仅迁移用] v1.0.5 单窗口取值路径。</summary>
    public string? ValuePath { get; set; }

    /// <summary>[旧字段，仅迁移用] v1.0.5 单窗口重置时间路径。</summary>
    public string? ResetsAtPath { get; set; }

    /// <summary>[旧字段，仅迁移用] v1.0.5 单窗口数据语义。</summary>
    public CustomDataKind? DataKind { get; set; }

    /// <summary>[旧字段，仅迁移用] v1.0.5 余额币种。</summary>
    public string? Currency { get; set; }
}
