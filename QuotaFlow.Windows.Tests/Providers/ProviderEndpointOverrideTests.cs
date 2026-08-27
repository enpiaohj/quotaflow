using QuotaFlow.Windows.Core.Authentication;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Tests.TestDoubles;

namespace QuotaFlow.Windows.Tests.Providers;

// 全部使用脱敏 fixture，不含任何真实 token/Key。
public class ProviderEndpointOverrideTests
{
    private const string ClaudeUsageJson = """
        { "five_hour": { "utilization": 10, "resets_at": "2026-08-27T18:30:00Z" } }
        """;

    private const string CodexUsageJson = """
        { "rate_limit": { "primary_window": { "used_percent": 10, "limit_window_seconds": 18000, "reset_at": 1798000000 } } }
        """;

    private const string MiniMaxJson = """
        { "base_resp": { "status_code": 0 }, "model_remains": [ { "model_name": "general", "current_interval_remaining_percent": 50 } ] }
        """;

    private const string DeepSeekJson = """
        { "is_available": true, "balance_infos": [ { "currency": "CNY", "total_balance": "100.00", "granted_balance": "0.00", "topped_up_balance": "100.00" } ] }
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EndpointResolver_NullOrWhitespace_UsesDefault(string? overrideUrl)
    {
        Assert.Equal("https://default.example", EndpointResolver.Resolve(overrideUrl, "https://default.example"));
    }

    [Fact]
    public void EndpointResolver_TrimsAndReturnsOverride()
    {
        Assert.Equal("https://x.example", EndpointResolver.Resolve("  https://x.example  ", "https://default.example"));
    }

    [Fact]
    public async Task ClaudeQuotaProvider_UsesEndpointOverride()
    {
        using var cred = new TempCredentialFile(
            """{ "claudeAiOauth": { "accessToken": "fixture-token-not-real", "expiresAt": 9999999999 } }""");
        var handler = new RecordingHttpMessageHandler { ResponseBody = ClaudeUsageJson };
        var provider = new ClaudeQuotaProvider(new HttpClient(handler),
            credentialReader: new ClaudeCredentialReader(cred.Path),
            endpointOverride: "https://claude-override.example/usage");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("https://claude-override.example/usage", handler.RequestedUris.Single().AbsoluteUri);
        Assert.Equal("https://claude-override.example/usage", snapshot.DataSource);
    }

    [Fact]
    public async Task ClaudeQuotaProvider_NoOverride_UsesDefaultUrl()
    {
        using var cred = new TempCredentialFile(
            """{ "claudeAiOauth": { "accessToken": "fixture-token-not-real", "expiresAt": 9999999999 } }""");
        var handler = new RecordingHttpMessageHandler { ResponseBody = ClaudeUsageJson };
        var provider = new ClaudeQuotaProvider(new HttpClient(handler), credentialReader: new ClaudeCredentialReader(cred.Path));

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("https://api.anthropic.com/api/oauth/usage", handler.RequestedUris.Single().AbsoluteUri);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", snapshot.DataSource);
    }

    [Fact]
    public async Task CodexQuotaProvider_UsesEndpointOverride()
    {
        var lastRefresh = DateTimeOffset.UtcNow.ToString("O");
        using var cred = new TempCredentialFile($$"""
            { "auth_mode": "chatgpt", "tokens": { "access_token": "fixture-token-not-real" }, "last_refresh": "{{lastRefresh}}" }
            """);
        var handler = new RecordingHttpMessageHandler { ResponseBody = CodexUsageJson };
        var provider = new CodexQuotaProvider(new HttpClient(handler),
            credentialReader: new CodexCredentialReader(cred.Path),
            endpointOverride: "https://codex-override.example/usage");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("https://codex-override.example/usage", handler.RequestedUris.Single().AbsoluteUri);
        Assert.Equal("https://codex-override.example/usage", snapshot.DataSource);
    }

    [Fact]
    public async Task MiniMaxQuotaProvider_UsesEndpointOverride()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = MiniMaxJson };
        var provider = new MiniMaxQuotaProvider(new HttpClient(handler), () => "fixture-key-not-real",
            endpointOverride: "https://minimax-override.example/usage");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("https://minimax-override.example/usage", handler.RequestedUris.Single().AbsoluteUri);
        Assert.Equal("https://minimax-override.example/usage", snapshot.DataSource);
    }

    [Fact]
    public async Task MiniMaxQuotaProvider_NoOverride_UsesRegionDomain()
    {
        var chinaHandler = new RecordingHttpMessageHandler { ResponseBody = MiniMaxJson };
        var china = new MiniMaxQuotaProvider(new HttpClient(chinaHandler), () => "fixture-key-not-real", MiniMaxQuotaProvider.DomainCn);
        await china.GetSnapshotAsync();
        Assert.Equal("https://api.minimaxi.com/v1/api/openplatform/coding_plan/remains", chinaHandler.RequestedUris.Single().AbsoluteUri);

        var intlHandler = new RecordingHttpMessageHandler { ResponseBody = MiniMaxJson };
        var intl = new MiniMaxQuotaProvider(new HttpClient(intlHandler), () => "fixture-key-not-real", MiniMaxQuotaProvider.DomainIntl);
        await intl.GetSnapshotAsync();
        Assert.Equal("https://api.minimax.io/v1/api/openplatform/coding_plan/remains", intlHandler.RequestedUris.Single().AbsoluteUri);
    }

    [Fact]
    public async Task DeepSeekBalanceProvider_UsesEndpointOverride()
    {
        var handler = new RecordingHttpMessageHandler { ResponseBody = DeepSeekJson };
        var provider = new DeepSeekBalanceProvider(new HttpClient(handler), () => "fixture-key-not-real",
            endpointOverride: "https://deepseek-override.example/balance");

        var snapshot = await provider.GetSnapshotAsync();

        Assert.Equal("https://deepseek-override.example/balance", handler.RequestedUris.Single().AbsoluteUri);
        Assert.Equal("https://deepseek-override.example/balance", snapshot.DataSource);
    }

    /// <summary>临时凭据 fixture 文件，用完即删。</summary>
    private sealed class TempCredentialFile : IDisposable
    {
        public string Path { get; }

        public TempCredentialFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".json");
            File.WriteAllText(Path, content);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
