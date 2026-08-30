using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 协调四个平台的刷新请求：
/// - 同一个平台如果已有一次查询在途，重复触发（比如用户连续点了好几下刷新）直接复用
///   同一个 Task，而不是再发一次网络请求——避免请求风暴。
/// - 同一个平台刚查询完（无论成功失败）的短时间内（<see cref="_minRefreshInterval"/>，
///   默认 18 秒）再次触发，直接复用上一次的快照，不真的再发网络请求——面板上能触发刷新的
///   入口很多（自动定时、手动按钮、托盘中键/双击、保存设置……），彼此互不知情，
///   短时间内叠加很容易把某个平台的接口打到限流（尤其是 Claude 的非公开用量接口）。
///   这个冷却只挡"离上次完成太近的重复请求"，不影响真正间隔够久的正常刷新。
/// - "刷新全部"并行触发各平台查询；单个平台失败/异常都被兜底成一份错误快照，
///   绝不会因为一个平台出问题而让 Task.WhenAll 整体失败、拖累其他平台的结果。
/// </summary>
public sealed class RefreshCoordinator
{
    private IReadOnlyList<IQuotaProvider> _providers;
    private readonly Dictionary<string, Task<ProviderSnapshot>> _inFlight = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCompletedAt = new();
    private readonly Dictionary<string, ProviderSnapshot> _lastSnapshot = new();
    private readonly object _gate = new();
    private readonly TimeSpan _minRefreshInterval;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="minRefreshInterval">同一平台两次真实网络请求之间的最短间隔；默认 18 秒。</param>
    /// <param name="now">当前时间的来源；单测用可控的假时钟注入，默认 <see cref="DateTimeOffset.UtcNow"/>。</param>
    public RefreshCoordinator(
        IEnumerable<IQuotaProvider> providers,
        TimeSpan? minRefreshInterval = null,
        Func<DateTimeOffset>? now = null)
    {
        _providers = providers.ToList();
        _minRefreshInterval = minRefreshInterval ?? TimeSpan.FromSeconds(18);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<string> ProviderIds => _providers.Select(p => p.ProviderId).ToList();

    /// <summary>
    /// 用新列表整体替换 provider 集合（例如设置页新增/删除自定义平台、覆盖了接口地址后）。
    /// 引用替换原子完成，列表本身不再被变异，读方不会看到撕裂状态；同时清空在途去重表——
    /// 运行中的 task 不取消，只是后续同 id 刷新会基于新定义重发（定义已变，旧结果落在
    /// 新定义卡片上随即被覆盖，可接受）。
    /// </summary>
    public void Rebuild(IEnumerable<IQuotaProvider> providers)
    {
        var list = providers.ToList();
        lock (_gate)
        {
            _providers = list;
            _inFlight.Clear();
            // 定义已经变了（比如自定义平台的接口地址被改掉），冷却期里缓存的旧快照对应的是
            // 旧定义，继续复用会把新配置该有的效果延迟到冷却期结束之后才生效，一并清空。
            _lastCompletedAt.Clear();
            _lastSnapshot.Clear();
        }
    }

    /// <summary>
    /// 刷新单个平台：已有一次刷新在途时复用同一个 Task；刚完成一次查询、还在冷却期内时
    /// 直接返回上一次的快照，不发起新的网络请求；否则真正发起一次新查询。
    /// </summary>
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

            if (_lastSnapshot.TryGetValue(providerId, out var cached) &&
                _lastCompletedAt.TryGetValue(providerId, out var completedAt) &&
                _now() - completedAt < _minRefreshInterval)
            {
                return Task.FromResult(cached);
            }

            var task = StartTrackedAsync(provider, cancellationToken);
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

    /// <summary>
    /// 启动一次查询并在其完成时按"任务身份"清理去重表。
    ///
    /// 清理必须校验身份而不是按 id 盲删：<see cref="Rebuild"/> 允许同一 id 先后存在两个
    /// 在途任务（旧任务未完成时列表被整体替换），若旧任务结束随手把新任务的去重项删掉，
    /// 后续同 id 刷新就会并发重发。用 ContinueWith 引用任务自身，只有仍是当前注册项时才移除。
    /// </summary>
    private Task<ProviderSnapshot> StartTrackedAsync(IQuotaProvider provider, CancellationToken cancellationToken)
    {
        var task = RunAndRecordAsync(provider, cancellationToken);
        _ = task.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (_inFlight.TryGetValue(provider.ProviderId, out var current) &&
                        ReferenceEquals(current, task))
                    {
                        _inFlight.Remove(provider.ProviderId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// 冷却时间戳/快照必须在这个方法返回的 Task 本身完成之前写好，不能放在另一个独立的
    /// ContinueWith 里——那样和"调用方 await 后立刻再调一次 RefreshAsync"之间会有竞态：
    /// .NET 不保证同一个 Task 的多个延续（这里的 ContinueWith 和调用方的 await）按注册顺序
    /// 执行，调用方可能先于冷却记录被写入就已经恢复运行，冷却形同虚设。写在这里，是这个方法
    /// 返回的 Task 变成 RanToCompletion 之前的最后一步，任何 await 它的人都保证已经看到写入结果。
    /// </summary>
    private async Task<ProviderSnapshot> RunAndRecordAsync(IQuotaProvider provider, CancellationToken cancellationToken)
    {
        var snapshot = await RunAsync(provider, cancellationToken);
        lock (_gate)
        {
            _lastCompletedAt[provider.ProviderId] = _now();
            _lastSnapshot[provider.ProviderId] = snapshot;
        }

        return snapshot;
    }

    private static async Task<ProviderSnapshot> RunAsync(IQuotaProvider provider, CancellationToken cancellationToken)
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
    }
}
