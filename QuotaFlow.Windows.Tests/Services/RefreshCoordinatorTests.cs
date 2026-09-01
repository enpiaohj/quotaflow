using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

public class RefreshCoordinatorTests
{
    /// <summary>可控的假 Provider：每次调用递增计数，并可插入延迟以模拟"请求进行中"。</summary>
    private sealed class FakeProvider(string id, TimeSpan delay) : IQuotaProvider
    {
        public int CallCount;
        public string ProviderId => id;

        public async Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new ProviderSnapshot
            {
                ProviderId = id,
                DisplayName = id,
                State = ProviderState.Available,
            };
        }
    }

    /// <summary>每次调用都返回"请求过于频繁"的假 Provider，用于验证限流后的延长冷却。</summary>
    private sealed class RateLimitedProvider(string id) : IQuotaProvider
    {
        public int CallCount;
        public string ProviderId => id;

        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new ProviderSnapshot
            {
                ProviderId = id,
                DisplayName = id,
                State = ProviderState.RateLimited,
            });
        }
    }

    /// <summary>限流并附带服务端 Retry-After（绝对时刻）的 provider。</summary>
    private sealed class RateLimitedWithRetryAfterProvider(string id, Func<DateTimeOffset> retryAfter) : IQuotaProvider
    {
        public int CallCount;
        public string ProviderId => id;

        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new ProviderSnapshot
            {
                ProviderId = id,
                DisplayName = id,
                State = ProviderState.RateLimited,
                RetryAfter = retryAfter(),
            });
        }
    }

    private sealed class ThrowingProvider(string id) : IQuotaProvider
    {
        public string ProviderId => id;

        public Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("模拟一个未被 Provider 自身捕获的异常");
    }

    /// <summary>可手动拨动的假时钟，让冷却时间相关的测试不必真的等待墙钟时间、也不会因为
    /// 测试环境抖动而偶发失败。</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
    }

    [Fact]
    public async Task RefreshAsync_ConcurrentCalls_DedupeIntoOneRequest()
    {
        // 模拟"用户连续点了好几下刷新"：并发发起 5 次刷新，底层查询只应该真正执行一次。
        var provider = new FakeProvider("claude", TimeSpan.FromMilliseconds(200));
        var coordinator = new RefreshCoordinator([provider]);

        var tasks = Enumerable.Range(0, 5).Select(_ => coordinator.RefreshAsync("claude")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, provider.CallCount);
    }

    // ---- 冷却时间（防止面板上多个刷新入口——自动定时/手动按钮/托盘中键双击/保存设置——
    //      短时间内叠加，把某个平台的接口打到限流，尤其是 Claude 的非公开用量接口）----

    [Fact]
    public async Task RefreshAsync_SequentialCallsWithinCooldown_ReusesLastSnapshotWithoutNewRequest()
    {
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator([provider], minRefreshInterval: TimeSpan.FromSeconds(18), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        await coordinator.RefreshAsync("claude"); // 冷却期内，时钟没有前进

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_AfterCooldownElapses_TriggersNewRequest()
    {
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator([provider], minRefreshInterval: TimeSpan.FromSeconds(18), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        clock.Now = clock.Now.AddSeconds(19); // 冷却期已过
        await coordinator.RefreshAsync("claude");

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_CooldownIsPerProvider_DoesNotAffectOtherProviders()
    {
        var claude = new FakeProvider("claude", TimeSpan.Zero);
        var codex = new FakeProvider("codex", TimeSpan.Zero);
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator([claude, codex], minRefreshInterval: TimeSpan.FromSeconds(18), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        await coordinator.RefreshAsync("codex"); // 不同平台，不受 claude 冷却影响

        Assert.Equal(1, claude.CallCount);
        Assert.Equal(1, codex.CallCount);
    }

    // ---- 限流冷却以服务端 Retry-After 为准（"Claude 偶尔提示请求过于频繁"的三个根因） ----

    [Fact]
    public async Task RateLimited_ServerRetryAfterLongerThanDefault_ServerValueWins()
    {
        // 服务端说要等 30 分钟，本地默认冷却只有 3 分钟。若仍按本地值，3 分钟后就会再打一次
        // 并再次撞上 429——这正是用户反复看到"请求过于频繁"的直接原因。
        var clock = new FakeClock();
        var provider = new RateLimitedWithRetryAfterProvider("claude", () => clock.Now.AddMinutes(30));
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18),
            rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");

        clock.Now = clock.Now.Add(TimeSpan.FromMinutes(10)); // 已过本地默认冷却，但远未到服务端要求
        await coordinator.RefreshAsync("claude");
        Assert.Equal(1, provider.CallCount);

        clock.Now = clock.Now.Add(TimeSpan.FromMinutes(21)); // 越过服务端要求的时刻
        await coordinator.RefreshAsync("claude");
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RateLimited_ServerRetryAfterShorterThanDefault_LocalFloorStillApplies()
    {
        // 服务端给了个比本地兜底还短的值时不能变得更激进，取两者中更长的那个。
        var clock = new FakeClock();
        var provider = new RateLimitedWithRetryAfterProvider("claude", () => clock.Now.AddSeconds(5));
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18),
            rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        clock.Now = clock.Now.Add(TimeSpan.FromSeconds(30));
        await coordinator.RefreshAsync("claude");

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Rebuild_PreservesActiveRateLimitCooldown()
    {
        // 限流是远端配额的状态，跟本地 provider 定义无关，Rebuild 清掉它并不能让服务端提前放行。
        // 而保存设置会 Rebuild + 立即 RefreshAll，清空等于每次保存设置都强行绕过限流冷却。
        var clock = new FakeClock();
        var provider = new RateLimitedWithRetryAfterProvider("claude", () => clock.Now.AddMinutes(30));
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18),
            rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        Assert.Equal(1, provider.CallCount);

        coordinator.Rebuild([provider]);
        clock.Now = clock.Now.Add(TimeSpan.FromMinutes(1));
        await coordinator.RefreshAsync("claude");

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SeedFromCache_ActiveRateLimit_BlocksStartupRefresh()
    {
        // 冷却此前只在内存里，重启即清零，而"启动时立即刷新"默认开启——每次重启都会立刻
        // 再打一次接口，哪怕几秒前才刚被限流。
        var clock = new FakeClock();
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18),
            rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        coordinator.SeedFromCache(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new()
            {
                ProviderId = "claude",
                DisplayName = "Claude",
                State = ProviderState.RateLimited,
                RetryAfter = clock.Now.AddMinutes(20),
            },
        });

        await coordinator.RefreshAsync("claude");
        Assert.Equal(0, provider.CallCount);

        clock.Now = clock.Now.Add(TimeSpan.FromMinutes(21));
        await coordinator.RefreshAsync("claude");
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task SeedFromCache_ExpiredRateLimit_DoesNotBlockStartupRefresh()
    {
        // 缓存里的限流时刻已经过去时不能继续拦——那等于把平台永久冻住。
        var clock = new FakeClock();
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var coordinator = new RefreshCoordinator([provider], now: () => clock.Now);

        coordinator.SeedFromCache(new Dictionary<string, ProviderSnapshot>
        {
            ["claude"] = new()
            {
                ProviderId = "claude",
                DisplayName = "Claude",
                State = ProviderState.RateLimited,
                RetryAfter = clock.Now.AddMinutes(-1),
            },
        });

        await coordinator.RefreshAsync("claude");

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Rebuild_ClearsCooldownCache_NewDefinitionNotServedFromOldSnapshot()
    {
        var oldProvider = new FakeProvider("custom-1", TimeSpan.Zero);
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator([oldProvider], minRefreshInterval: TimeSpan.FromSeconds(18), now: () => clock.Now);
        await coordinator.RefreshAsync("custom-1");

        // 接口地址等定义变了，重建后即使冷却时间没到，也不应该复用旧定义查出来的快照。
        var newProvider = new FakeProvider("custom-1", TimeSpan.Zero);
        coordinator.Rebuild([newProvider]);
        await coordinator.RefreshAsync("custom-1");

        Assert.Equal(1, oldProvider.CallCount);
        Assert.Equal(1, newProvider.CallCount);
    }

    // ---- 限流后延长冷却（普通 18 秒冷却挡不住"下一轮 5 分钟自动刷新照样撞上同一个限流窗口"，
    //      需要单独一个更长的冷却，参见 Claude 非公开用量接口偶发限流的实际问题） ----

    [Fact]
    public async Task RefreshAsync_LastResultWasRateLimited_UsesLongerCooldown_NormalIntervalNotEnough()
    {
        var provider = new RateLimitedProvider("claude");
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18), rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        clock.Now = clock.Now.AddSeconds(19); // 已经超过普通的 18 秒冷却……

        await coordinator.RefreshAsync("claude");

        // ……但上一次结果是限流，这次仍然应该复用缓存，而不是又发一次请求。
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_AfterRateLimitedCooldownElapses_TriggersNewRequest()
    {
        var provider = new RateLimitedProvider("claude");
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18), rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude");
        clock.Now = clock.Now.AddMinutes(4); // 超过限流冷却

        await coordinator.RefreshAsync("claude");

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_SuccessAfterRateLimited_RevertsToNormalCooldown()
    {
        // 限流冷却只应该在"上一次结果确实是限流"时生效；一旦成功查询过一次，
        // 冷却要立刻恢复正常的短间隔，不能一直被上上次的限流状态拖长。
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var clock = new FakeClock();
        var coordinator = new RefreshCoordinator(
            [provider], minRefreshInterval: TimeSpan.FromSeconds(18), rateLimitedCooldown: TimeSpan.FromMinutes(3), now: () => clock.Now);

        await coordinator.RefreshAsync("claude"); // 成功，走普通冷却
        clock.Now = clock.Now.AddSeconds(19);
        await coordinator.RefreshAsync("claude"); // 普通冷却已过，应该真的再查一次

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RefreshAllAsync_OneProviderThrows_OthersStillSucceed()
    {
        var healthy = new FakeProvider("claude", TimeSpan.Zero);
        var broken = new ThrowingProvider("codex");
        var coordinator = new RefreshCoordinator([healthy, broken]);

        var results = await coordinator.RefreshAllAsync();

        Assert.Equal(ProviderState.Available, results["claude"].State);
        Assert.Equal(ProviderState.ProviderError, results["codex"].State);
    }

    [Fact]
    public async Task RefreshAllAsync_RunsProvidersInParallelNotSequentially()
    {
        var a = new FakeProvider("claude", TimeSpan.FromMilliseconds(150));
        var b = new FakeProvider("codex", TimeSpan.FromMilliseconds(150));
        var coordinator = new RefreshCoordinator([a, b]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await coordinator.RefreshAllAsync();
        sw.Stop();

        // 串行会 >= 300ms；并行应明显少于两者之和。留足余量避免测试环境抖动导致误判。
        Assert.True(sw.ElapsedMilliseconds < 300, $"耗时 {sw.ElapsedMilliseconds}ms，看起来是串行执行的");
    }

    // ---- Rebuild（设置页增删/修改平台后整体替换）----

    [Fact]
    public async Task Rebuild_ReplacesProviderSet_RefreshAllUsesNewSet()
    {
        var oldProvider = new FakeProvider("custom-1", TimeSpan.Zero);
        var newProvider = new FakeProvider("custom-2", TimeSpan.Zero);
        var coordinator = new RefreshCoordinator([oldProvider]);

        coordinator.Rebuild([newProvider]);

        Assert.Equal(["custom-2"], coordinator.ProviderIds);
        var results = await coordinator.RefreshAllAsync();
        Assert.True(results.ContainsKey("custom-2"));
        Assert.False(results.ContainsKey("custom-1"));
        Assert.Equal(0, oldProvider.CallCount);
        Assert.Equal(1, newProvider.CallCount);
    }

    [Fact]
    public async Task Rebuild_ThenRefreshRemovedId_Throws()
    {
        var coordinator = new RefreshCoordinator([new FakeProvider("custom-1", TimeSpan.Zero)]);

        coordinator.Rebuild([new FakeProvider("custom-2", TimeSpan.Zero)]);

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RefreshAsync("custom-1"));
    }

    [Fact]
    public async Task Rebuild_ClearsInFlight_SameIdRetriesWithNewDefinition()
    {
        // 定义已变：在途的同 id 请求结束后，下一次刷新必须基于新集合重发，而不是复用旧去重项。
        var slow = new FakeProvider("custom-1", TimeSpan.FromMilliseconds(150));
        var coordinator = new RefreshCoordinator([slow]);

        var inFlight = coordinator.RefreshAsync("custom-1");

        // Rebuild 后 in-flight 去重表被清空；新 provider 同 id 刷新应触发一次新请求。
        var fast = new FakeProvider("custom-1", TimeSpan.Zero);
        coordinator.Rebuild([fast]);

        var second = coordinator.RefreshAsync("custom-1");

        await Task.WhenAll(inFlight, second);
        Assert.Equal(1, slow.CallCount);
        Assert.Equal(1, fast.CallCount);
    }
}
