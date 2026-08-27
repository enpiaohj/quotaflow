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
        AppSettings initialSettings,
        Func<AppSettings, IEnumerable<IQuotaProvider>> providerFactory)
    {
        _coordinator = coordinator;
        _cache = cache;
        _settings = initialSettings;
        _providerFactory = providerFactory;

        // 初始顺序：Claude → Codex → MiniMax → DeepSeek（文档 §5.4），随后按设置里的 PlatformOrder 重排。
        foreach (var id in _coordinator.ProviderIds)
        {
            Cards.Add(new ProviderCardViewModel(id, DisplayNameFor(id), () => RefreshOneAsync(id)));
        }

        ApplyPlatformOrder(_settings.PlatformOrder);

        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));

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
        TickAll();
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
                Cards.Add(new ProviderCardViewModel(id, DisplayNameFor(id), () => RefreshOneAsync(id)));
            }
        }
    }

    /// <summary>
    /// 按设置中的 PlatformOrder 重排卡片（从上到下）。配置里出现的平台按其指定顺序，
    /// 未出现的保持自然顺序排在末尾；null/空表示全部用自然顺序。只重排不改状态，
    /// 卡片实例及其缓存/刷新状态都不受影响。
    /// </summary>
    private void ApplyPlatformOrder(string[]? order)
    {
        if (order is null || order.Length == 0)
        {
            return;
        }

        var ordered = Cards.ToList()
            .OrderBy(c => RankOf(c.ProviderId, order)) // OrderBy 稳定：未知 id 保持自然顺序
            .ToList();

        Cards.Clear();
        foreach (var card in ordered)
        {
            Cards.Add(card);
        }

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
