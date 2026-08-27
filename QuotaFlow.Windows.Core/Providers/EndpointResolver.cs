namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 解析平台接口地址：配置里填了覆盖值就用覆盖值（去首尾空白），否则用内置默认地址。
/// 覆盖值来自设置页"接口地址（高级）"卡片，经加密的 settings.json 持久化。
/// </summary>
internal static class EndpointResolver
{
    public static string Resolve(string? overrideUrl, string defaultUrl) =>
        string.IsNullOrWhiteSpace(overrideUrl) ? defaultUrl : overrideUrl.Trim();
}
