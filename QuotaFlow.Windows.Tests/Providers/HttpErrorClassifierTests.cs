using System.Net.Sockets;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Providers;

namespace QuotaFlow.Windows.Tests.Providers;

public class HttpErrorClassifierTests
{
    [Fact]
    public void Classify_TimeoutNotRequestedByCaller_ReturnsTimeoutCategory()
    {
        using var callerCts = new CancellationTokenSource(); // 未取消
        var ex = new OperationCanceledException();

        var (state, category, _) = HttpErrorClassifier.Classify(ex, callerCts.Token);

        Assert.Equal(ProviderState.NetworkError, state);
        Assert.Equal(ErrorCategory.Timeout, category);
    }

    [Fact]
    public void Classify_DnsFailure_ReturnsDnsCategory()
    {
        var socketEx = new SocketException((int)SocketError.HostNotFound);
        var httpEx = new HttpRequestException("dns failure", socketEx);

        var (state, category, _) = HttpErrorClassifier.Classify(httpEx, CancellationToken.None);

        Assert.Equal(ProviderState.NetworkError, state);
        Assert.Equal(ErrorCategory.Dns, category);
    }

    [Fact]
    public void Classify_GenericHttpRequestException_ReturnsNetworkCategory()
    {
        var httpEx = new HttpRequestException("connection refused");

        var (state, category, _) = HttpErrorClassifier.Classify(httpEx, CancellationToken.None);

        Assert.Equal(ProviderState.NetworkError, state);
        Assert.Equal(ErrorCategory.Network, category);
    }

    [Fact]
    public void Classify_UnknownException_ReturnsUnknownCategory()
    {
        var (state, category, _) = HttpErrorClassifier.Classify(new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal(ProviderState.ProviderError, state);
        Assert.Equal(ErrorCategory.Unknown, category);
    }

    [Fact]
    public void Classify_NeverReturnsGuidanceContainingSecretKeywords()
    {
        // 引导文案是硬编码的静态字符串，这里只是回归防线：万一以后有人往这里塞了动态内容，
        // 至少不会把 Authorization/Bearer 之类的字样带出去。
        var httpEx = new HttpRequestException("connection refused");

        var (_, _, guidance) = HttpErrorClassifier.Classify(httpEx, CancellationToken.None);

        Assert.DoesNotContain("Bearer", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", guidance, StringComparison.OrdinalIgnoreCase);
    }
}
