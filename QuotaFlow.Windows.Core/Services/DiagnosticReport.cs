using System.Text;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>诊断报告需要的一项平台状态（不含任何额度数值）。</summary>
public sealed class ProviderDiagnostic
{
    public required string ProviderId { get; init; }
    public required ProviderState State { get; init; }
    public ErrorCategory ErrorCategory { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>该平台配置的数据来源主机名（不含路径与查询串，避免带出 token 之类的参数）。</summary>
    public string? DataSourceHost { get; init; }
}

/// <summary>诊断报告的输入。由 App 层采集，Core 只负责组装与脱敏。</summary>
public sealed class DiagnosticInput
{
    public required string AppVersion { get; init; }
    public required string OsVersion { get; init; }
    public bool IsRemoteSession { get; init; }

    public required ProxyMode ProxyMode { get; init; }

    /// <summary>实际生效的代理地址（host:port），直连时为 null。</summary>
    public string? EffectiveProxy { get; init; }

    public int AutoRefreshIntervalMinutes { get; init; }

    /// <summary>设置文件最近一次写盘是否成功。今天的事故正是"一直失败但没人知道"。</summary>
    public bool? LastSettingsSaveSucceeded { get; init; }

    public IReadOnlyList<ProviderDiagnostic> Providers { get; init; } = [];

    /// <summary>crash.log 里最近若干条异常的类型名（不含消息与堆栈，避免带出路径或数据）。</summary>
    public IReadOnlyList<string> RecentErrorTypes { get; init; } = [];
}

/// <summary>
/// 生成可直接发给开发者的诊断报告。
///
/// 存在的理由：本项目排查问题时反复需要临时搭诊断设施——网络代理问题临时写 PowerShell 脚本、
/// 百炼接口失效临时加六个调试命令、设置页缺陷临时写 UI 自动化。全都是一次性的，下次同类问题
/// 要从零再来一遍。更糟的是有些问题从界面上根本看不出来：设置写盘连续失败了十几次，
/// 界面照常显示新值，用户毫无察觉。
///
/// <b>脱敏是硬性要求</b>：报告绝不包含 API Key、Cookie、SEC_TOKEN、额度数值。
/// 数据来源只保留主机名（不含路径与查询串）；错误只保留分类与异常类型名，不含消息与堆栈。
/// </summary>
public static class DiagnosticReport
{
    public static string Build(DiagnosticInput input, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sb = new StringBuilder();
        sb.AppendLine("QuotaFlow 诊断报告");
        sb.AppendLine("本报告已脱敏：不含 API Key、Cookie、登录态与任何额度数值，可直接发送。");
        sb.AppendLine();
        sb.AppendLine($"生成时间   : {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"应用版本   : {input.AppVersion}");
        sb.AppendLine($"系统版本   : {input.OsVersion}");
        sb.AppendLine($"远程桌面   : {(input.IsRemoteSession ? "是（RDP 会话下 DPI 与窗口尺寸可能异常）" : "否")}");
        sb.AppendLine($"刷新间隔   : {input.AutoRefreshIntervalMinutes} 分钟");
        sb.AppendLine();

        sb.AppendLine("— 网络 —");
        sb.AppendLine($"代理来源   : {DescribeProxyMode(input.ProxyMode)}");
        sb.AppendLine($"实际代理   : {input.EffectiveProxy ?? "直连"}");
        if (input.ProxyMode == ProxyMode.System && !string.IsNullOrEmpty(input.EffectiveProxy))
        {
            sb.AppendLine("             提示：跟随系统会优先读 HTTP_PROXY / HTTPS_PROXY 环境变量。");
            sb.AppendLine("             若浏览器正常而本应用全部失败，多为环境变量残留了失效代理，可改为「直连」。");
        }

        sb.AppendLine();

        sb.AppendLine("— 配置存储 —");
        sb.AppendLine($"最近写盘   : {DescribeSaveResult(input.LastSettingsSaveSucceeded)}");
        if (input.LastSettingsSaveSucceeded == false)
        {
            sb.AppendLine("             警告：设置改动未能写入磁盘，重启后会丢失。详见 crash.log。");
        }

        sb.AppendLine();

        sb.AppendLine("— 平台状态 —");
        if (input.Providers.Count == 0)
        {
            sb.AppendLine("（没有已配置的平台）");
        }
        else
        {
            foreach (var p in input.Providers)
            {
                var age = p.LastUpdatedAt is { } at
                    ? $"{(int)Math.Max(0, (now - at).TotalMinutes)} 分钟前"
                    : "从未成功";
                sb.AppendLine($"{p.ProviderId,-20} {p.State,-22} 错误分类={p.ErrorCategory,-20} 数据时间={age}");
                if (!string.IsNullOrEmpty(p.DataSourceHost))
                {
                    sb.AppendLine($"{string.Empty,-20} 数据来源主机={p.DataSourceHost}");
                }
            }
        }

        sb.AppendLine();

        sb.AppendLine("— 最近异常类型 —");
        if (input.RecentErrorTypes.Count == 0)
        {
            sb.AppendLine("（无）");
        }
        else
        {
            foreach (var group in input.RecentErrorTypes.GroupBy(t => t).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"{group.Count(),4} 次  {group.Key}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从完整 URL 中只取主机名。查询串里可能带 token 之类的参数，路径也可能暴露账号标识，
    /// 一律不保留。
    /// </summary>
    public static string? HostOnly(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string DescribeProxyMode(ProxyMode mode) => mode switch
    {
        ProxyMode.Direct => "直连（忽略系统与环境变量代理）",
        ProxyMode.Custom => "自定义",
        _ => "跟随系统",
    };

    // null 表示"本次运行没有记录到写盘失败"，而不是"确认成功过"——只挂了失败回调，
    // 没有逐个包装成功路径，措辞上不能声称超出证据的结论。
    private static string DescribeSaveResult(bool? ok) => ok switch
    {
        true => "成功",
        false => "失败（改动未落盘）",
        _ => "本次运行未记录到写盘失败",
    };
}
