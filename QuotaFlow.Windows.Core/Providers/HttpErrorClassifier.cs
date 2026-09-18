using System.Net.Sockets;
using System.Security.Authentication;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Providers;

/// <summary>
/// 把 HttpClient 抛出的传输层异常翻译成 (State, ErrorCategory, 用户可读引导) 三元组。
/// 四个 Provider 共用这一份分类逻辑，避免每个 Provider 各写一套、互相不一致。
/// </summary>
public static class HttpErrorClassifier
{
    /// <summary>
    /// 返回当前实际使用的代理描述（如 <c>192.0.2.10:8889</c>），直连时返回 null。
    /// 由组合根在启动时注入一次。
    ///
    /// 为什么要有它：代理配错时，"网络请求失败，请检查网络连接"这句话会把人引向完全错误的
    /// 方向——用户照着去查网络，而网络本身没问题。实测遇到过：系统代理设置是关闭的、环境变量
    /// 里却残留了一个不存在的代理地址，浏览器一切正常，本应用所有平台都失败，表象上完全看不出
    /// 跟代理有关。把实际使用的代理写进提示，这类问题一眼就能看出来。
    ///
    /// 代理是进程级概念（<c>HttpClient.DefaultProxy</c> 本身也是静态的），这里用静态委托而不是
    /// 给六个 Provider 逐个加构造参数。未注入时为 null，提示文案与改动前完全一致。
    /// </summary>
    public static Func<string?>? ProxyDescriber { get; set; }

    private static string ProxySuffix()
    {
        try
        {
            var proxy = ProxyDescriber?.Invoke();
            return string.IsNullOrEmpty(proxy)
                ? string.Empty
                : $"（当前经由代理 {proxy}，若该代理不可用可在设置的「网络代理」中改为直连）";
        }
        catch (Exception)
        {
            return string.Empty; // 描述代理本身出错时不能影响错误分类
        }
    }

    public static (ProviderState State, ErrorCategory Category, string Guidance) Classify(
        Exception ex, CancellationToken callerToken)
    {
        // 调用方主动取消（例如用户切换面板/关闭应用）不是错误，不应该产出一份"失败快照"，
        // 但这属于调用方职责（应直接吞掉 OperationCanceledException），此处仅覆盖超时场景。
        if (ex is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            return (ProviderState.NetworkError, ErrorCategory.Timeout, "请求超时，请检查网络连接后重试" + ProxySuffix());
        }

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound })
            {
                return (ProviderState.NetworkError, ErrorCategory.Dns, "域名解析失败，请检查网络连接");
            }

            if (httpEx.InnerException is AuthenticationException ||
                httpEx.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                httpEx.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
            {
                return (ProviderState.NetworkError, ErrorCategory.Tls, "安全连接建立失败，请检查系统时间或网络环境");
            }

            return (ProviderState.NetworkError, ErrorCategory.Network, "网络请求失败，请检查网络连接" + ProxySuffix());
        }

        return (ProviderState.ProviderError, ErrorCategory.Unknown, "发生未知错误，请稍后重试");
    }
}
