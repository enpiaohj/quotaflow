using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 托盘面板的顶层 ViewModel：持有四张平台卡片（固定顺序 Claude/Codex/MiniMax/DeepSeek），
/// 负责启动时"先展示缓存、再后台刷新"，以及自动刷新的节奏控制。
/// 具体的 HTTP/凭据判断都在 Core.Providers 里，这里只做编排。
/// </summary>
public sealed partial class MainPanelViewModel : ObservableObject, IDisposable
{
    private readonly RefreshCoordinator _coordinator;
    private readonly LocalCache _cache;
    private AppSettings _settings;
    private readonly DispatcherTimer _tickTimer;
    private DispatcherTimer? _autoRefreshTimer;
    private DateTimeOffset? _lastRefreshAllAt;

    public ObservableCollection<ProviderCardViewModel> Cards { get; } = [];

    [ObservableProperty] private bool _isRefreshingAll;
    [ObservableProperty] private string _headerLastUpdatedText = string.Empty;
    [ObservableProperty] private string _clockText = string.Empty;

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

    public MainPanelViewModel(RefreshCoordinator coordinator, LocalCache cache, AppSettings initialSettings)
    {
        _coordinator = coordinator;
        _cache = cache;
        _settings = initialSettings;

        // 固定展示顺序：Claude → Codex → MiniMax → DeepSeek（文档 §5.4）。
        foreach (var id in _coordinator.ProviderIds)
        {
            Cards.Add(new ProviderCardViewModel(id, DisplayNameFor(id), () => RefreshOneAsync(id)));
        }

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

    /// <summary>设置页保存后调用：自动刷新间隔立即生效，不需要重启应用。</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        ApplyAutoRefreshInterval(settings.AutoRefreshIntervalMinutes);
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

    private static string DisplayNameFor(string providerId) => providerId switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "minimax" => "MiniMax",
        "deepseek" => "DeepSeek",
        _ => providerId,
    };

    public void Dispose()
    {
        _tickTimer.Stop();
        _autoRefreshTimer?.Stop();
    }
}
