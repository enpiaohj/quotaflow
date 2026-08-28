using CommunityToolkit.Mvvm.ComponentModel;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 单个额度窗口（5 小时/7 天等）的展示模型。进度条颜色只取决于这个窗口自己的剩余百分比，
/// 与卡片整体的 ProviderState 无关——文档 §5.4 的颜色分档是"每个窗口各自判断"。
///
/// v1.1.0：新增两种展示能力——
/// <list type="bullet">
/// <item><description><see cref="IsError"/>：自定义平台的某个窗口取值/换算失败时，本窗口只显示
/// "数据不可用" + 原因，不渲染进度条，绝不把占位的 0% 当成真实数据。</description></item>
/// <item><description><see cref="DisplaySemantic"/>：面板顶部可切换"已用/剩余"，只影响
/// <see cref="PercentText"/> 怎么念这个数字，底层数据和进度条填充方向不变（进度条永远按"剩余"
/// 填充，符合"油量表"的直觉——切换的只是旁边那行文字）。</description></item>
/// </list>
/// </summary>
public sealed partial class QuotaWindowViewModel : ObservableObject
{
    private readonly QuotaWindow _window;

    public QuotaWindowViewModel(QuotaWindow window, QuotaDisplaySemantic displaySemantic = QuotaDisplaySemantic.Remaining)
    {
        _window = window;
        _displaySemantic = displaySemantic;
        UpdateCountdown(DateTimeOffset.UtcNow);
    }

    public string Id => _window.Id;
    public string DisplayName => _window.DisplayName;
    public double RemainingPercent => _window.RemainingPercent;

    /// <summary>该窗口是否解析失败（自定义平台专属场景，内置四平台从不出现）。</summary>
    public bool IsError => _window.IsError;

    /// <summary>解析失败时的原因，供"数据不可用"行的 ToolTip 展示，方便定位是哪个路径配错了。</summary>
    public string? ErrorMessage => _window.ErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private QuotaDisplaySemantic _displaySemantic;

    /// <summary>按当前显示语义念出的百分比；已用 = 100 - 剩余，两者互补，底层数据不变。</summary>
    public string PercentText => DisplaySemantic == QuotaDisplaySemantic.Used
        ? $"{_window.UsedPercent:F0}%"
        : $"{_window.RemainingPercent:F0}%";

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
