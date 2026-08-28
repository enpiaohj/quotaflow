using QuotaFlow.Windows.Core.Authentication;

namespace QuotaFlow.Windows.Tests.Authentication;

// 全部使用脱敏 fixture，不含任何真实 token。
public class CodexCredentialReaderTests
{
    [Fact]
    public void Parse_ChatGptOAuthMode_ReturnsValid()
    {
        var lastRefresh = DateTimeOffset.UtcNow.ToString("O");
        var json = $$"""
            {
              "auth_mode": "chatgpt",
              "tokens": { "access_token": "fixture-token-not-real", "account_id": "fixture-account" },
              "last_refresh": "{{lastRefresh}}"
            }
            """;

        var result = CodexCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Valid, result.Status);
        Assert.Equal("fixture-token-not-real", result.AccessToken);
        Assert.Equal("fixture-account", result.AccountId);
    }

    [Fact]
    public void Parse_ApiKeyMode_ReturnsNotFound()
    {
        // API Key 模式没有对应的订阅用量体系，不应该被当成可查询的凭据。
        var json = """
            { "auth_mode": "apikey", "tokens": { "access_token": "irrelevant" } }
            """;

        var result = CodexCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.AccessToken);
        // Message 要点出具体原因（API Key 模式），而不是空——上层要用它替换掉"请登录"这种
        // 对已经登录、只是模式不对的用户会造成误导的通用文案。
        Assert.NotNull(result.Message);
        Assert.Contains("API Key", result.Message);
    }

    [Fact]
    public void Parse_StaleRefresh_ReturnsExpiredButKeepsToken()
    {
        var lastRefresh = DateTimeOffset.UtcNow.AddDays(-9).ToString("O");
        var json = $$"""
            {
              "auth_mode": "chatgpt",
              "tokens": { "access_token": "fixture-token-not-real", "account_id": "fixture-account" },
              "last_refresh": "{{lastRefresh}}"
            }
            """;

        var result = CodexCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Expired, result.Status);
        Assert.Equal("fixture-token-not-real", result.AccessToken);
    }

    [Fact]
    public void Parse_RecentRefresh_ReturnsValid()
    {
        var lastRefresh = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var json = $$"""
            {
              "auth_mode": "chatgpt",
              "tokens": { "access_token": "fixture-token-not-real" },
              "last_refresh": "{{lastRefresh}}"
            }
            """;

        var result = CodexCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Valid, result.Status);
    }

    [Fact]
    public void Parse_MissingTokens_ReturnsParseError()
    {
        var json = """{ "auth_mode": "chatgpt" }""";

        var result = CodexCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.ParseError, result.Status);
    }

    [Fact]
    public void Read_FileDoesNotExist_ReturnsNotFound()
    {
        var reader = new CodexCredentialReader(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        var result = reader.Read();

        Assert.Equal(CredentialStatus.NotFound, result.Status);
    }
}
