using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.Tests.Services;

/// <summary>
/// <see cref="ConfigurableProxy"/> 的测试。
///
/// 背景：.NET 的默认代理优先读 HTTP_PROXY / HTTPS_PROXY 环境变量，浏览器却只看系统代理设置。
/// 实测遇到过一台机器系统代理是关闭的、环境变量里却残留着一个已不存在的代理地址，结果浏览器
/// 一切正常，本应用所有平台都报"网络请求失败"，且表象上完全看不出跟代理有关。
/// </summary>
public class ConfigurableProxyTests
{
    private static readonly Uri Destination = new("https://api.deepseek.com/user/balance");

    [Fact]
    public void Direct_IgnoresAllProxyConfiguration()
    {
        var proxy = new ConfigurableProxy(() => (ProxyMode.Direct, "http://10.0.0.1:8888"));

        Assert.Null(proxy.GetProxy(Destination));
        Assert.True(proxy.IsBypassed(Destination));
        Assert.Null(proxy.DescribeFor(Destination));
    }

    [Fact]
    public void Custom_UsesConfiguredAddress()
    {
        var proxy = new ConfigurableProxy(() => (ProxyMode.Custom, "http://10.0.0.1:8888"));

        Assert.Equal(new Uri("http://10.0.0.1:8888"), proxy.GetProxy(Destination));
        Assert.Equal("10.0.0.1:8888", proxy.DescribeFor(Destination));
    }

    [Fact]
    public void Custom_HostPortWithoutScheme_IsAccepted()
    {
        // 用户多半会直接粘 "127.0.0.1:7890"，不该因为少个 http:// 就整个失效。
        var proxy = new ConfigurableProxy(() => (ProxyMode.Custom, "127.0.0.1:7890"));

        Assert.Equal(new Uri("http://127.0.0.1:7890"), proxy.GetProxy(Destination));
    }

    [Fact]
    public void Custom_WithoutAddress_FallsBackToDirect()
    {
        // 选了自定义却没填地址：按直连处理，总好过让整个应用连不上网。
        var proxy = new ConfigurableProxy(() => (ProxyMode.Custom, null));

        Assert.Null(proxy.GetProxy(Destination));
    }

    [Fact]
    public void Custom_MalformedAddress_FallsBackToDirect()
    {
        var proxy = new ConfigurableProxy(() => (ProxyMode.Custom, "::::not a uri::::"));

        Assert.Null(proxy.GetProxy(Destination));
    }

    [Fact]
    public void SettingsAreReadPerRequest_SoSwitchingTakesEffectImmediately()
    {
        // 关键行为：切换代理方式后不需要重建 HttpClient。
        var mode = ProxyMode.Custom;
        var proxy = new ConfigurableProxy(() => (mode, "http://10.0.0.1:8888"));

        Assert.NotNull(proxy.GetProxy(Destination));

        mode = ProxyMode.Direct;

        Assert.Null(proxy.GetProxy(Destination));
    }

    [Fact]
    public void DescribeFor_NeverLeaksCredentials()
    {
        // 描述文案会显示在界面上，只能包含地址本身。
        var proxy = new ConfigurableProxy(() => (ProxyMode.Custom, "http://user:secret@10.0.0.1:8888"));

        var described = proxy.DescribeFor(Destination);

        Assert.Equal("10.0.0.1:8888", described);
        Assert.DoesNotContain("secret", described, StringComparison.Ordinal);
        Assert.DoesNotContain("user", described, StringComparison.Ordinal);
    }
}
