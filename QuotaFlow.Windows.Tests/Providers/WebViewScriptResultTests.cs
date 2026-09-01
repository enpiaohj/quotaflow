using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

/// <summary>
/// WebView2 <c>ExecuteScriptAsync</c> 返回值解析的回归测试。
///
/// 这些测试对应一个真实线上缺陷：脚本返回值是 JSON 序列化后的结果，早期代码用
/// "去掉引号后长度大于 0" 判断脚本是否返回了有效内容，结果把 JS 异常/undefined 对应的
/// 字面量 <c>null</c>（4 个字符）当成了有效值，导致阿里云百炼登录窗口在<b>完全未登录</b>的
/// 页面上判定"已登录"、抓走无效 Cookie 就自动关窗。
/// </summary>
public class WebViewScriptResultTests
{
    [Fact]
    public void Unquote_JsonNullLiteral_TreatedAsNoResult()
    {
        // 核心回归：JS 抛异常或返回 undefined 时，ExecuteScriptAsync 给的是字面量 null，
        // 绝不能被当成"拿到了内容"。
        Assert.Equal(string.Empty, WebViewScriptResult.Unquote("null"));
    }

    [Fact]
    public void HasMarker_JsonNullLiteral_DoesNotMatchAnyMarker()
    {
        // 这正是当初误判"已登录"的那条路径。
        Assert.False(WebViewScriptResult.HasMarker("null", "YES:"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unquote_NullOrBlank_ReturnsEmpty(string? raw)
    {
        Assert.Equal(string.Empty, WebViewScriptResult.Unquote(raw));
    }

    [Fact]
    public void Unquote_QuotedString_StripsSurroundingQuotes()
    {
        Assert.Equal("YES:12", WebViewScriptResult.Unquote("\"YES:12\""));
    }

    [Fact]
    public void Unquote_EmptyJsonString_ReturnsEmpty()
    {
        // JS 返回 '' → JSON 是 ""（两个引号字符）。
        Assert.Equal(string.Empty, WebViewScriptResult.Unquote("\"\""));
    }

    [Fact]
    public void HasMarker_LoggedInResult_Matches()
    {
        Assert.True(WebViewScriptResult.HasMarker("\"YES:12\"", "YES:"));
    }

    [Fact]
    public void HasMarker_NotLoggedInResult_DoesNotMatch()
    {
        Assert.False(WebViewScriptResult.HasMarker("\"NO\"", "YES:"));
    }

    [Fact]
    public void HasMarker_ScriptErrorResult_DoesNotMatch()
    {
        Assert.False(WebViewScriptResult.HasMarker("\"ERR\"", "YES:"));
    }

    [Fact]
    public void HasMarker_MarkerMustBeAtStart_NotAnywhere()
    {
        // 不能用 Contains——那样页面上任何位置出现该标记都会误判。
        Assert.False(WebViewScriptResult.HasMarker("\"NO-BUT-CONTAINS-YES:12\"", "YES:"));
    }
}
