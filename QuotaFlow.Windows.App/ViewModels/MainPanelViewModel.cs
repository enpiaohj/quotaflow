using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 托盘面板的顶层 ViewModel：持有平台卡片（顺序由设置 PlatformOrder 决定，默认 Claude/Codex/MiniMax/DeepSeek；
/// 未配置的平台自动隐藏），负责启动时"先展示缓存、再后台刷新"，以及自动刷新的节奏控制。
/// 具体的 HTTP/凭据判断都在 Core.Providers 里，这里只做编排。
/// </summary>
public sealed partial class MainPanelViewModel : ObservableObject, IDisposable
{
    private readonly RefreshCoordinator _coordinator;
    private readonly LocalCache _cache;
    private readonly AppSettingsStore _settingsStore;
    private readonly Func<AppSettings, IEnumerable<IQuotaProvider>> _providerFactory;
    private AppSettings _settings;
    private readonly DispatcherTimer _tickTimer;
    private DispatcherTimer? _autoRefreshTimer;
    private DateTimeOffset? _lastRefreshAllAt;

    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];

    [ObservableProperty] private bool _isRefreshingAll;
    [ObservableProperty] private string _headerLastUpdatedText = string.Empty;
    [ObservableProperty] private string _clockText = string.Empty;

    /// <summary>全部平台均未配置（面板没有任何可显示的卡片）时的引导文案可见性。</summary>
    [ObservableProperty] private Visibility _emptyStateVisibility = Visibility.Collapsed;

    public IAsyncRelayCommand RefreshAllCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand ExitCommand { get; }

    /// <summary>卡片排序：直接在面板上用 ▲/▼ 调整，点击即时生效并持久化——不再经过设置页。</summary>
    public IRelayCommand MoveCardUpCommand { get; }
    public IRelayCommand MoveCardDownCommand { get; }

    /// <summary>
    /// 额度百分比显示"剩余"还是"已用"，面板顶部一键切换，立即生效并持久化。纯展示层设置——
    /// 底层数据（QuotaWindow 同时保存两个百分比）和查询/解析逻辑完全不受影响。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySemanticLabel))]
    private QuotaDisplaySemantic _displaySemantic;

    /// <summary>切换按钮上显示的文案：念的是"当前显示的是什么"，点击后切到另一个。</summary>
    public string DisplaySemanticLabel => DisplaySemantic == QuotaDisplaySemantic.Used ? "已用" : "剩余";

    public IRelayCommand ToggleDisplaySemanticCommand { get; }

    /// <summary>面板标题行产品名后的版本号（如 "v1.0.3"）。与设置页 About 同源：程序集版本，避免手工改 UI 文本造成漂移。</summary>
    public string VersionText
    {
        get
        {
            var version = typeof(MainPanelViewModel).Assembly.GetName().Version;
            return version is null ? string.Empty : $"v{version.ToString(3)}";
        }
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    public MainPanelViewModel(
        RefreshCoordinator coordinator,
        LocalCache cache,
        AppSettingsStore settingsStore,
        AppSettings initialSettings,
        Func<AppSettings, IEnumerable<IQuotaProvider>> providerFactory)
    {
        _coordinator = coordinator;
        _cache = cache;
        _settingsStore = settingsStore;
        _settings = initialSettings;
        _providerFactory = providerFactory;
        _displaySemantic = initialSettings.QuotaDisplaySemantic;

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        // 先于下面的 ApplyPlatformOrder 构造：后者在任何卡片顺序变化后都会刷新这两个命令的
        // CanExecute（首/末位禁用对应箭头），如果晚于该调用赋值，ctor 里就会先撞上空引用。
        MoveCardUpCommand = new RelayCommand<ProviderCardViewModel>(c => MoveCard(c, -1),
            c => c is not null && Cards.IndexOf(c) > 0);
        MoveCardDownCommand = new RelayCommand<ProviderCardViewModel>(c => MoveCard(c, +1),
            c => c is not null && Cards.IndexOf(c) is var i && i >= 0 && i < Cards.Count - 1);
        ToggleDisplaySemanticCommand = new RelayCommand(ToggleDisplaySemantic);

        // 初始顺序：Claude → Codex → MiniMax → DeepSeek（文档 §5.4），随后按设置里的 PlatformOrder 重排。
        foreach (var id in _coordinator.ProviderIds)
        {
            Cards.Add(CreateCard(id));
        }

        ApplyPlatformOrder(_settings.PlatformOrder);

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => TickAll();
        _tickTimer.Start();

        LoadFromCache();
        ApplyAutoRefreshInterval(_settings.AutoRefreshIntervalMinutes);
    }

    /// <summary>应用启动的最初一步：在真正发起网络请求之前，先把上次缓存渲染出来。</summary>
    public void LoadFromCache()
    {
        var cached = _cache.Load();
        var staleAfter = TimeSpan.FromMinutes(Math.Max(_settings.AutoRefreshIntervalMinutes * 3, 15));

        foreach (var card in Cards)
        {
            if (cached.TryGetValue(card.ProviderId, out var snapshot))
            {
                card.Apply(LocalCache.MarkAsCache(snapshot, staleAfter));
            }
        }

        TickAll();
    }

    /// <summary>启动时/设置里"启动时刷新"触发的首轮刷新，与自动刷新计时器共用同一套去重逻辑。</summary>
    public Task RefreshOnStartupIfEnabledAsync() =>
        _settings.RefreshOnStartup ? RefreshAllAsync() : Task.CompletedTask;

    private async Task RefreshOneAsync(string providerId)
    {
        var snapshot = await _coordinator.RefreshAsync(providerId);
        Cards.FirstOrDefault(c => c.ProviderId == providerId)?.Apply(snapshot);
        PersistCurrentSnapshots();
    }

    public async Task RefreshAllAsync()
    {
        IsRefreshingAll = true;
        try
        {
            var results = await _coordinator.RefreshAllAsync();
            foreach (var card in Cards)
            {
                if (results.TryGetValue(card.ProviderId, out var snapshot))
                {
                    card.Apply(snapshot);
                }
            }

            _lastRefreshAllAt = DateTimeOffset.UtcNow;
            PersistCurrentSnapshots();
        }
        finally
        {
            IsRefreshingAll = false;
        }
    }

    /// <summary>
    /// 设置页保存后调用：自动刷新间隔、平台顺序、接口地址覆盖、自定义平台的增删改全部立即生效，
    /// 不需要重启应用。会用新设置重建 provider 集合（<see cref="RefreshCoordinator.Rebuild"/>），
    /// 再对账卡片集合（保留仍在的平台实例、增删变化的平台）。
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _coordinator.Rebuild(_providerFactory(settings));
        ReconcileCards();
        ApplyPlatformOrder(settings.PlatformOrder);
        ApplyAutoRefreshInterval(settings.AutoRefreshIntervalMinutes);

        // 显示语义现在只由面板自己的切换按钮调整并即时持久化（同 PlatformOrder），正常情况下
        // 打开设置页时读到的就是这个值，这里同步一次纯粹是防御性收口，不依赖调用顺序。
        if (DisplaySemantic != settings.QuotaDisplaySemantic)
        {
            DisplaySemantic = settings.QuotaDisplaySemantic;
            foreach (var card in Cards)
            {
                card.UpdateDisplaySemantic(DisplaySemantic);
            }
        }

        TickAll();
    }

    /// <summary>面板顶部"剩余/已用"切换：立即生效并持久化，不触发任何网络请求。</summary>
    private void ToggleDisplaySemantic()
    {
        DisplaySemantic = DisplaySemantic == QuotaDisplaySemantic.Used
            ? QuotaDisplaySemantic.Remaining
            : QuotaDisplaySemantic.Used;

        _settings.QuotaDisplaySemantic = DisplaySemantic;
        _settingsStore.Save(_settings);

        foreach (var card in Cards)
        {
            card.UpdateDisplaySemantic(DisplaySemantic);
        }
    }

    /// <summary>新建一张卡片并套用当前的显示语义，避免新卡片在下一次 Apply() 之前短暂地用错默认值。</summary>
    private ProviderCardViewModel CreateCard(string id)
    {
        var card = new ProviderCardViewModel(id, DisplayNameFor(id), () => RefreshOneAsync(id));
        card.UpdateDisplaySemantic(DisplaySemantic);
        return card;
    }

    /// <summary>
    /// 把卡片集合与 coordinator 当前持有的平台集合对齐：删去已不存在的平台卡片（保留仍存在的
    /// 卡片实例，其缓存/状态不受影响），新增平台尾随追加，随后由 <see cref="ApplyPlatformOrder"/> 排定顺序。
    /// </summary>
    private void ReconcileCards()
    {
        var liveIds = _coordinator.ProviderIds;

        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!liveIds.Contains(Cards[i].ProviderId))
            {
                Cards.RemoveAt(i);
            }
        }

        foreach (var id in liveIds)
        {
            if (Cards.All(c => c.ProviderId != id))
            {
                Cards.Add(CreateCard(id));
            }
        }

        // ▲/▼ 的 CanExecute（首/末位禁用）在 ApplyPlatformOrder 里统一刷新——UpdateSettings 里
        // ReconcileCards 之后总会紧跟着调用它，这里不用重复刷新一次。
    }

    /// <summary>
    /// 面板卡片 ▲/▼：与相邻卡片交换位置，立即持久化到 PlatformOrder（不需要打开设置页、
    /// 不需要额外点"保存"）。<see cref="_settings"/> 是当前已知的完整设置快照，这里只改
    /// PlatformOrder 一个字段后整体落盘，其它字段维持不变。
    /// </summary>
    private void MoveCard(ProviderCardViewModel? card, int delta)
    {
        if (card is null)
        {
            return;
        }

        var index = Cards.IndexOf(card);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Cards.Count)
        {
            return;
        }

        Cards.Move(index, target);
        _settings.PlatformOrder = Cards.Select(c => c.ProviderId).ToArray();
        _settingsStore.Save(_settings);
        NotifyMoveCommandsCanExecuteChanged();
    }

    private void NotifyMoveCommandsCanExecuteChanged()
    {
        MoveCardUpCommand.NotifyCanExecuteChanged();
        MoveCardDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 按设置中的 PlatformOrder 重排卡片（从上到下）。配置里出现的平台按其指定顺序，
    /// 未出现的保持自然顺序排在末尾；null/空表示全部用自然顺序。只重排不改状态，
    /// 卡片实例及其缓存/刷新状态都不受影响。
    /// </summary>
    private void ApplyPlatformOrder(string[]? order)
    {
        if (order is { Length: > 0 })
        {
            var ordered = Cards.ToList()
                .OrderBy(c => RankOf(c.ProviderId, order)) // OrderBy 稳定：未知 id 保持自然顺序
                .ToList();

            Cards.Clear();
            foreach (var card in ordered)
            {
                Cards.Add(card);
            }
        }

        // 每次调用后 Cards 的成员/顺序都可能变了（哪怕这次没有 order 可用），
        // ▲/▼ 首末位禁用状态要跟着刷新——本方法是唯一一个覆盖了 ctor 初始化和
        // UpdateSettings 两条路径的收口点。
        NotifyMoveCommandsCanExecuteChanged();

        static int RankOf(string providerId, string[] order)
        {
            var idx = Array.IndexOf(order, providerId);
            return idx < 0 ? int.MaxValue : idx;
        }
    }

    private void ApplyAutoRefreshInterval(int minutes)
    {
        _autoRefreshTimer?.Stop();
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, minutes)) };
        _autoRefreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _autoRefreshTimer.Start();
    }

    private void PersistCurrentSnapshots()
    {
        var map = new Dictionary<string, ProviderSnapshot>();
        foreach (var card in Cards)
        {
            if (card.CurrentSnapshot is { } snapshot)
            {
                map[card.ProviderId] = snapshot;
            }
        }

        _cache.Save(map);
    }

    private void TickAll()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var card in Cards)
        {
            card.Tick(now);
        }

        HeaderLastUpdatedText = _lastRefreshAllAt is { } refreshedAt
            ? FormatRelative(now - refreshedAt)
            : string.Empty;

        // 全部未配置时显示友好引导文案（面板为空的状态，而非假装有数据）。
        EmptyStateVisibility = Cards.Any(c => c.State != ProviderState.NotConfigured)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 面板顶部实时时钟：每秒刷新，格式由设置"日期显示格式"决定（UpdateSettings 后即时生效）。
        ClockText = ClockFormatter.Format(DateTimeOffset.Now, _settings.ClockDisplayFormat);
    }

    private static string FormatRelative(TimeSpan delta) => delta switch
    {
        { } d when d.TotalMinutes < 1 => "刚刚更新",
        { } d when d.TotalHours < 1 => $"{(int)d.TotalMinutes} 分钟前更新",
        { } d when d.TotalDays < 1 => $"{(int)d.TotalHours} 小时前更新",
        _ => $"{(int)delta.TotalDays} 天前更新",
    };

    /// <summary>
    /// 平台显示名：内置四平台走映射表，自定义平台查设置的 Name。改为实例方法后，
    /// 新建自定义平台卡片时能立刻拿到正确名字，避免初始帧先闪原始 id 再被快照覆盖。
    /// 读取方一律 <c>?? []</c> 兜底，防手改配置出现 null。
    /// </summary>
    private string DisplayNameFor(string providerId) => providerId switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "minimax" => "MiniMax",
        "deepseek" => "DeepSeek",
        _ => (_settings.CustomPlatforms ?? []).FirstOrDefault(p => p.Id == providerId)?.Name ?? providerId,
    };

    public void Dispose()
    {
        _tickTimer.Stop();
        _autoRefreshTimer?.Stop();
    }
}
