using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 桌面看板紧凑行的只读展示适配层——不复制任何 Provider 业务逻辑，只是把已经算好的
/// <see cref="ProviderCardViewModel"/>/<see cref="QuotaWindowViewModel"/> 数据重新"取景"成
/// 一行能放下的形状：挑出"主/次窗口"、把其余窗口折叠数数、把笼统的 ProviderState 翻译成
/// 点名具体窗口的异常文案。
///
/// 不持有任何独立状态、不订阅任何事件——<see cref="Refresh"/> 由 <see cref="ProviderCardViewModel"/>
/// 在 Apply()（收到新快照）和 Tick()（每秒本地倒计时刷新）两个既有更新点结束时调用一次，
/// 用一次性的 <c>OnPropertyChanged(string.Empty)</c> 让所有绑定重新取值，不需要另外维护一份
/// 与卡片状态同步的缓存字段，也就不存在"忘记同步"的风险。
/// </summary>
public sealed class CompactProviderRowViewModel : ObservableObject
{
    // Claude/Codex 都用 five_hour + seven_day；MiniMax 用 five_hour + weekly_limit。
    // 自定义平台的窗口 Id 是用户自己起的，大概率不会命中——命中不了时优先/次要窗口退化为
    // "窗口列表里的第一个/第二个"，不假装它们是"5 小时"/"7 天"（文档 §7.1 明确要求按 Id
    // 语义识别，不能按数组顺序盲目假设）。
    private static readonly string[] PrimaryWindowIds = ["five_hour"];
    private static readonly string[] SecondaryWindowIds = ["seven_day", "weekly_limit"];

    private readonly ProviderCardViewModel _card;

    public CompactProviderRowViewModel(ProviderCardViewModel card)
    {
        _card = card;
    }

    public string ProviderId => _card.ProviderId;
    public string DisplayName => _card.DisplayName;

    /// <summary>未配置的平台整行隐藏，跟标准卡片的可见性规则完全一致（同一个数据源，不重复判断）。</summary>
    public Visibility Visibility => _card.Visibility;

    public bool IsBalanceRow => _card.HasBalance;
    public bool IsWindowRow => _card.HasWindows;
    public bool IsLoading => _card.State == ProviderState.Loading;

    public string? BalanceAmountText => _card.BalanceAmountText;
    public string? BalanceCurrencyText => _card.BalanceCurrencyText;

    /// <summary>每平台单独刷新——复用标准卡片的同一个命令，不是另起一套刷新逻辑。
    /// 桌面看板常态不占位显示，只出现在行的右键菜单/Tooltip 里（文档 §10.3）。</summary>
    public IAsyncRelayCommand RefreshCommand => _card.RefreshCommand;

    public QuotaWindowViewModel? PrimaryWindow =>
        FindWindow(PrimaryWindowIds) ?? _card.Windows.FirstOrDefault();

    public bool HasPrimary => PrimaryWindow is not null;

    public QuotaWindowViewModel? SecondaryWindow
    {
        get
        {
            var secondary = FindWindow(SecondaryWindowIds);
            if (secondary is not null)
            {
                return secondary;
            }

            // 没有能识别出"次要窗口"语义的 Id：退化为"除了主窗口之外的第一个"，
            // 至少还能把第二个窗口露出来，而不是直接藏起来什么都不显示。
            var primary = PrimaryWindow;
            return _card.Windows.FirstOrDefault(w => !ReferenceEquals(w, primary));
        }
    }

    public bool HasSecondary => SecondaryWindow is not null;

    /// <summary>主/次之外的其余窗口，折叠为 "+N"（文档 §8），不无限撑高这一行。</summary>
    public IReadOnlyList<QuotaWindowViewModel> ExtraWindows
    {
        get
        {
            var primary = PrimaryWindow;
            var secondary = SecondaryWindow;
            return _card.Windows.Where(w => !ReferenceEquals(w, primary) && !ReferenceEquals(w, secondary)).ToList();
        }
    }

