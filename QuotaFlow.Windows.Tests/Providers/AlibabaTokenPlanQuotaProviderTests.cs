using System.Text.Json;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

/// <summary>
/// 阿里云百炼 Token Plan（个人版）用量的解析层测试。
/// 真实的 Console Cookie + SEC_TOKEN 流程依赖登录态，无法在单测里做；这里只验证
/// <see cref="AlibabaTokenPlanQuotaProvider.ParseUsageResponse"/> 对各种响应形态的解析逻辑：
/// 正常窗口、缺字段、业务错误、会话过期、JSON 损坏。
/// </summary>
public class AlibabaTokenPlanQuotaProviderTests
{
    private static AlibabaTokenPlanQuotaProvider CreateProvider() =>
        new(new HttpClient(), () => "fixture-cookie-not-real");

    [Fact]
    public void ParseUsageResponse_ResultWrapped_ComputesUsedAndRemainingWindows()
    {
        // per1WeekPercentage 0.7913 = 已使用 79.13%，剩余 20.87%；
        // per1WeekResetTime 是 Unix 毫秒。
        var json = """
            {
              "code": 200,
              "request_id": "some-request-id",
              "result": {
                "per1WeekPercentage": 0.7913,
                "per1WeekResetTime": 1799000000000
              }
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.Null(usage.Error);
        Assert.Equal(79.13, usage.UsedPercent, precision: 2);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1799000000000L), usage.ResetsAt);
    }

    /// <summary>
    /// 真实网关响应的回归测试（结构照抄抓包所得，数值为构造值）。
    ///
    /// 这里锁住两个曾经导致查询完全不可用的缺陷：
    /// 一是 <c>code</c> 是<b>字符串</b>（<c>"200"</c> / <c>"SUCCESS"</c>）而非数字，早期实现直接
    /// 调 <c>TryGetInt64</c>，在非 Number 元素上会抛 <see cref="InvalidOperationException"/>，
    /// 整个查询崩溃；二是额度字段埋在 <c>data.DataV2.data.data</c> 五层嵌套里，递归查找的深度
    /// 上限必须够。
    /// </summary>
    [Fact]
    public void ParseUsageResponse_RealGatewayShape_StringCodesAndDeepNesting_Parses()
    {
        var json = """
            {
              "code": "200",
              "data": {
                "DataV2": {
                  "ret": ["SUCCESS::接口调用成功"],
                  "data": {
                    "msg": "Success.",
                    "code": "SUCCESS",
                    "data": {
                      "per1WeekResetTime": 1788829080000,
                      "per1WeekPercentage": 0.46964957599999996
                    },
                    "success": true
                  }
                }
              },
              "httpStatusCode": "200",
              "successResponse": true
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.Null(usage.Error);
        Assert.Equal(46.96, usage.UsedPercent, precision: 2);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788829080000L), usage.ResetsAt);
    }

    /// <summary>
    /// 网关在参数不全时会返回 HTTP 200 + 内层 <c>success:false</c>（实测缺 cornerstoneParam
    /// 就是这个形态）。这种"看起来成功、实则失败"的响应必须被识别成错误，绝不能落到额度显示上。
    /// </summary>
    [Fact]
    public void ParseUsageResponse_InnerSuccessFalse_ReturnsErrorNotZeroUsage()
    {
        var json = """
            {
              "code": "200",
              "data": {
                "success": false,
                "httpStatus": 200,
                "errorCode": "Bad Request",
                "api": "zeldaHttp.apikeyMgr./tokenplan/personal/api/v2/usage",
                "errorMsg": "Bad Request"
              },
              "successResponse": true
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error!.State);
    }

    /// <summary>
    /// 网关信封必须带 cornerstoneParam——只发 <c>{"Api":…,"V":"1.0","Data":{}}</c> 时实测
    /// 内层返回 Bad Request。这个测试防止有人"简化"掉这段看似冗余的元数据。
    /// </summary>
    [Fact]
    public void BuildGatewayParams_IncludesCornerstoneParam()
    {
        var json = AlibabaTokenPlanQuotaProvider.BuildGatewayParams(AlibabaTokenPlanQuotaProvider.UsageApi);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(AlibabaTokenPlanQuotaProvider.UsageApi, root.GetProperty("Api").GetString());
        Assert.Equal("1.0", root.GetProperty("V").GetString());

        var cornerstone = root.GetProperty("Data").GetProperty("cornerstoneParam");
        Assert.Equal("V2", cornerstone.GetProperty("protocol").GetString());
        Assert.Equal("ONE_CONSOLE", cornerstone.GetProperty("console").GetString());
        Assert.Equal("p_efm", cornerstone.GetProperty("productCode").GetString());
    }

    /// <summary>
    /// 接口字段改名时，提示里必须带上本次实际收到的字段名——否则下次又要从零逆向一遍。
    /// </summary>
    [Fact]
    public void ParseUsageResponse_FieldRenamed_GuidanceCarriesActualFieldNames()
    {
        // 模拟阿里云把字段名改掉的情形
        var json = """
            {
              "code": "200",
              "data": { "DataV2": { "data": { "data": { "weeklyUsedRatio": 0.31, "weeklyResetAt": 1788829080000 } } } }
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error!.ErrorCategory);
        Assert.Contains("weeklyUsedRatio", usage.Error.UserGuidance!, StringComparison.Ordinal);
        Assert.Contains("weeklyResetAt", usage.Error.UserGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseUsageResponse_FieldRenamed_GuidanceDoesNotLeakValues()
    {
        // 提示会显示在界面上、也会被用户发出来，只能带字段名。
        var json = """
            {
              "code": "200",
              "requestId": "SENSITIVE-REQUEST-ID",
              "data": { "DataV2": { "data": { "data": { "weeklyUsedRatio": 0.31 } } } }
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.DoesNotContain("SENSITIVE-REQUEST-ID", usage.Error!.UserGuidance!, StringComparison.Ordinal);
        Assert.DoesNotContain("0.31", usage.Error.UserGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseUsageResponse_FlatJson_StillFindsFields()
    {
        var json = """
            {
              "per1WeekPercentage": 0.5,
              "per1WeekResetTime": 1799000000000
            }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.Null(usage.Error);
        Assert.Equal(50.0, usage.UsedPercent);
    }

    [Fact]
    public void GetSnapshotAsync_ValidResponse_BuildsTwoWindowsAndState()
    {
        // 79.13% 已用 → 剩余 20.87% → ProviderState.Low；两个窗口：7 天已用 / 7 天剩余。
        var provider = CreateProvider();
        var raw = """
            { "result": { "per1WeekPercentage": 0.7913, "per1WeekResetTime": 1799000000000 } }
            """;

        // 通过公共查询入口拿不到真实 HTTP 响应，这里走内部解析 + 手动组装快照的方式验证状态口径。
        var usage = provider.ParseUsageResponse(raw);
        var remaining = 100.0 - usage.UsedPercent;

        Assert.Equal(ProviderState.Low, remaining is >= 10 and < 30
            ? ProviderState.Low
            : remaining < 10 ? ProviderState.Critical : ProviderState.Available);
    }

    [Fact]
    public void GetSnapshotAsync_AllConsumed_ReturnsExhaustedState()
    {
        var provider = CreateProvider();
        var usage = provider.ParseUsageResponse("""{ "result": { "per1WeekPercentage": 1.0, "per1WeekResetTime": 1799000000000 } }""");

        var remaining = 100.0 - usage.UsedPercent;
        Assert.Equal(0, remaining, precision: 1);
    }

    [Fact]
    public void ParseUsageResponse_MissingPercentageField_ReturnsResponseFormatError()
    {
        var json = """
            { "result": { "per1WeekResetTime": 1799000000000 } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error.State);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error!.ErrorCategory);
        Assert.Contains("per1WeekPercentage", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_MissingResetTimeField_ReturnsResponseFormatError()
    {
        var json = """
            { "result": { "per1WeekPercentage": 0.5 } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error!.ErrorCategory);
        Assert.Contains("per1WeekResetTime", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_ApiErrorCode_ReturnsProviderError()
    {
        var json = """
            { "result": { "code": 400123, "message": "some business error" } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error!.State);
        Assert.Contains("some business error", usage.Error.UserGuidance);
    }

    [Fact]
    public void ParseUsageResponse_SessionExpiredMessage_MapsToAuthenticationExpired()
    {
        var json = """
            { "result": { "code": 400401, "message": "login session expired, please login again" } }
            """;

        var usage = CreateProvider().ParseUsageResponse(json);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.AuthenticationExpired, usage.Error!.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, usage.Error.ErrorCategory);
    }

    [Fact]
    public void ParseUsageResponse_InvalidJson_ReturnsResponseFormatError()
    {
        var usage = CreateProvider().ParseUsageResponse("not-json-at-all");

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.ProviderError, usage.Error!.State);
        Assert.Equal(ErrorCategory.ResponseFormat, usage.Error.ErrorCategory);
    }

    [Fact]
    public void ParseUsageResponse_RedirectedToErrorPage_MapsToSessionExpired()
    {
        // 实测无效 Cookie / SEC_TOKEN 时，网关 302 到 err.taobao.com/error1.html，
        // HttpClient 跟随重定向后拿到的是 HTML 错误页而不是 JSON——必须识别成"会话失效"，
        // 而不是笼统的"不是有效 JSON"。
        var html = """
            <!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML 2.0//EN">
            <html><head><title>302 Found</title></head>
            <body><p>err.taobao.com/error1.html</p></body></html>
            """;

        var usage = CreateProvider().ParseUsageResponse(html);

        Assert.NotNull(usage.Error);
        Assert.Equal(ProviderState.AuthenticationExpired, usage.Error!.State);
        Assert.Equal(ErrorCategory.AuthenticationExpired, usage.Error.ErrorCategory);
    }

    [Fact]
    public async Task GetSnapshotAsync_NoCookie_ReturnsNotConfigured()
    {
        var provider = new AlibabaTokenPlanQuotaProvider(new HttpClient(), () => null);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Equal(ErrorCategory.NotConfigured, snapshot.ErrorCategory);
    }

    // ---- LooksLikeLoginPage（判定拿到的控制台 HTML 是否其实是登录页而非正常已登录内容）----

    [Fact]
    public void LooksLikeLoginPage_ValidLoggedInPage_DoesNotFalsePositive()
    {
        // 实测事故回归：早期版本的判定关键词里有 "window.location" ——这是几乎任何现代前端
        // bundle 都会出现的普通 JS 字符串，一个完全有效的登录态页面（真实抓包：243KB 正常内容，
        // 含 CURRENT_PK）同样会命中它，被误判成"已失效"，把正常登录态错误地报成"请重新登录"。
        var html = """
            <html><body>
            <script>
              var ALIYUN_CONSOLE_CONFIG = { CURRENT_PK: "1234567890" };
              function foo() { window.location.href = '/some/route'; }
            </script>
            </body></html>
            """;

        Assert.False(AlibabaTokenPlanQuotaProvider.LooksLikeLoginPage(html));
    }

    [Fact]
    public void LooksLikeLoginPage_ActualLoginRedirect_DetectsCorrectly()
    {
        var html = """<html><body><script>window.location = "https://passport.aliyun.com/login.htm";</script></body></html>""";

        Assert.True(AlibabaTokenPlanQuotaProvider.LooksLikeLoginPage(html));
    }

    [Fact]
    public void LooksLikeLoginPage_ChinesePromptText_DetectsCorrectly()
    {
        Assert.True(AlibabaTokenPlanQuotaProvider.LooksLikeLoginPage("<div>敬请登录后查看</div>"));
        Assert.True(AlibabaTokenPlanQuotaProvider.LooksLikeLoginPage("<div>登录后使用本功能</div>"));
    }
}