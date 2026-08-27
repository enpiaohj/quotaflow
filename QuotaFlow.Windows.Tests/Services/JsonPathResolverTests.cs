using System.Text.Json;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

public class JsonPathResolverTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private const string Nested = """
        {
          "data": {
            "usage_percent": 42.5,
            "quota_left": "0.618",
            "items": [
              { "name": "a", "quota": 80 },
              { "name": "b", "quota": 15 }
            ],
            "resets_at_ms": 1798000000000,
            "resets_at_s": 1798000000,
            "resets_at_iso": "2026-10-22T22:00:00Z",
            "resets_at_invalid": "not-a-time"
          }
        }
        """;

    // ---- 路径解析 ----

    [Fact]
    public void TryResolve_DottedPath_ResolvesValue()
    {
        var root = Parse(Nested);

        var value = JsonPathResolver.TryResolve(root, "data.usage_percent");

        Assert.True(value.HasValue);
        Assert.Equal(JsonValueKind.Number, value.Value.ValueKind);
        Assert.Equal(42.5, value.Value.GetDouble());
    }

    [Fact]
    public void TryResolve_ArrayIndex_ResolvesItem()
    {
        var root = Parse(Nested);

        var value = JsonPathResolver.TryResolve(root, "data.items[1].quota");

        Assert.True(value.HasValue);
        Assert.Equal(15, value.Value.GetInt32());
    }

    [Fact]
    public void TryResolve_RootIsArray_ResolvesBareIndex()
    {
        var root = Parse("""[ { "q": 10 }, { "q": 20 } ]""");

        var value = JsonPathResolver.TryResolve(root, "[1].q");

        Assert.True(value.HasValue);
        Assert.Equal(20, value.Value.GetInt32());
    }

    [Fact]
    public void TryResolve_ChainedIndices_Resolves()
    {
        var root = Parse("""[ [1, 2], [3, 4] ]""");

        var value = JsonPathResolver.TryResolve(root, "[1][0]");

        Assert.True(value.HasValue);
        Assert.Equal(3, value.Value.GetInt32());
    }

    [Fact]
    public void TryResolve_MissingProperty_ReturnsNull()
    {
        var root = Parse(Nested);

        var value = JsonPathResolver.TryResolve(root, "data.nonexistent");

        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_IndexOutOfRange_ReturnsNull()
    {
        var root = Parse(Nested);

        var value = JsonPathResolver.TryResolve(root, "data.items[9]");

        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_TypeMismatch_ObjectIndex_ReturnsNull()
    {
        var root = Parse(Nested);

        // 对对象用下标：data 不是数组。
        var value = JsonPathResolver.TryResolve(root, "data[0]");

        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_TypeMismatch_ArrayProperty_ReturnsNull()
    {
        var root = Parse(Nested);

        // 对数组用属性名：items 是数组不是对象。
        var value = JsonPathResolver.TryResolve(root, "data.items.quota");

        Assert.Null(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("data..usage")]
    [InlineData(".data")]
    [InlineData("data.")]
    [InlineData("data[abc]")]
    [InlineData("data[]")]
    [InlineData("data[-1]")]
    [InlineData("data[0")]
    [InlineData("data[0]x")]
    public void TryResolve_InvalidPaths_ReturnNull(string path)
    {
        var root = Parse(Nested);

        var value = JsonPathResolver.TryResolve(root, path);

        Assert.Null(value);
    }

    // ---- 数字强转 ----

    [Fact]
    public void TryGetDouble_Number_Works()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.usage_percent");

        Assert.True(JsonPathResolver.TryGetDouble(el, out var value));
        Assert.Equal(42.5, value);
    }

    [Fact]
    public void TryGetDouble_NumericString_Works()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.quota_left");

        Assert.True(JsonPathResolver.TryGetDouble(el, out var value));
        Assert.Equal(0.618, value, 3);
    }

    [Fact]
    public void TryGetDouble_NonNumericString_Fails()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.resets_at_iso");

        Assert.False(JsonPathResolver.TryGetDouble(el, out _));
    }

    [Fact]
    public void TryGetDouble_NullElement_Fails()
    {
        Assert.False(JsonPathResolver.TryGetDouble(null, out _));
    }

    [Fact]
    public void TryGetDecimal_NumericString_PreservesPrecision()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.quota_left");

        Assert.True(JsonPathResolver.TryGetDecimal(el, out var value));
        Assert.Equal(0.618m, value);
    }

    // ---- 重置时间解析 ----

    [Fact]
    public void TryGetDateTimeOffset_EpochMilliseconds_Works()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.resets_at_ms");

        Assert.True(JsonPathResolver.TryGetDateTimeOffset(el, out var value));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798000000000), value);
    }

    [Fact]
    public void TryGetDateTimeOffset_EpochSeconds_Works()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.resets_at_s");

        Assert.True(JsonPathResolver.TryGetDateTimeOffset(el, out var value));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1798000000), value);
    }

    [Fact]
    public void TryGetDateTimeOffset_IsoString_Works()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.resets_at_iso");

        Assert.True(JsonPathResolver.TryGetDateTimeOffset(el, out var value));
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T22:00:00Z"), value);
    }

    [Fact]
    public void TryGetDateTimeOffset_InvalidString_Fails()
    {
        var root = Parse(Nested);
        var el = JsonPathResolver.TryResolve(root, "data.resets_at_invalid");

        Assert.False(JsonPathResolver.TryGetDateTimeOffset(el, out _));
    }

    [Fact]
    public void TryGetDateTimeOffset_EpochString_Works()
    {
        // 有些接口把 epoch 数字以字符串形式返回。
        var root = Parse("""{ "t": "1798000000000" }""");
        var el = JsonPathResolver.TryResolve(root, "t");

        Assert.True(JsonPathResolver.TryGetDateTimeOffset(el, out var value));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1798000000000), value);
    }
}
