using System.Text.Json;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 描述一段 JSON 的<b>结构</b>：只输出字段名路径，绝不输出任何值。
///
/// 用途：非公开接口（阿里云百炼 Token Plan 走的是控制台内部接口）随时可能改字段名。
/// 改了之后原来的提示只能说一句"接口可能已变化"，等于把逆向工作原样留给下一次——
/// 实测过一次：为了搞清楚新结构，临时写了正则扫描器去抓字段名。既然这个动作注定要做，
/// 就把它固化下来，让错误提示自己带上"这次实际收到的字段有哪些"。
///
/// <b>只输出字段名</b>是硬性要求：值里可能有额度数字、账号 ID、令牌。
/// 名字足以判断接口变成了什么样，值则一律不要。
/// </summary>
public static class JsonShapeDescriber
{
    /// <summary>最多下探几层。太深的路径对判断接口变化没有额外帮助，还会让提示变得很长。</summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>最多列出多少个字段路径，避免一个大响应把提示撑爆。</summary>
    public const int DefaultMaxNames = 40;

    /// <summary>
    /// 提取 JSON 里的字段名路径（如 <c>data.DataV2.data.msg</c>）。
    /// 解析失败时返回空列表——描述结构本身绝不能再抛一次异常。
    /// </summary>
    public static IReadOnlyList<string> DescribeFieldPaths(
        string? json, int maxDepth = DefaultMaxDepth, int maxNames = DefaultMaxNames)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var paths = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, prefix: string.Empty, depth: 0, maxDepth, maxNames, paths);
        }
        catch (Exception)
        {
            return [];
        }

        return paths;
    }

    /// <summary>拼一句可直接放进用户提示的结构描述；没有可用信息时返回 null。</summary>
    public static string? Summarize(string? json, int maxDepth = DefaultMaxDepth, int maxNames = DefaultMaxNames)
    {
        var paths = DescribeFieldPaths(json, maxDepth, maxNames);
        return paths.Count == 0 ? null : string.Join("、", paths);
    }

    private static void Walk(
        JsonElement element, string prefix, int depth, int maxDepth, int maxNames, List<string> paths)
    {
        if (depth > maxDepth || paths.Count >= maxNames)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (paths.Count >= maxNames)
                    {
                        return;
                    }

                    var path = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Walk(prop.Value, path, depth + 1, maxDepth, maxNames, paths);
                    }
                    else
                    {
                        // 叶子节点只记名字，值一概不取。
                        paths.Add(path);
                    }
                }

                break;

            case JsonValueKind.Array:
                // 数组只看第一个元素的形状就够了：同一数组里的元素结构一般一致，
                // 全部展开只会产生一堆 [0]/[1]/[2] 的重复路径。
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{prefix}[]", depth + 1, maxDepth, maxNames, paths);
                    break;
                }

                break;

            default:
                if (!string.IsNullOrEmpty(prefix))
                {
                    paths.Add(prefix);
                }

                break;
        }
    }
}
