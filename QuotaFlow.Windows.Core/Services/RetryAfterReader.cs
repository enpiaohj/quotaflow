using System.Net.Http.Headers;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 从 HTTP 429 响应里读取服务端要求的最早重试时刻。
///
/// 存在的意义：被限流时"该等多久"只有服务端知道。早期实现忽略 <c>Retry-After</c>，一律套用
/// 固定的本地冷却时长——服务端要求等得比这更久时，我们会提前重试并再次撞上 429，用户就会
/// 反复看到"请求过于频繁"。这里改成以服务端的指示为准，本地固定时长只作为它没给值时的兜底。
///
/// <c>Retry-After</c> 有两种合法形式（RFC 9110）：秒数（delta-seconds）或 HTTP 日期。
/// <see cref="HttpResponseHeaders.RetryAfter"/> 两种都能解析，这里统一换算成绝对时刻。
/// </summary>
public static class RetryAfterReader
{
    /// <summary>服务端给出的重试时刻明显不合理时的上限——避免一个异常的头把平台"冻"到天荒地老。</summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(1);

    /// <summary>
    /// 读取响应头里的 <c>Retry-After</c> 并换算成绝对时刻。没有该头、或值不合理时返回 null，
    /// 由调用方回退到默认冷却时长。
    /// </summary>
    public static DateTimeOffset? Read(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        DateTimeOffset target;
        if (retryAfter.Delta is { } delta)
        {
            target = now + delta;
        }
        else if (retryAfter.Date is { } date)
        {
            target = date;
        }
        else
        {
            return null;
        }

        // 已经是过去的时间等于"现在就能重试"，没有记录价值；超出上限的一律按上限截断。
        if (target <= now)
        {
            return null;
        }

        return target > now + MaxCooldown ? now + MaxCooldown : target;
    }
}
