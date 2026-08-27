using System.Net;
using System.Text;

namespace QuotaFlow.Windows.Tests.TestDoubles;

/// <summary>
/// 记录每个请求的 URI、返回预设响应体的 HttpMessageHandler 桩。
/// 用来断言 Provider 实际请求的接口地址（覆盖生效与否），不发起真实网络调用。
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    public List<Uri> RequestedUris { get; } = new();

    public string ResponseBody { get; set; } = "{}";

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
        });
    }
}
