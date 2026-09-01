using System.Net;
using System.Net.Http;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="RetryAfterReader"/> 的测试。
///
/// 背景：被限流时"该等多久"只有服务端知道。早期实现忽略 Retry-After、一律套用本地固定冷却，
/// 服务端要求等更久时就会提前重试并再次撞上 429，用户反复看到"请求过于频繁"。
/// </summary>
public class RetryAfterReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage ResponseWith(string? retryAfterHeader)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfterHeader is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfterHeader);
        }

        return response;
    }

    [Fact]
    public void Read_DeltaSeconds_ReturnsAbsoluteInstant()
    {
        using var response = ResponseWith("120");

        Assert.Equal(Now.AddSeconds(120), RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_HttpDate_ReturnsThatInstant()
    {
        var target = Now.AddMinutes(10);
        using var response = ResponseWith(target.UtcDateTime.ToString("R"));

        Assert.Equal(target, RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_NoHeader_ReturnsNull()
    {
        using var response = ResponseWith(null);

        Assert.Null(RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_MalformedHeader_ReturnsNull()
    {
        // 解析不了就当没给，交由调用方回退到默认冷却，绝不能因此抛异常打断整次查询。
        using var response = ResponseWith("not-a-number");

        Assert.Null(RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_PastInstant_ReturnsNull()
    {
        // 已经是过去的时刻等于"现在就能重试"，没有记录价值。
        using var response = ResponseWith(Now.AddMinutes(-5).UtcDateTime.ToString("R"));

        Assert.Null(RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_ZeroSeconds_ReturnsNull()
    {
        using var response = ResponseWith("0");

        Assert.Null(RetryAfterReader.Read(response.Headers, Now));
    }

    [Fact]
    public void Read_AbsurdlyLargeValue_IsCappedSoPlatformIsNotFrozen()
    {
        // 一个异常的头不能把平台"冻"到天荒地老。
        using var response = ResponseWith("999999999");

        Assert.Equal(Now + RetryAfterReader.MaxCooldown, RetryAfterReader.Read(response.Headers, Now));
    }
}
