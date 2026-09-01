using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 遇到"临时性失败"时，保留上一次查到的真实数据继续展示，而不是把卡片清空。
///
/// 背景：被限流（HTTP 429）时返回的快照不含任何额度窗口，卡片按它渲染就会把原本好好的
/// 额度数字整片抹掉、只剩一行报错——但那份数据几分钟前才刚拿到，并没有失效，配额本身也没变。
/// 用户的直接观感就是"额度突然没了"。
///
/// 这里保留的是<b>真实拿到过的数据</b>，并且不动 <see cref="ProviderSnapshot.LastUpdatedAt"/>，
/// 所以界面上的"x 分钟前更新"仍然如实反映数据的实际时间，同时 State/UserGuidance 采用本次的
/// 失败信息，用户能同时看到"数据是什么"和"为什么没刷新成功"。绝不是把旧数据伪装成新数据，
/// 更不会在没有数据时编造 0%。
/// </summary>
public static class TransientFailureMerger
{
    /// <summary>
    /// 决定这次实际要展示的快照。
    ///
    /// 只在"本次是限流失败、且本次没带任何数据、且上一次确实有数据"这三个条件同时成立时
    /// 才做保留；其余情况一律原样使用本次快照——包括认证过期、配置错误这类需要用户处理的
    /// 问题，那些必须立刻如实暴露，不能被旧数据盖住。
    /// </summary>
    public static ProviderSnapshot Merge(ProviderSnapshot? previous, ProviderSnapshot incoming)
    {
        if (incoming.State != ProviderState.RateLimited)
        {
            return incoming;
        }

        if (incoming.QuotaWindows.Count > 0 || incoming.Balance is not null)
        {
            return incoming;
        }

        if (previous is null || (previous.QuotaWindows.Count == 0 && previous.Balance is null))
        {
            return incoming;
        }

        return incoming with
        {
            QuotaWindows = previous.QuotaWindows,
            Balance = previous.Balance,
            LastUpdatedAt = previous.LastUpdatedAt,
        };
    }
}
