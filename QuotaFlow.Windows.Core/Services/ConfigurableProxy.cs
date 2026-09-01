using System.Net;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.Core.Services;

/// <summary>
/// 按用户设置决定走不走代理的 <see cref="IWebProxy"/>。
///
/// 做成"每次请求时现查设置"而不是启动时定死：这样在设置页切换代理方式后立即生效，
/// 不需要重建 <see cref="HttpClient"/>（重建会波及所有 Provider 持有的引用，还要处理
/// 在途请求与旧 handler 的释放时机）。
///
/// 背景见 <see cref="AppSettings.ProxyMode"/>：.NET 的默认代理优先取环境变量，与浏览器
/// 只看系统设置的行为不一致，环境变量残留失效地址时表现为"浏览器正常、本应用全部网络失败"。
/// </summary>
public sealed class ConfigurableProxy : IWebProxy
{
    private readonly Func<(ProxyMode Mode, string? Address)> _readSettings;

    public ConfigurableProxy(Func<(ProxyMode Mode, string? Address)> readSettings)
    {
        _readSettings = readSettings;
        // 代理需要认证时（企业网关常见）带上当前登录用户的凭据，与浏览器行为一致；
        // 不需要认证的代理会忽略它，因此设了没有副作用。
        Credentials = CredentialCache.DefaultCredentials;
    }

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination)
    {
        var (mode, address) = _readSettings();
        return mode switch
        {
            ProxyMode.Direct => null,
            ProxyMode.Custom => TryParse(address),
            _ => SystemProxyFor(destination),
        };
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    /// <summary>
    /// 这次请求实际会走的代理，用于错误提示。返回 null 表示直连。
    /// 只暴露地址本身（不含任何凭据），可以安全写进界面提示。
    /// </summary>
    public string? DescribeFor(Uri destination)
    {
        try
        {
            var proxy = GetProxy(destination);
            return proxy is null ? null : $"{proxy.Host}:{proxy.Port}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Uri? TryParse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null; // 选了自定义却没填地址：按直连处理，总好过整个应用连不上网
        }

        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text; // 允许只填 host:port
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static Uri? SystemProxyFor(Uri destination)
    {
        try
        {
            var system = HttpClient.DefaultProxy;
            if (system.IsBypassed(destination))
            {
                return null;
            }

            var proxy = system.GetProxy(destination);
            // DefaultProxy 在"不走代理"时会把目标地址原样返回，这种情况等同直连。
            return proxy is null || proxy == destination ? null : proxy;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
