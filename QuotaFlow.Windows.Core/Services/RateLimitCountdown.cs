namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 被限流期间"还要等多久"的实时文案。
///
/// 起因是一个真实的观感问题：限流时卡片显示「请求过于频繁」，提示写着「将在 3 分钟后自动重试」，
/// 但那句话是收到 429 的那一刻算出来的<b>静态文字</b>——七八分钟过去它还写着"3 分钟后"，
/// 同时"上次更新"的分钟数一直在涨。用户看到的是「数字在变旧、承诺没兑现」，
/// 很自然地以为程序卡住了，而实际上它只是在老老实实地等服务端放行。
///
/// <see cref="Models.ProviderSnapshot.RetryAfter"/> 存的是<b>绝对时刻</b>，
/// 所以每次界面 tick 都能重新算出准确的剩余时间。让这句话跟着走，
/// "看起来坏了"就变成"看得见它在等"。
/// </summary>
public static class RateLimitCountdown
{
    /// <summary>
    /// 距离可以重试还有多久的人话描述。已经到点、或没有服务端给的重试时刻时返回 null，
    /// 由调用方回退到不含具体时间的通用提示——绝不编一个数字出来。
    /// </summary>
    public static string? Describe(DateTimeOffset? retryAfter, DateTimeOffset now)
    {
        if (retryAfter is not { } until)
        {
            return null;
        }

        var remaining = until - now;
        if (remaining <= TimeSpan.Zero)
        {
            // 到点了但还没轮到下一次刷新：说"即将"，别显示 0 分钟或负数。
            return "即将自动重试";
        }

        if (remaining < TimeSpan.FromMinutes(1))
        {
            return $"约 {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒后自动重试";
        }

        if (remaining < TimeSpan.FromHours(1))
        {
            return $"约 {(int)Math.Ceiling(remaining.TotalMinutes)} 分钟后自动重试";
        }

        return $"约 {remaining.TotalHours:F1} 小时后自动重试";
    }

    /// <summary>限流状态下卡片要显示的完整提示。</summary>
    public static string BuildGuidance(DateTimeOffset? retryAfter, DateTimeOffset now)
    {
        var countdown = Describe(retryAfter, now);
        return countdown is null
            ? "查询过于频繁，稍后会自动重试（期间显示的是上次查到的数据）"
            : $"查询过于频繁，{countdown}（期间显示的是上次查到的数据）";
    }
}
