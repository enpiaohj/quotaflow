namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 解析平台接口地址：配置里填了覆盖值就用覆盖值（去首尾空白），否则用内置默认地址。
/// 覆盖值来自设置页各平台分组下的"接口地址（可选）"输入框，经加密的 settings.json 持久化。
/// </summary>
public static class EndpointResolver
{
    public static string Resolve(string? overrideUrl, string defaultUrl) =>
        string.IsNullOrWhiteSpace(overrideUrl) ? defaultUrl : overrideUrl.Trim();
}
