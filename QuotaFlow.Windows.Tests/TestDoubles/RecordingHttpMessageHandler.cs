using System.Net;
using System.Text;

namespace QuotaFlow.Windows.Tests.TestDoubles;

/// <summary>
/// 记录每个请求的 URI、请求头、返回预设响应体的 HttpMessageHandler 桩。
/// 用来断言 Provider 实际请求的接口地址（覆盖生效与否）与鉴权头（Bearer / 自定义请求头），
/// 不发起真实网络调用。
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    public List<Uri> RequestedUris { get; } = new();

    /// <summary>每个请求的请求头快照（键不区分大小写），与 <see cref="RequestedUris"/> 一一对应。</summary>
    public List<IReadOnlyDictionary<string, string>> RequestedHeaders { get; } = new();

    public string ResponseBody { get; set; } = "{}";

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        RequestedHeaders.Add(headers);

        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
        });
    }
}
