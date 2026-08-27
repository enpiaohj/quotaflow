using System.Globalization;
using System.Windows.Data;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Colors = System.Windows.Media.Colors;

namespace QuotaFlow.Windows.App.Converters;

/// <summary>
/// 把 "Good"/"Warn"/"Bad"/"Unknown" 状态字符串映射到当前主题下的状态色 DynamicResource。
/// 用查资源而不是硬编码颜色，这样浅色/深色切换时会自动跟着换。
/// </summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Good" => "Brush.StatusGood",
            "Warn" => "Brush.StatusWarn",
            "Bad" => "Brush.StatusBad",
            _ => "Brush.StatusUnknown",
        };

        return Application.Current.TryFindResource(key) as Brush
               ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
