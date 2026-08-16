using System.Net;
using System.Net.Http.Json;
using MessageService.Services;
using MessageService.Tests.TestSupport;

namespace MessageService.Tests.Services;

// ApiContentWorkSource「自己發出的請求」的形狀（路徑、方法、Content-Type、404→null 映射）——
// 這一區正是雙進程演練抓到「漏帶 X-Ingest-Key」bug 的地方：controller 端測試蓋不到這裡，
// 這個類別實際送出什麼只有它自己的測試看得到（標頭本身設定在 Program.cs 的具名 client
// 註冊上，由 DeploymentModeTests 的具名 client 測試涵蓋，這裡不重複）。
public class ApiContentWorkSourceTests
{
    private static (ApiContentWorkSource source, FakeHttpMessageHandler handler, FakeHttpClientFactory factory) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var factory = new FakeHttpClientFactory(handler);
        return (new ApiContentWorkSource(factory), handler, factory);
    }

    [Fact]
    public async Task GetPendingIdsAsync_RequestsContentWorkPath_AndParsesIds()
    {
        var (source, handler, factory) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new long[] { 3, 7 })
        });

        var ids = await source.GetPendingIdsAsync(reclaimDownloading: true, CancellationToken.None);

        Assert.Equal([3, 7], ids);
        Assert.Equal("https://db-host.example/api/ingest/content-work?reclaimDownloading=true", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("ingest", Assert.Single(factory.RequestedClientNames)); // 小型 JSON 走短 timeout 的 client
    }

    [Fact]
    public async Task GetPendingIdsAsync_PeriodicRequeue_SendsReclaimDownloadingFalse()
    {
        // 週期重掃時 Edge 端的 worker 正在跑，必須明確告訴 Core 端不要撿回 Downloading
        var (source, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Array.Empty<long>())
        });

        await source.GetPendingIdsAsync(reclaimDownloading: false, CancellationToken.None);

        Assert.EndsWith("content-work?reclaimDownloading=false", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull_InsteadOfThrowing()
    {
        // 404 是正常語意（該筆已非 Pending），對應 ContentDownloadService.ProcessAsync
        // 既有的「已被處理過就跳過」判斷，不能當錯誤拋出去變成無謂的下載重試
        var (source, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var item = await source.GetAsync(42, CancellationToken.None);

        Assert.Null(item);
    }

    [Fact]
    public async Task GetAsync_Ok_ReturnsItem()
    {
        var expected = new ContentWorkItem(42, "line-msg-42", "video");
        var (source, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        });

        var item = await source.GetAsync(42, CancellationToken.None);

        Assert.Equal(expected, item);
        Assert.Equal("https://db-host.example/api/ingest/content-work/42", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_PutsRawBodyWithContentType_OnContentClient()
    {
        byte[]? sentBody = null;
        string? sentContentType = null;
        var (source, handler, factory) = Create(request =>
        {
            sentBody = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            sentContentType = request.Content.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await source.CompleteAsync(42, new MemoryStream([1, 2, 3]), 3, "image/jpeg", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("https://db-host.example/api/ingest/content/42", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal([1, 2, 3], sentBody);
        Assert.Equal("image/jpeg", sentContentType);
        Assert.Equal("ingest-content", Assert.Single(factory.RequestedClientNames)); // blob 走長 timeout 的 client
    }

    [Fact]
    public async Task CompleteAsync_SetsContentLengthHeader_FromExplicitLength()
    {
        // 來源串流多半不支援 Seek（例如 LINE API 的回應本身），StreamContent 沒辦法自動推算，
        // 必須是明講的 contentLength 參數
        long? sentContentLength = null;
        var (source, _, _) = Create(request =>
        {
            sentContentLength = request.Content!.Headers.ContentLength;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await source.CompleteAsync(42, new MemoryStream([1, 2, 3, 4, 5]), 5, "image/jpeg", CancellationToken.None);

        Assert.Equal(5, sentContentLength);
    }

    [Fact]
    public async Task CompleteAsync_NullContentType_SendsWithoutContentTypeHeader()
    {
        string? sentContentType = "sentinel";
        var (source, _, _) = Create(request =>
        {
            sentContentType = request.Content!.Headers.ContentType?.ToString();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await source.CompleteAsync(42, new MemoryStream([1]), 1, contentType: null, CancellationToken.None);

        Assert.Null(sentContentType);
    }

    [Fact]
    public async Task FailAsync_PostsToFailedPath()
    {
        var (source, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await source.FailAsync(42, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://db-host.example/api/ingest/content/42/failed", handler.LastRequest.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task NonSuccessResponses_Throw_SoDownloadRetryLogicTakesOver(HttpStatusCode statusCode)
    {
        // 交給 ContentDownloadService 既有的 MaxRetries／Failed 狀態機處理，
        // 這裡不自己分辨可否重試（見類別註解——不疊加第二套死信機制）
        var (source, _, _) = Create(_ => new HttpResponseMessage(statusCode));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.CompleteAsync(1, new MemoryStream([1]), 1, "a/b", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.FailAsync(1, CancellationToken.None));
    }
}
