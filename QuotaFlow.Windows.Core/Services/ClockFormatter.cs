using System.Globalization;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 按设置里选的 <see cref="ClockDisplayFormat"/> 把当前时刻格式化为面板顶部的一行文本。
/// 周几固定用中文（周四），第几周取 ISO 8601 周号；时间部分是实时刷新的 HH:mm:ss。
/// </summary>
public static class ClockFormatter
{
    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");

    public static string Format(DateTimeOffset now, ClockDisplayFormat format)
    {
        // 用偏移量自己的"墙上时间"（DateTimeOffset.Now 即本地时间），不依赖进程所在时区，便于测试。
        var local = now.DateTime;

        return format switch
        {
            ClockDisplayFormat.Full =>
                $"{local.ToString("yyyy-MM-dd ddd", ZhCn)} · 第{ISOWeek.GetWeekOfYear(local)}周 · {local:HH:mm:ss}",
            ClockDisplayFormat.DateWeekdayTime =>
                $"{local.ToString("yyyy-MM-dd ddd", ZhCn)} · {local:HH:mm:ss}",
            ClockDisplayFormat.DateTime =>
                $"{local:yyyy-MM-dd} · {local:HH:mm:ss}",
            ClockDisplayFormat.TimeOnly =>
                $"{local:HH:mm:ss}",
            _ => $"{local:yyyy-MM-dd ddd} · {local:HH:mm:ss}",
        };
    }
}
