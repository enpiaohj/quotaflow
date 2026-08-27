using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 四个平台共用的查询接口。每个平台各自实现，互不感知彼此的存在——
/// 任何一个 Provider 抛异常或返回错误快照，都不应该影响其他 Provider 的调用。
/// </summary>
public interface IQuotaProvider
{
    /// <summary>平台标识，例如 "claude"。</summary>
    string ProviderId { get; }

    /// <summary>
    /// 执行一次查询并返回快照。本方法承诺不抛出未处理异常——网络/解析/鉴权错误
    /// 都会被翻译成对应 <see cref="ProviderState"/> 的快照返回给调用方。
    /// </summary>
    Task<ProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
