using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

// 这些测试会真实读写本机 Windows 凭据管理器（使用独立的测试专用 key 名，
// 与 QuotaFlow 实际使用的 "minimax:ApiKey" / "deepseek:ApiKey" 完全隔离），
// 并在每个测试结束后清理，不会在本机留下残留凭据。
public class SecureCredentialStoreTests : IDisposable
{
    private readonly string _testKey = $"test:roundtrip-{Guid.NewGuid()}";
    private readonly SecureCredentialStore _store = new();

    public void Dispose() => _store.Delete(_testKey);

    [Fact]
    public void TryRead_NotYetSaved_ReturnsNull()
    {
        var result = _store.TryRead(_testKey);

        Assert.Null(result);
    }

    [Fact]
    public void SaveThenRead_RoundTripsExactValue()
    {
        const string secret = "fixture-secret-not-real-0123456789";

        _store.Save(_testKey, secret);
        var result = _store.TryRead(_testKey);

        Assert.Equal(secret, result);
    }

    [Fact]
    public void Save_Overwrite_ReplacesOldValue()
    {
        _store.Save(_testKey, "old-fixture-value");
        _store.Save(_testKey, "new-fixture-value");

        var result = _store.TryRead(_testKey);

        Assert.Equal("new-fixture-value", result);
    }

    [Fact]
    public void Delete_RemovesCredential()
    {
        _store.Save(_testKey, "fixture-value");
        _store.Delete(_testKey);

        var result = _store.TryRead(_testKey);

        Assert.Null(result);
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        var exception = Record.Exception(() => _store.Delete($"test:never-existed-{Guid.NewGuid()}"));

        Assert.Null(exception);
    }
}
