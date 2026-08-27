using System.Globalization;
using System.Windows.Data;

namespace QuotaFlow.Windows.App.Converters;

/// <summary>
/// 把 (Value, ActualWidth) 换算成进度条填充条的像素宽度，用来在自定义 ProgressBar 模板里
/// 画出圆角填充条——WPF 的 ColumnDefinition/Star 语法不支持直接绑定 double，所以用这个转换器。
/// </summary>
public sealed class ProgressToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is [double value, double actualWidth] && actualWidth > 0)
        {
            var clamped = Math.Clamp(value, 0, 100);
            return actualWidth * clamped / 100.0;
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
