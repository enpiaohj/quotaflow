using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="JsonShapeDescriber"/> 的测试。
///
/// 用途是让"接口已变化"这类错误提示自带线索：阿里云百炼走的是控制台内部接口（非公开），
/// 字段名随时可能改。只说一句"接口可能已变化"等于把逆向工作留给下一次——实测过一次，
/// 为搞清新结构临时写了正则扫描器抓字段名。
///
/// <b>只输出字段名、绝不输出值</b>是硬性要求，这里当作首要断言来守：
/// 值里可能有额度数字、账号 ID、令牌。
/// </summary>
public class JsonShapeDescriberTests
{
    /// <summary>百炼用量接口的真实响应结构（数值为构造值）。</summary>
    private const string RealShape = """
        {
          "code": "200",
          "data": {
            "DataV2": {
              "ret": ["SUCCESS::接口调用成功"],
              "data": {
                "msg": "Success.",
                "code": "SUCCESS",
                "data": { "per1WeekResetTime": 1788829080000, "per1WeekPercentage": 0.4696 },
                "success": true
              }
            }
          },
          "requestId": "abc-123"
        }
        """;

    [Fact]
    public void DescribeFieldPaths_FindsNestedFieldNames()
    {
        var paths = JsonShapeDescriber.DescribeFieldPaths(RealShape);

        Assert.Contains("data.DataV2.data.data.per1WeekPercentage", paths);
        Assert.Contains("data.DataV2.data.msg", paths);
        Assert.Contains("code", paths);
    }

    [Fact]
    public void DescribeFieldPaths_NeverContainsValues()
    {
        // 这是整个设计成立的前提：字段名可以外发，值不行。
        var joined = string.Join("|", JsonShapeDescriber.DescribeFieldPaths(RealShape));

        Assert.DoesNotContain("1788829080000", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("0.4696", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("abc-123", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("Success", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFieldPaths_ArrayIsDescribedOnceNotPerElement()
    {
        // 同一数组里的元素结构一般一致，全展开只会产生一堆重复路径。
        var paths = JsonShapeDescriber.DescribeFieldPaths(
            """{ "items": [ {"a": 1}, {"a": 2}, {"a": 3} ] }""");

        Assert.Equal(["items[].a"], paths);
    }

    [Fact]
    public void DescribeFieldPaths_RespectsDepthLimit()
    {
        var deep = """{"l1":{"l2":{"l3":{"l4":{"l5":"x"}}}}}""";

        var shallow = JsonShapeDescriber.DescribeFieldPaths(deep, maxDepth: 2);

        Assert.DoesNotContain(shallow, p => p.Contains("l5", StringComparison.Ordinal));
    }

    [Fact]
    public void DescribeFieldPaths_RespectsNameLimit()
    {
        var many = "{" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"f{i}\":1")) + "}";

        var paths = JsonShapeDescriber.DescribeFieldPaths(many, maxNames: 5);

        Assert.Equal(5, paths.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    public void DescribeFieldPaths_BadInput_ReturnsEmptyNeverThrows(string? bad)
    {
        // 描述结构是错误处理路径上的一环，它自己再抛异常就本末倒置了。
        Assert.Empty(JsonShapeDescriber.DescribeFieldPaths(bad));
    }

    [Fact]
    public void Summarize_NoUsableShape_ReturnsNull()
    {
        Assert.Null(JsonShapeDescriber.Summarize("not json"));
    }

    [Fact]
    public void Summarize_JoinsPathsReadably()
    {
        var text = JsonShapeDescriber.Summarize("""{"a":1,"b":{"c":2}}""");

        Assert.Equal("a、b.c", text);
    }
}
