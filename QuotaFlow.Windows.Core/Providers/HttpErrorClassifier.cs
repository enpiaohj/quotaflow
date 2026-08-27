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
    public static (ProviderState State, ErrorCategory Category, string Guidance) Classify(
        Exception ex, CancellationToken callerToken)
    {
        // 调用方主动取消（例如用户切换面板/关闭应用）不是错误，不应该产出一份"失败快照"，
        // 但这属于调用方职责（应直接吞掉 OperationCanceledException），此处仅覆盖超时场景。
        if (ex is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            return (ProviderState.NetworkError, ErrorCategory.Timeout, "请求超时，请检查网络连接后重试");
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

            return (ProviderState.NetworkError, ErrorCategory.Network, "网络请求失败，请检查网络连接");
        }

        return (ProviderState.ProviderError, ErrorCategory.Unknown, "发生未知错误，请稍后重试");
    }
}
