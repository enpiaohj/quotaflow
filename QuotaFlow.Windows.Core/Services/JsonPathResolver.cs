using System.Globalization;
using System.Text.Json;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 自定义平台取值的 JSON 点号路径解析。语法刻意保持最小：点号属性遍历 + 数组下标
/// <c>[n]</c>（可连写），例如 <c>data.usage_percent</c>、<c>data.items[0].quota</c>、
/// <c>[0][1]</c>（根元素是数组）。不提供转义：属性名里含 <c>.</c> 或 <c>[</c> 的接口
/// 无法用本语法表达——配错路径的结果是返回 null / ProviderError，绝不臆造数据。
/// </summary>
public static class JsonPathResolver
{
    /// <summary>
    /// 按点号路径在 <paramref name="root"/> 中取值。路径为空/空白/格式非法、属性缺失、
    /// 下标越界或类型不匹配（对对象用下标、对数组用属性名）时返回 null。
    /// </summary>
    public static JsonElement? TryResolve(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = root;
        var start = 0;
        while (start <= path.Length)
        {
            var dot = path.IndexOf('.', start);
            var end = dot < 0 ? path.Length : dot;
            var segment = path[start..end];
            if (end == start)
            {
                return null; // 空段（双点/前导/尾随点）
            }

            var next = NavigateSegment(current, segment);
            if (next is null)
            {
                return null;
            }

            current = next.Value;
            if (dot < 0)
            {
                break;
            }

            start = end + 1;
        }

        return current;
    }

    /// <summary>
    /// 单个段：可选属性名前缀 + 零或多个 <c>[n]</c> 下标。段必须以属性名或 <c>[</c> 开头。
    /// </summary>
    private static JsonElement? NavigateSegment(JsonElement current, string segment)
    {
        var property = (string?)null;
        var i = 0;

        if (segment[0] != '[')
        {
            var bracket = segment.IndexOf('[');
            property = bracket < 0 ? segment : segment[..bracket];
            i = bracket < 0 ? segment.Length : bracket;
        }

        while (i < segment.Length)
        {
            if (segment[i] != '[')
            {
                return null; // 段中间出现意外字符
            }

            var close = segment.IndexOf(']', i);
            if (close < 0)
            {
                return null; // 未闭合
            }

            var inner = segment[(i + 1)..close];
            if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                return null; // 非法下标（空/非数字/负数）
            }

            // 先应用属性（若本段以属性名开头），再应用下标。
            if (property is not null)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out var child))
                {
                    return null;
                }

                current = child;
                property = null;
            }

            if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
            {
                return null;
            }

            current = current[index];
            i = close + 1;
        }

        if (property is not null)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out var child))
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    /// <summary>把元素强转成 double：Number 原样，数字字符串用 InvariantCulture 解析，其它类型 false。</summary>
    public static bool TryGetDouble(JsonElement? element, out double value)
    {
        value = 0;
        if (element is not { } el)
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>把元素强转成 decimal（余额场景用，保留精度）：规则同 <see cref="TryGetDouble"/>。</summary>
    public static bool TryGetDecimal(JsonElement? element, out decimal value)
    {
        value = 0;
        if (element is not { } el)
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>
    /// 把元素解析成 DateTimeOffset：Number 按"epoch 毫秒 vs 秒"启发式（<c>abs &gt;= 1e10</c> 视为毫秒，
    /// 否则视为秒）；字符串先试数字（epoch 数字以字符串形式给出），再试 ISO 8601/RFC3339。
    /// 取不到/非法返回 false，调用方应得到 null 而非臆造一个时间。
    /// </summary>
    public static bool TryGetDateTimeOffset(JsonElement? element, out DateTimeOffset value)
    {
        value = default;
        if (element is not { } el)
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDouble(out var n) => TryFromEpoch(n, out value),
            JsonValueKind.String when el.GetString() is { } s
                && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => TryFromEpoch(n, out value),
            JsonValueKind.String when el.GetString() is { } iso
                && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value) => true,
            _ => false,
        };
    }

    private static bool TryFromEpoch(double epoch, out DateTimeOffset value)
    {
        try
        {
            value = Math.Abs(epoch) >= 1e10
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch)
                : DateTimeOffset.FromUnixTimeSeconds((long)epoch);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            value = default;
            return false;
        }
    }
}
