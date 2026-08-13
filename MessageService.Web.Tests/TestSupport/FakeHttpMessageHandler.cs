namespace MessageService.Tests.TestSupport;

/// <summary>可控制回應（或直接丟例外）的 HttpMessageHandler，測試依賴 HttpClient 的類別
/// （如 HttpIngestSink）時不需要真的打網路。</summary>
public class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(responder(request));
    }
}