    public int ExtraWindowCount => ExtraWindows.Count;
    public bool HasExtraWindows => ExtraWindowCount > 0;

    /// <summary>
    /// 点名具体是哪个窗口异常，而不是笼统的"紧张"（文档 §9 的核心诉求）。多个窗口异常时取
    /// 全局最严重的那一个——不局限于主/次窗口，额外窗口如果更严重也会顶上来替换这里显示的内容
    /// （文档 §8："最严重的额外窗口如果低于阈值，可以替换状态区域"）。
    /// </summary>
    public string StatusText
    {
        get
        {
            switch (_card.State)
            {
                case ProviderState.Loading: return "加载中";
                case ProviderState.NotConfigured: return "尚未配置"; // 行本身也被隐藏，这里只是防御
                case ProviderState.AuthenticationExpired: return "需要重新登录";
                case ProviderState.Stale: return "数据可能已过期";
                case ProviderState.NetworkError: return "网络异常";
                case ProviderState.RateLimited: return "请求过于频繁";
                case ProviderState.ProviderError: return "服务异常";
            }

            if (IsBalanceRow)
            {
                return _card.State is ProviderState.Critical or ProviderState.Exhausted ? "余额告急" : string.Empty;
            }

            var worst = _card.Windows.Where(w => !w.IsError).OrderBy(w => w.RemainingPercent).FirstOrDefault();
            if (worst is null)
            {
                return string.Empty;
            }

            var label = WindowLabel(worst);
            return worst.RemainingPercent switch
            {
                <= 0 => $"{label}已用尽",
                < 10 => $"{label}额度告急",
                < 30 => $"{label}额度偏低",
                _ => string.Empty, // 正常：不显示"正常"文字，只用中性状态点表达（文档 §9）
            };
        }
    }

    /// <summary>行左侧状态点 / 状态文案的颜色——直接透传卡片已经算好的 Good/Warn/Bad/Unknown，
    /// 跟标准卡片用同一套颜色 Token（<see cref="Converters.StatusKindToBrushConverter"/>），
    /// 不重新发明一套判断规则。</summary>
    public string StatusKind => _card.StatusKind;

    /// <summary>悬停/点击时展开的详情：列出这个平台全部窗口的名称、百分比、重置倒计时——
    /// 既是 §8 "+N" 的详情浮层，也是 §9 "Tooltip 列出全部异常" 的信息来源，两处共用同一份文本，
    /// 不必分别维护两套摘要逻辑。</summary>
    public string DetailsTooltip
    {
        get
        {
            if (!IsWindowRow)
            {
                return string.Empty;
            }

            var lines = _card.Windows.Select(w =>
            {
                if (w.IsError)
                {
                    return $"{w.DisplayName}：数据不可用";
                }

                var reset = string.IsNullOrEmpty(w.ResetHintText) ? string.Empty : $"（{w.ResetHintText}）";
                return $"{w.DisplayName}：{w.PercentText}{reset}";
            });

            return string.Join("\n", lines);
        }
    }

    private QuotaWindowViewModel? FindWindow(string[] ids) =>
        _card.Windows.FirstOrDefault(w => ids.Contains(w.Id, StringComparer.OrdinalIgnoreCase));

    private static string WindowLabel(QuotaWindowViewModel window) =>
        PrimaryWindowIds.Contains(window.Id, StringComparer.OrdinalIgnoreCase) ? "5 小时"
        : SecondaryWindowIds.Contains(window.Id, StringComparer.OrdinalIgnoreCase) ? "周"
        : window.DisplayName;

    /// <summary>由 <see cref="ProviderCardViewModel.Apply"/> / <see cref="ProviderCardViewModel.Tick"/>
    /// 在更新完自身状态后调用一次。空字符串让 WPF 把绑定到本对象的所有属性都重新拉取一遍——
    /// 本类没有可变字段，"重新算一遍" 本身就是最简单也不会跟卡片状态失配的刷新方式。</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
