using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 一张平台卡片的展示模型。只做"把 ProviderSnapshot 翻译成 UI 友好的字符串/状态"，
/// 不包含任何 HTTP/凭据判断逻辑——那些都在 Core.Providers 里，这里只是绑定层。
/// </summary>
public sealed partial class ProviderCardViewModel : ObservableObject
{
    private readonly Func<Task> _refreshCallback;
    private ProviderSnapshot? _lastSnapshot;
    private QuotaDisplaySemantic _displaySemantic = QuotaDisplaySemantic.Remaining;

    public string ProviderId { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Visibility))]
    private ProviderState _state = ProviderState.Loading;
    [ObservableProperty] private string _stateText = "加载中";
    [ObservableProperty] private string _statusKind = "Unknown";
    [ObservableProperty] private string? _userGuidance;
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _hasBalance;
    [ObservableProperty] private string? _balanceAmountText;
    [ObservableProperty] private string? _balanceCurrencyText;
    [ObservableProperty] private string? _balanceDetailText;

    public ObservableCollection<QuotaWindowViewModel> Windows { get; } = [];

    public string? DataSource => _lastSnapshot?.DataSource;

    /// <summary>
    /// 未配置（NotConfigured）的平台在面板上隐藏——没有数据可展示就不占空间，也避免误读成"0%/0.00"。
    /// 其它状态（网络异常、需要重新登录等）仍然显示，让用户知道该平台出问题了。
    /// </summary>
    public Visibility Visibility => State == ProviderState.NotConfigured ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 是否有额度窗口要显示。v1.1.0 起与 <see cref="HasBalance"/> 不再互斥——一个自定义平台
    /// 理论上可以同时有额度窗口和一个余额窗口，两段各自按自己的可见性显示，不再用同一个
    /// 布尔值反向驱动对方（面板 XAML 里两段可见性各自独立绑定 HasBalance / HasWindows）。
    /// </summary>
    public bool HasWindows => Windows.Count > 0;

    /// <summary>供 MainPanelViewModel 落地缓存使用；ProviderSnapshot 本身不含任何密钥。</summary>
    public ProviderSnapshot? CurrentSnapshot => _lastSnapshot;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand ToggleExpandCommand { get; }

    public ProviderCardViewModel(string providerId, string displayName, Func<Task> refreshCallback)
    {
        ProviderId = providerId;
        _displayName = displayName;
        _refreshCallback = refreshCallback;
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    private async Task ExecuteRefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await _refreshCallback();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void Apply(ProviderSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        DisplayName = snapshot.DisplayName;
        State = snapshot.State;
        StateText = ToStateText(snapshot.State);
        StatusKind = ToStatusKind(snapshot.State);
        UserGuidance = snapshot.UserGuidance;

        Windows.Clear();
        foreach (var window in snapshot.QuotaWindows)
        {
            Windows.Add(new QuotaWindowViewModel(window, _displaySemantic));
        }

        OnPropertyChanged(nameof(HasWindows));

        HasBalance = snapshot.Balance is not null;
        if (snapshot.Balance is { } balance)
        {
            BalanceAmountText = balance.Amount.ToString("0.00");
            BalanceCurrencyText = balance.Currency;
            BalanceDetailText = BuildBalanceDetail(balance);
        }
        else
        {
            BalanceAmountText = null;
            BalanceCurrencyText = null;
            BalanceDetailText = null;
        }

        OnPropertyChanged(nameof(DataSource));
        UpdateLastUpdatedText(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 面板顶部切换"已用/剩余"时调用：纯展示层更新，立即生效，不触发任何网络请求或重新解析。
    /// </summary>
    public void UpdateDisplaySemantic(QuotaDisplaySemantic semantic)
    {
        _displaySemantic = semantic;
        foreach (var window in Windows)
        {
            window.DisplaySemantic = semantic;
        }
    }

    /// <summary>每秒调用一次：只做本地倒计时/相对时间文案刷新，不触发任何网络请求。</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var window in Windows)
        {
            window.UpdateCountdown(now);
        }

        UpdateLastUpdatedText(now);
    }

    private void UpdateLastUpdatedText(DateTimeOffset now)
    {
        if (_lastSnapshot?.LastUpdatedAt is not { } updatedAt)
        {
            LastUpdatedText = string.Empty;
            return;
        }

        var delta = now - updatedAt;
        var prefix = _lastSnapshot.IsFromCache ? "（缓存）" : string.Empty;
        LastUpdatedText = delta switch
        {
            { } d when d.TotalMinutes < 1 => $"刚刚更新{prefix}",
            { } d when d.TotalHours < 1 => $"{(int)d.TotalMinutes} 分钟前更新{prefix}",
            { } d when d.TotalDays < 1 => $"{(int)d.TotalHours} 小时前更新{prefix}",
            _ => $"{(int)delta.TotalDays} 天前更新{prefix}",
        };
    }

    private static string BuildBalanceDetail(BalanceMetric balance)
    {
        var parts = new List<string>();
        if (balance.GrantedAmount is { } granted)
        {
            parts.Add($"赠送 {granted:0.00}");
        }

        if (balance.ToppedUpAmount is { } toppedUp)
        {
            parts.Add($"充值 {toppedUp:0.00}");
        }

        return string.Join(" · ", parts);
    }

    private static string ToStateText(ProviderState state) => state switch
    {
        ProviderState.Loading => "加载中",
        ProviderState.Available => "正常",
        ProviderState.Low => "偏低",
        ProviderState.Critical => "紧张",
        ProviderState.Exhausted => "已用尽",
        ProviderState.NotConfigured => "未配置",
        ProviderState.AuthenticationExpired => "需要重新登录",
        ProviderState.NetworkError => "网络异常",
        ProviderState.RateLimited => "请求过于频繁",
        ProviderState.ProviderError => "服务异常",
        ProviderState.Stale => "数据可能已过期",
        _ => "未知",
    };

    private static string ToStatusKind(ProviderState state) => state switch
    {
        ProviderState.Available => "Good",
        ProviderState.Low => "Warn",
        ProviderState.Critical => "Bad",
        ProviderState.Exhausted => "Bad",
        ProviderState.RateLimited => "Warn",
        ProviderState.AuthenticationExpired => "Bad",
        ProviderState.ProviderError => "Bad",
        _ => "Unknown",
    };
}
