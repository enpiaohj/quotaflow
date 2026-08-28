using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 设置页里一个自定义平台内部、单个额度窗口的可编辑行。比 <see cref="CustomPlatformRow"/> 简单——
/// 不涉及密钥，只是字段编辑 + 折叠/展开/删除。新增窗口默认展开，已保存的窗口默认收起并显示摘要
/// （<see cref="SummaryText"/>），呼应文档"新增默认展开、已有默认收起"的要求。
/// </summary>
public sealed partial class CustomQuotaWindowRow : ObservableObject
{
    private readonly Action<CustomQuotaWindowRow> _onDelete;

    /// <summary>窗口稳定标识（如 <c>window-1</c>），新增时分配，之后不变。</summary>
    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LimitVisibility))]
    [NotifyPropertyChangedFor(nameof(UnitLabel))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private CustomDataKind _dataKind = CustomDataKind.UtilizationPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string _valuePath = string.Empty;

    [ObservableProperty] private string? _resetsAtPath;
    [ObservableProperty] private CustomResetTimeKind _resetTimeKind = CustomResetTimeKind.Auto;
    [ObservableProperty] private string? _limitPath;

    /// <summary>固定额度上限的文本编辑态；保存时按 InvariantCulture 解析，留空即未填。</summary>
    [ObservableProperty] private string _fixedLimitText = string.Empty;

    [ObservableProperty] private string? _unit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderVisibility))]
    [NotifyPropertyChangedFor(nameof(EditorVisibility))]
    private bool _isExpanded;

    /// <summary>只有"已使用数值/剩余数值"两种语义才需要额度上限。</summary>
    public Visibility LimitVisibility =>
        DataKind is CustomDataKind.UsedValue or CustomDataKind.RemainingValue ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>单位输入框的标签：余额语义下是"币种"，数值语义下是"单位"，百分比语义下不显示。</summary>
    public string UnitLabel => DataKind == CustomDataKind.Balance ? "币种" : "单位";

    public Visibility UnitVisibility =>
        DataKind is CustomDataKind.Balance or CustomDataKind.UsedValue or CustomDataKind.RemainingValue
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>收起态只显示名称输入框 + 展开/删除。</summary>
    public Visibility HeaderVisibility => IsExpanded ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>展开态显示完整编辑区。</summary>
    public Visibility EditorVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>收起态摘要文案，例如 "已使用百分比 · usage.rolling.percent"，不展开也能确认配的是啥。</summary>
    public string SummaryText
    {
        get
        {
            var kindLabel = DataKind switch
            {
                CustomDataKind.UtilizationPercent => "已使用百分比",
                CustomDataKind.RemainingPercent => "剩余百分比",
                CustomDataKind.Balance => "余额",
                CustomDataKind.UsedValue => "已使用数值",
                CustomDataKind.RemainingValue => "剩余数值",
                _ => "未知语义",
            };

            var path = string.IsNullOrWhiteSpace(ValuePath) ? "（未填取值路径）" : ValuePath;
            return $"{kindLabel} · {path}";
        }
    }

    public IRelayCommand ToggleExpandCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    /// <param name="id">固定标识 window-{n}，新增时分配，之后不改变。</param>
    /// <param name="onDelete">点击删除时回调（负责从父行的窗口列表移除）。</param>
    /// <param name="existing">已保存的窗口定义；null 表示新建。</param>
    public CustomQuotaWindowRow(string id, Action<CustomQuotaWindowRow> onDelete, CustomQuotaWindowSettings? existing = null)
    {
        Id = id;
        _onDelete = onDelete;

        _name = existing?.Name ?? string.Empty;
        _dataKind = existing?.DataKind ?? CustomDataKind.UtilizationPercent;
        _valuePath = existing?.ValuePath ?? string.Empty;
        _resetsAtPath = existing?.ResetsAtPath;
        _resetTimeKind = existing?.ResetTimeKind ?? CustomResetTimeKind.Auto;
        _limitPath = existing?.LimitPath;
        _fixedLimitText = existing?.FixedLimit is { } limit ? limit.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _unit = existing?.Unit;

        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        DeleteCommand = new RelayCommand(() => onDelete(this));
    }

    /// <summary>把当前编辑态打包成窗口定义（<paramref name="sortOrder"/> 由父行按列表顺序赋值）。</summary>
    public CustomQuotaWindowSettings ToSettings(int sortOrder) => new()
    {
        Id = Id,
        Name = Name.Trim(),
        DataKind = DataKind,
        ValuePath = ValuePath.Trim(),
        ResetsAtPath = string.IsNullOrWhiteSpace(ResetsAtPath) ? null : ResetsAtPath.Trim(),
        ResetTimeKind = ResetTimeKind,
        LimitPath = string.IsNullOrWhiteSpace(LimitPath) ? null : LimitPath.Trim(),
        FixedLimit = double.TryParse(FixedLimitText, NumberStyles.Any, CultureInfo.InvariantCulture, out var fixedLimit)
            ? fixedLimit
            : null,
        Unit = string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim(),
        SortOrder = sortOrder,
    };
}
