using CommunityToolkit.Mvvm.ComponentModel;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 单个额度窗口（5 小时/7 天等）的展示模型。进度条颜色只取决于这个窗口自己的剩余百分比，
/// 与卡片整体的 ProviderState 无关——文档 §5.4 的颜色分档是"每个窗口各自判断"。
/// </summary>
public sealed partial class QuotaWindowViewModel : ObservableObject
{
    private readonly QuotaWindow _window;

    public QuotaWindowViewModel(QuotaWindow window)
    {
        _window = window;
        UpdateCountdown(DateTimeOffset.UtcNow);
    }

    public string Id => _window.Id;
    public string DisplayName => _window.DisplayName;
    public double RemainingPercent => _window.RemainingPercent;
    public string PercentText => $"{_window.RemainingPercent:F0}%";

    /// <summary>Good（&gt;30%）/ Warn（10%~30%）/ Bad（&lt;10%），供颜色转换器查表。</summary>
    public string StatusKind => _window.RemainingPercent switch
    {
        > 30 => "Good",
        >= 10 => "Warn",
        _ => "Bad",
    };

    [ObservableProperty]
    private string _resetHintText = string.Empty;

    /// <summary>每秒由 ProviderCardViewModel 调用一次，纯本地时间计算，不触发任何网络请求。</summary>
    public void UpdateCountdown(DateTimeOffset now)
    {
        var remaining = _window.RemainingDuration(now);
        ResetHintText = remaining switch
        {
            null => string.Empty,
            { } d when d <= TimeSpan.Zero => "即将重置",
            { } d when d.TotalDays >= 1 => $"{(int)d.TotalDays}天{d.Hours}小时后重置",
            { } d when d.TotalHours >= 1 => $"{(int)d.TotalHours}小时{d.Minutes}分后重置",
            { } d => $"{Math.Max(1, (int)d.TotalMinutes)}分钟后重置",
        };
    }
}
