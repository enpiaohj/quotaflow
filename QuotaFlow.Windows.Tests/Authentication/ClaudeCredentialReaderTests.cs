using QuotaFlow.Windows.Core.Authentication;

namespace QuotaFlow.Windows.Tests.Authentication;

// 全部使用脱敏 fixture，不含任何真实 token。
public class ClaudeCredentialReaderTests
{
    [Fact]
    public void Parse_ValidToken_ReturnsValid()
    {
        var json = """
            { "claudeAiOauth": { "accessToken": "fixture-token-not-real", "expiresAt": 9999999999 } }
            """;

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Valid, result.Status);
        Assert.Equal("fixture-token-not-real", result.AccessToken);
    }

    [Fact]
    public void Parse_LegacyKeyName_IsAlsoAccepted()
    {
        var json = """
            { "claude.ai_oauth": { "accessToken": "fixture-token-not-real", "expiresAt": 9999999999 } }
            """;

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Valid, result.Status);
    }

    [Fact]
    public void Parse_ExpiredSecondsTimestamp_ReturnsExpired()
    {
        var json = """
            { "claudeAiOauth": { "accessToken": "fixture-token-not-real", "expiresAt": 1000000000 } }
            """;

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Expired, result.Status);
        // 过期时依然把 token 一并返回，调用方可以"过期也试一把"，不能因为本地判断过期就丢弃 token。
        Assert.Equal("fixture-token-not-real", result.AccessToken);
    }

    [Fact]
    public void Parse_ExpiredMillisecondTimestamp_IsDetectedCorrectly()
    {
        // 1_700_000_000_000 是毫秒级时间戳（远早于当前时间），需要正确识别单位而不是当成秒来判断。
        var json = """
            { "claudeAiOauth": { "accessToken": "fixture-token-not-real", "expiresAt": 1700000000000 } }
            """;

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.Expired, result.Status);
    }

    [Fact]
    public void Parse_MissingOAuthEntry_ReturnsParseError()
    {
        var json = "{ \"somethingElse\": {} }";

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.ParseError, result.Status);
        Assert.Null(result.AccessToken);
    }

    [Fact]
    public void Parse_EmptyAccessToken_ReturnsParseError()
    {
        var json = """
            { "claudeAiOauth": { "accessToken": "", "expiresAt": 9999999999 } }
            """;

        var result = ClaudeCredentialReader.Parse(json);

        Assert.Equal(CredentialStatus.ParseError, result.Status);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsParseErrorWithoutThrowing()
    {
        var result = ClaudeCredentialReader.Parse("not a json");

        Assert.Equal(CredentialStatus.ParseError, result.Status);
    }

    [Fact]
    public void Read_FileDoesNotExist_ReturnsNotFound()
    {
        var reader = new ClaudeCredentialReader(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        var result = reader.Read();

        Assert.Equal(CredentialStatus.NotFound, result.Status);
        Assert.Null(result.AccessToken);
    }
}
