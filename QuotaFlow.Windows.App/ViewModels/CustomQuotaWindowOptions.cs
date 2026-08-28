using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>ComboBox 选项：额度窗口数据语义的中文标签（设置页窗口编辑器用）。</summary>
public sealed class CustomDataKindOption
{
    public CustomDataKind Value { get; }
    public string Label { get; }

    private CustomDataKindOption(CustomDataKind value, string label)
    {
        Value = value;
        Label = label;
    }

    public static readonly IReadOnlyList<CustomDataKindOption> All =
    [
        new(CustomDataKind.UtilizationPercent, "已使用百分比"),
        new(CustomDataKind.RemainingPercent, "剩余百分比"),
        new(CustomDataKind.Balance, "余额"),
        new(CustomDataKind.UsedValue, "已使用数值（需配额度上限）"),
        new(CustomDataKind.RemainingValue, "剩余数值（需配额度上限）"),
    ];
}

/// <summary>ComboBox 选项：额度窗口重置时间解析方式的中文标签。</summary>
public sealed class CustomResetTimeKindOption
{
    public CustomResetTimeKind Value { get; }
    public string Label { get; }

    private CustomResetTimeKindOption(CustomResetTimeKind value, string label)
    {
        Value = value;
        Label = label;
    }

    public static readonly IReadOnlyList<CustomResetTimeKindOption> All =
    [
        new(CustomResetTimeKind.Auto, "自动识别"),
        new(CustomResetTimeKind.Absolute, "绝对时间"),
        new(CustomResetTimeKind.RelativeSeconds, "剩余秒数"),
    ];
}
