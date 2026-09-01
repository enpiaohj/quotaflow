using System.Text;
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

    [Fact]
    public void Save_Utf8_LongAsciiValue_RoundTripsWhereUtf16WouldExceedLimit()
    {
        // 实测事故回归：百炼完整会话 Cookie 很长，默认 UTF-16 编码会超出 Windows 凭据管理器
        // 单个凭据 2560 字节明文 blob 上限（CredWrite 失败，Win32 错误码 1783，表现为
        // "登录窗口消失但额度卡片不出现"）。用 UTF-8 存储后 ASCII 长值体积减半，在 UTF-16
        // 放不下的长度下必须能正常往返。
        const string key = "test:long-utf8";
        var cookie = string.Join("; ", Enumerable.Range(0, 45).Select(i => $"cookie_{i}=abcdefghijklmnopqrstuvwxyz1234567890_{i}"));
        // 前置条件：UTF-8 字节数在 1281~2559 之间（UTF-16 必然超上限，UTF-8 能放下）。
        Assert.InRange(Encoding.UTF8.GetByteCount(cookie), 1281, 2559);
        try
        {
            _store.Save(key, cookie, useUtf8: true);
            var result = _store.TryRead(key, useUtf8: true);

            Assert.Equal(cookie, result);
        }
        finally
        {
            _store.Delete(key);
        }
    }

    [Fact]
    public void Save_OverLimitEvenInUtf8_ThrowsReadableError_NotCrypticWin32Code()
    {
        // 超过 2560 字节上限时给出可读错误（含字节数），而不是让 CredWrite 的 1783 裸奔到界面。
        const string key = "test:oversized";
        var cookie = string.Join("; ", Enumerable.Range(0, 60).Select(i => $"cookie_{i}=abcdefghijklmnopqrstuvwxyz1234567890_{i}"));
        Assert.True(Encoding.UTF8.GetByteCount(cookie) > 2560, "测试前置：确实超上限");
        try
        {
            var exception = Record.Exception(() => _store.Save(key, cookie, useUtf8: true));

            Assert.NotNull(exception);
            Assert.Contains("2560", exception!.Message);
            Assert.DoesNotContain("1783", exception.Message);
        }
        finally
        {
            _store.Delete(key);
        }
    }
}
