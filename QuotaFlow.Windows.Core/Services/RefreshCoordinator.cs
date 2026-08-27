using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 协调四个平台的刷新请求：
/// - 同一个平台如果已有一次查询在途，重复触发（比如用户连续点了好几下刷新）直接复用
///   同一个 Task，而不是再发一次网络请求——避免请求风暴。
/// - "刷新全部"并行触发各平台查询；单个平台失败/异常都被兜底成一份错误快照，
///   绝不会因为一个平台出问题而让 Task.WhenAll 整体失败、拖累其他平台的结果。
/// </summary>
public sealed class RefreshCoordinator
{
    private readonly IReadOnlyList<IQuotaProvider> _providers;
    private readonly Dictionary<string, Task<ProviderSnapshot>> _inFlight = new();
    private readonly object _gate = new();

    public RefreshCoordinator(IEnumerable<IQuotaProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IReadOnlyList<string> ProviderIds => _providers.Select(p => p.ProviderId).ToList();

    /// <summary>刷新单个平台；若已有一次刷新在途，返回同一个 Task。</summary>
    public Task<ProviderSnapshot> RefreshAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderId == providerId)
            ?? throw new ArgumentException($"未知的 provider: {providerId}", nameof(providerId));

        lock (_gate)
        {
            if (_inFlight.TryGetValue(providerId, out var existing) && !existing.IsCompleted)
            {
                return existing;
            }

            var task = RunAndTrackAsync(provider, cancellationToken);
            _inFlight[providerId] = task;
            return task;
        }
    }

    /// <summary>并行刷新全部平台。返回结果按 ProviderId 索引，四个平台互不影响彼此的成败。</summary>
    public async Task<IReadOnlyDictionary<string, ProviderSnapshot>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var providerIds = ProviderIds;
        var tasks = providerIds.Select(id => RefreshAsync(id, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        var map = new Dictionary<string, ProviderSnapshot>(providerIds.Count);
        for (var i = 0; i < providerIds.Count; i++)
        {
            map[providerIds[i]] = results[i];
        }

        return map;
    }

    private async Task<ProviderSnapshot> RunAndTrackAsync(IQuotaProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider 实现应当自行兜底、不抛未处理异常；这里是最后一道防线，
            // 保证即便某个 Provider 有缺口，也不会导致 RefreshAllAsync 整体失败。
            return new ProviderSnapshot
            {
                ProviderId = provider.ProviderId,
                DisplayName = provider.ProviderId,
                State = ProviderState.ProviderError,
                ErrorCategory = ErrorCategory.Unknown,
                UserGuidance = "查询过程中发生意外错误，请稍后重试",
            };
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(provider.ProviderId);
            }
        }
    }
}
