using System.Globalization;
using System.Windows.Data;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace QuotaFlow.Windows.App.Converters;

/// <summary>
/// 把平台 Id 映射到该平台的品牌色，用于卡片左侧的身份色条。
///
/// 品牌色只表示"这是哪个平台"，不表示状态——状态由 <see cref="StatusKindToBrushConverter"/>
/// 负责。此前四个品牌色里有三个（Claude/Codex/MiniMax）定义了却零引用，
/// 剩下的 AccentDeepSeek 被当成通用强调色借用，整套色板既没承载身份也没承载状态。
///
/// 自定义平台没有预设品牌色，回落到中性的 <c>Brush.TextTertiary</c>——不猜一个颜色，
/// 也不留空（留空会让自定义平台的卡片缺一条竖线、看起来像渲染坏了）。
/// </summary>
public sealed class ProviderIdToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "claude" => "Brush.AccentClaude",
            "codex" => "Brush.AccentCodex",
            "minimax" => "Brush.AccentMiniMax",
            "deepseek" => "Brush.AccentDeepSeek",
            "alibaba-tokenplan" => "Brush.AccentAlibaba",
            "volcengine-ark" => "Brush.AccentVolcengine",
            _ => "Brush.TextTertiary",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
