namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 解析 WebView2 <c>ExecuteScriptAsync</c> 的返回值。
///
/// 这个类存在的唯一原因是它曾经出过一个真实的判定 bug：<c>ExecuteScriptAsync</c> 返回的是
/// <b>JSON 序列化后</b>的结果，不是脚本里那个原始字符串——
/// <list type="bullet">
/// <item><description>JS 返回 <c>'NO'</c> → C# 拿到带引号的 <c>"NO"</c>（4 个字符）；</description></item>
/// <item><description>JS 抛异常 / 返回 undefined → C# 拿到字面量 <c>null</c>（4 个字符，不是 C# 的 null）。</description></item>
/// </list>
/// 早期代码用 <c>result.Trim('"').Length &gt; 0</c> 判断"脚本是否返回了内容"，于是把字面量
/// <c>null</c> 当成了有效返回值——具体后果是：阿里云百炼登录窗口在页面<b>根本没有登录</b>的
/// 情况下判定为"已登录"，抓走一堆无效 Cookie 后自动关窗，用户看到的现象是"窗口一闪就没了、
/// 额度查询始终失败"。
///
/// 教训：不要用"长度是否大于 0"这类模糊条件判断脚本返回值，应当让脚本返回<b>约定好的、
/// 可精确匹配的标记</b>，宿主侧严格匹配该标记。
/// </summary>
public static class WebViewScriptResult
{
    /// <summary>JSON 里的字面量 null——脚本抛异常或返回 undefined 时会拿到它，必须当作"无结果"。</summary>
    private const string JsonNullLiteral = "null";

    /// <summary>
    /// 剥掉 <c>ExecuteScriptAsync</c> 结果外层的 JSON 引号，返回脚本实际产生的字符串。
    /// 结果是 JSON 字面量 <c>null</c>、空串或 C# null 时统一返回 <see cref="string.Empty"/>，
    /// 让调用方无需区分这些"没有结果"的形态。
    /// </summary>
    public static string Unquote(string? executeScriptResult)
    {
        if (string.IsNullOrWhiteSpace(executeScriptResult))
        {
            return string.Empty;
        }

        var value = executeScriptResult.Trim();
        if (string.Equals(value, JsonNullLiteral, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value;
    }

    /// <summary>
    /// 判断脚本结果是否带有约定的成功标记前缀。用精确的前缀匹配代替"长度大于 0"这类模糊判断，
    /// 从根上避免把 JSON 字面量 <c>null</c>、错误标记等误判成成功。
    /// </summary>
    public static bool HasMarker(string? executeScriptResult, string marker) =>
        Unquote(executeScriptResult).StartsWith(marker, StringComparison.Ordinal);
}
