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

    // ---- 分片存储（SaveLarge/TryReadLarge/DeleteLarge）----
    // 实测事故回归：早期为绕开 2560 字节上限，靠"只挑几个域的 Cookie"精简内容，结果漏掉了
    // 用户实际登录所在域的会话 Cookie，导致查询鉴权信息不完整、反复提示"需要重新登录"。
    // 分片存储保证任意长度的内容都原样保留，不需要对内容做任何取舍。

    [Fact]
    public void SaveLarge_ValueFarExceedingSingleCredentialLimit_RoundTripsExactly()
    {
        // 6000 字符（远超单条 2560 字节上限，且刻意不是分片预算的整数倍，覆盖"最后一片不满"的情况）。
        var value = string.Join("; ", Enumerable.Range(0, 150).Select(i => $"cookie_{i}=abcdefghijklmnopqrstuvwxyz0123456789_{i}"));
        Assert.True(Encoding.UTF8.GetByteCount(value) > 2560 * 2, "测试前置：确实需要至少 3 个分片");

        try
        {
            _store.SaveLarge(_testKey, value);
            var result = _store.TryReadLarge(_testKey);

            Assert.Equal(value, result);
        }
        finally
        {
            _store.DeleteLarge(_testKey);
        }
    }

    [Fact]
    public void SaveLarge_ShortValue_StillRoundTrips()
    {
        // 短值（单分片）也要走通，不因为"不需要分片"就出问题。
        const string value = "short-fixture-value";
        try
        {
            _store.SaveLarge(_testKey, value);
            var result = _store.TryReadLarge(_testKey);

            Assert.Equal(value, result);
        }
        finally
        {
            _store.DeleteLarge(_testKey);
        }
    }

    [Fact]
    public void SaveLarge_Overwrite_WithShorterValue_DoesNotLeaveStaleTrailingChunks()
    {
        // 先存一个需要 3 片的长值，再存一个只需要 1 片的短值：如果不清理旧分片，
        // 读取时会把第 2、3 片的陈旧内容也拼接进去，读出错误的合并结果。
        var longValue = string.Join("; ", Enumerable.Range(0, 150).Select(i => $"cookie_{i}=abcdefghijklmnopqrstuvwxyz0123456789_{i}"));
        const string shortValue = "short-fixture-value";

        try
        {
            _store.SaveLarge(_testKey, longValue);
            _store.SaveLarge(_testKey, shortValue);
            var result = _store.TryReadLarge(_testKey);

            Assert.Equal(shortValue, result);
        }
        finally
        {
            _store.DeleteLarge(_testKey);
        }
    }

    [Fact]
    public void TryReadLarge_NotYetSaved_ReturnsNull()
    {
        var result = _store.TryReadLarge($"test:never-saved-{Guid.NewGuid()}");

        Assert.Null(result);
    }

    [Fact]
    public void DeleteLarge_RemovesAllChunksAndCount()
    {
        var value = string.Join("; ", Enumerable.Range(0, 150).Select(i => $"cookie_{i}=abcdefghijklmnopqrstuvwxyz0123456789_{i}"));
        _store.SaveLarge(_testKey, value);

        _store.DeleteLarge(_testKey);
        var result = _store.TryReadLarge(_testKey);

        Assert.Null(result);
    }

    [Fact]
    public void DeleteLarge_NeverSaved_DoesNotThrow()
    {
        var exception = Record.Exception(() => _store.DeleteLarge($"test:never-saved-{Guid.NewGuid()}"));

        Assert.Null(exception);
    }
}
