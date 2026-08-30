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
