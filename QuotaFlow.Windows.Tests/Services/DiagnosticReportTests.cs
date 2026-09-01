using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="DiagnosticReport"/> 的测试。
///
/// 报告是要发给开发者的，所以「不含敏感信息」是硬性要求而非建议，这里当作首要断言来守。
/// </summary>
public class DiagnosticReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 22, 0, 0, TimeSpan.Zero);

    private static DiagnosticInput Sample() => new()
    {
        AppVersion = "0.9.2",
        OsVersion = "Windows 11 26200",
        IsRemoteSession = true,
        ProxyMode = ProxyMode.System,
        EffectiveProxy = "192.168.10.101:8889",
        AutoRefreshIntervalMinutes = 5,
        LastSettingsSaveSucceeded = false,
        Providers =
        [
            new ProviderDiagnostic
            {
                ProviderId = "claude",
                State = ProviderState.Available,
                LastUpdatedAt = Now.AddMinutes(-3),
                DataSourceHost = "api.anthropic.com",
            },
            new ProviderDiagnostic
            {
                ProviderId = "minimax",
                State = ProviderState.NetworkError,
                ErrorCategory = ErrorCategory.Network,
                DataSourceHost = "api.minimaxi.com",
            },
        ],
        RecentErrorTypes = ["System.ArgumentException", "System.ArgumentException", "System.IO.IOException"],
    };

    [Fact]
    public void Build_ContainsKeyFacts()
    {
        var text = DiagnosticReport.Build(Sample(), Now);

        Assert.Contains("0.9.2", text, StringComparison.Ordinal);
        Assert.Contains("claude", text, StringComparison.Ordinal);
        Assert.Contains("NetworkError", text, StringComparison.Ordinal);
        Assert.Contains("192.168.10.101:8889", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_FlagsFailedSettingsSave_TheProblemNobodyCouldSee()
    {
        // 今天的事故就是"设置连续写盘失败但界面毫无表现"，报告必须把它挑明。
        var text = DiagnosticReport.Build(Sample(), Now);

        Assert.Contains("失败（改动未落盘）", text, StringComparison.Ordinal);
        Assert.Contains("重启后会丢失", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SystemProxyWithActualProxy_ExplainsTheEnvVarPitfall()
    {
        // 浏览器正常而本应用全挂的那个坑，报告里直接给出排查方向。
        var text = DiagnosticReport.Build(Sample(), Now);

        Assert.Contains("HTTP_PROXY", text, StringComparison.Ordinal);
        Assert.Contains("直连", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GroupsRecentErrorTypesWithCounts()
    {
        var text = DiagnosticReport.Build(Sample(), Now);

        Assert.Contains("2 次  System.ArgumentException", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoProviders_DoesNotLookBroken()
    {
        var input = new DiagnosticInput
        {
            AppVersion = "0.9.2",
            OsVersion = "Windows 11",
            ProxyMode = ProxyMode.Direct,
        };

        var text = DiagnosticReport.Build(input, Now);

        Assert.Contains("没有已配置的平台", text, StringComparison.Ordinal);
        Assert.Contains("（无）", text, StringComparison.Ordinal);
        Assert.Contains("未记录到写盘失败", text, StringComparison.Ordinal);
    }

    // ---- 脱敏（硬性要求）----

    [Fact]
    public void Build_NeverContainsQuotaValues()
    {
        // ProviderDiagnostic 里根本没有额度字段，这条测试锁住这个设计：
        // 将来有人往输入模型里加"剩余百分比"之类，这里会立刻暴露。
        var props = typeof(ProviderDiagnostic).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("RemainingPercent", props);
        Assert.DoesNotContain("UsedPercent", props);
        Assert.DoesNotContain("Balance", props);
    }

    [Fact]
    public void HostOnly_StripsPathAndQueryWhichMayCarryTokens()
    {
        // 查询串里可能带 sec_token 之类的参数，路径也可能暴露账号标识。
        var host = DiagnosticReport.HostOnly(
            "https://bailian-cs.console.aliyun.com/data/api.json?action=X&sec_token=SECRETVALUE");

        Assert.Equal("bailian-cs.console.aliyun.com", host);
        Assert.DoesNotContain("SECRETVALUE", host!, StringComparison.Ordinal);
    }

    [Fact]
    public void HostOnly_InvalidOrEmpty_ReturnsNull()
    {
        Assert.Null(DiagnosticReport.HostOnly(null));
        Assert.Null(DiagnosticReport.HostOnly("   "));
        Assert.Null(DiagnosticReport.HostOnly("not a url"));
    }

    [Fact]
    public void Build_WithTokenLikeDataSource_OnlyHostSurvives()
    {
        var input = new DiagnosticInput
        {
            AppVersion = "0.9.2",
            OsVersion = "Windows 11",
            ProxyMode = ProxyMode.Direct,
            Providers =
            [
                new ProviderDiagnostic
                {
                    ProviderId = "alibaba-tokenplan",
                    State = ProviderState.Available,
                    DataSourceHost = DiagnosticReport.HostOnly(
                        "https://bailian-cs.console.aliyun.com/data/api.json?sec_token=LEAKME"),
                },
            ],
        };

        var text = DiagnosticReport.Build(input, Now);

        Assert.DoesNotContain("LEAKME", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sec_token", text, StringComparison.Ordinal);
    }
}
