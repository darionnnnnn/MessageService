namespace MessageService.Tests.TestSupport;

/// <summary>把指定的 handler 包成 IHttpClientFactory，測試依賴具名 HttpClient 的類別
/// （如 ApiContentWorkSource／ApiProfileStore）時不需要架 DI 容器。所有具名 client 共用
/// 同一個 handler——這兩個類別的測試只關心「發出什麼請求、怎麼解讀回應」，不關心
/// 具名註冊本身（那屬於 Program.cs，由 DeploymentModeTests 的具名 client 標頭測試涵蓋）。</summary>
public class FakeHttpClientFactory(FakeHttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> RequestedClientNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        RequestedClientNames.Add(name);
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://db-host.example/")
        };
    }
}
