using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class CodexQuotaProviderTests
{
    private static CodexQuotaProvider CreateProvider() => new(new HttpClient());

    [Fact]
    public void ParseResponse_MapsPrimaryAndSecondaryWindows()
    {
        var json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 9, "limit_window_seconds": 18000, "reset_at": 1798000000 },
                "secondary_window": { "used_percent": 34, "limit_window_seconds": 604800, "reset_at": 1798500000 }
              }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(2, snapshot.QuotaWindows.Count);
        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "five_hour" && w.UsedPercent == 9);
        Assert.Contains(snapshot.QuotaWindows, w => w.Id == "seven_day" && w.UsedPercent == 34);
    }

    [Fact]
    public void ParseResponse_OnlyPrimaryWindow_SecondaryOmittedNotFaked()
    {
        var json = """
            {
              "rate_limit": {
                "primary_window": { "used_percent": 9, "limit_window_seconds": 18000, "reset_at": 1798000000 }
              }
            }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Single(snapshot.QuotaWindows);
    }

    [Theory]
    [InlineData(18_000, "five_hour")]
    [InlineData(604_800, "seven_day")]
    [InlineData(2_592_000, "30_day")]
    [InlineData(3_600, "1_hour")]
    [InlineData(86_400, "1_day")]
    public void WindowSecondsMapToExpectedTierNames(long seconds, string expectedId)
    {
        var json = $$"""
            { "rate_limit": { "primary_window": { "used_percent": 1, "limit_window_seconds": {{seconds}} } } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(expectedId, snapshot.QuotaWindows[0].Id);
    }

    [Fact]
    public void ParseResponse_ZeroRemaining_ReturnsExhausted()
    {
        var json = """
            { "rate_limit": { "primary_window": { "used_percent": 100, "limit_window_seconds": 18000, "reset_at": 1798000000 } } }
            """;

        var snapshot = CreateProvider().ParseResponse(json);

        Assert.Equal(ProviderState.Exhausted, snapshot.State);
    }

    [Fact]
    public void ParseResponse_NoRateLimitField_ReturnsProviderError()
    {
        var snapshot = CreateProvider().ParseResponse("{}");

        Assert.Equal(ProviderState.ProviderError, snapshot.State);
        Assert.Equal(ErrorCategory.ResponseFormat, snapshot.ErrorCategory);
    }

    // ---- 登录状态引导文案：区分"真的没登录"和"登录了但模式不对" ----

    [Fact]
    public async Task GetSnapshotAsync_NoAuthFile_ReturnsGenericLoginGuidance()
    {
        var reader = new CodexCredentialReader(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        var provider = new CodexQuotaProvider(new HttpClient(), reader);

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal(ProviderState.NotConfigured, snapshot.State);
        Assert.Contains("请先运行 Codex CLI 并使用 ChatGPT 账号登录", snapshot.UserGuidance);
    }

    [Fact]
    public async Task GetSnapshotAsync_ApiKeyModeLogin_ReturnsSpecificGuidance_NotGenericLoginPrompt()
    {
        // 用户已经用 Codex CLI 登录过，只是选了 API Key 模式而非 ChatGPT 订阅模式——
        // 这种情况下告诉他"请先登录"是误导，应该点明具体原因。
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{ "auth_mode": "apikey", "tokens": { "access_token": "irrelevant" } }""");
        try
        {
            var reader = new CodexCredentialReader(path);
            var provider = new CodexQuotaProvider(new HttpClient(), reader);

            var snapshot = await provider.GetSnapshotAsync();

            Assert.Equal(ProviderState.NotConfigured, snapshot.State);
            Assert.Contains("API Key", snapshot.UserGuidance);
            Assert.DoesNotContain("请先运行 Codex CLI 并使用 ChatGPT 账号登录", snapshot.UserGuidance);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
