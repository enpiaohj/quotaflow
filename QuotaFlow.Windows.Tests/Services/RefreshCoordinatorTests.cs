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

    [Fact]
    public async Task RefreshAsync_SequentialCallsAfterCompletion_EachTriggersNewRequest()
    {
        var provider = new FakeProvider("claude", TimeSpan.Zero);
        var coordinator = new RefreshCoordinator([provider]);

        await coordinator.RefreshAsync("claude");
        await coordinator.RefreshAsync("claude");

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
}
