using System.Net;
using System.Net.Http.Headers;
using MessageService.Models;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Api;

public class ContentStreamTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<long> SeedCompletedContentAsync(byte[] bytes, string contentType = "video/mp4", string? fileName = null)
    {
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = Guid.NewGuid().ToString(),
                LineMessageId = "m1",
                GroupId = "G1",
                MessageType = fileName is null ? "video" : "file",
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Content = bytes,
                    ContentType = contentType,
                    FileName = fileName,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });
        return contentId;
    }

    [Fact]
    public async Task GetContent_NonExistentId_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/api/messages/999999/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContent_PendingStatus_Returns404()
    {
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContent_Completed_NoRange_Returns200WithFullBody()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task GetContent_FileMessage_SetsContentDispositionFileName()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var contentId = await SeedCompletedContentAsync(bytes, "application/pdf", "report.pdf");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task GetContent_WithValidRange_Returns206WithSlice()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(10, response.Content.Headers.ContentLength);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Skip(10).Take(10), body);
    }

    [Fact]
    public async Task GetContent_RangeWithoutEnd_ReturnsFromStartToEndOfFile()
    {
        var bytes = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(40, null);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Skip(40).Take(10), body);
    }

    [Fact]
    public async Task GetContent_RangeStartBeyondLength_Returns416()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(100, 200);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */3", response.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task GetContent_MalformedRangeHeader_FallsBackToFullContent()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.TryAddWithoutValidation("Range", "not-a-valid-range");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    // === XSS 白名單：image/video/audio 才給 inline，SVG 跟其他型別一律降級成 attachment+octet-stream ===

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("video/mp4")]
    [InlineData("audio/mp4")]
    public async Task GetContent_SafeContentType_ServesInlineWithOriginalContentType(string contentType)
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], contentType, "file.bin");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/pdf")]
    public async Task GetContent_UnsafeContentType_DowngradesToAttachmentOctetStream(string contentType)
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], contentType, "file.bin");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    /// <summary>白名單比對前必須先正規化。MIME type 依 RFC 2045 §5.1 大小寫不敏感，而且
    /// 收錄端的 ingest 路徑是把 Request.ContentType 原樣存下（會帶 `; charset=…` 參數）——
    /// 若直接拿資料庫原值做序數比對，大寫或帶參數的 svg 都會通過「不等於 image/svg+xml」
    /// 這關，再被 image/ 前綴規則判成安全，於是內嵌執行任意腳本（本站無登入機制，腳本可以
    /// 直接把整個對話撈出去）。</summary>
    [Theory]
    [InlineData("IMAGE/SVG+XML")]
    [InlineData("Image/Svg+Xml")]
    [InlineData("image/svg+xml; charset=utf-8")]
    [InlineData("  image/svg+xml  ")]
    [InlineData("TEXT/HTML")]
    public async Task GetContent_UnsafeContentTypeVariants_CannotBypassInlineWhitelist(string contentType)
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], contentType, "file.bin");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    /// <summary>安全型別帶大小寫／參數時仍應正常 inline，而且回應送出的是正規化後的值，
    /// 不是資料庫裡那份未經整理的上游輸入。</summary>
    [Theory]
    [InlineData("IMAGE/JPEG", "image/jpeg")]
    [InlineData("video/mp4; charset=binary", "video/mp4")]
    public async Task GetContent_SafeContentTypeVariants_AreNormalizedAndInlined(string stored, string expected)
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], stored, "file.bin");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task GetContent_AlwaysSetsNosniffHeader()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3]);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    // === RFC 5987 檔名：中文檔名要能用 filename*=UTF-8'' 正確帶出來，不是 %E6%AA%94 那種原封不動字串 ===

    [Fact]
    public async Task GetContent_NonAsciiFileName_SetsRfc5987EncodedFileNameStar()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], "application/pdf", "報告.pdf");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("報告.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
        // filename= 的 ASCII fallback 不該是原封不動的百分比編碼字串，退而求其次用副檔名兜一個
        Assert.Equal("file.pdf", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task GetContent_AsciiFileName_KeepsExactNameInBothParameters()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3], "application/pdf", "report.pdf");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    // === ETag / Cache-Control / 304：內容不可變，重複瀏覽同一段對話不用再打資料庫拉圖 ===

    [Fact]
    public async Task GetContent_SetsImmutableCacheControlAndETag()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3]);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        // 不釘死完整字串，只確認 ETag 存在且認得出是哪一筆內容——實際格式還含 CompletedAt，
        // 見下面 GetContent_ETagChangesWhenContentIsReplacedUnderSameId 的說明
        Assert.StartsWith($"\"mc-{contentId}-", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains("immutable", response.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
    }

    [Fact]
    public async Task GetContent_MatchingIfNoneMatch_Returns304WithoutBody()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3]);

        var etag = (await _fixture.Client.GetAsync($"/api/messages/{contentId}/content")).Headers.ETag?.Tag;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag!);
        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>ETag 不能只用 Id 推算：SQLite 的 Id 是 rowid 別名、沒有 AUTOINCREMENT，
    /// 保留期清除把整張表清空之後新資料會從 1 重新發號。搭配 Cache-Control: immutable
    /// 與一年 max-age，使用者的瀏覽器會把「舊的 id 1」永久快取成「新的 id 1」——看到的是
    /// 已經被刪掉的舊圖，而且連 revalidate 都不會做。把 CompletedAt 折進 ETag 才能讓
    /// 同一個 Id 的新內容拿到不同的 ETag。</summary>
    [Fact]
    public async Task GetContent_ETagChangesWhenContentIsReplacedUnderSameId()
    {
        var contentId = await SeedCompletedContentAsync([1, 2, 3]);
        var originalETag = (await _fixture.Client.GetAsync($"/api/messages/{contentId}/content")).Headers.ETag?.Tag;

        // 模擬「整張表被保留期清除清空後，同一個 Id 被重新發號給不同的內容」
        await _fixture.SeedAsync(async dbContext =>
        {
            var content = await dbContext.MessageContents.SingleAsync(c => c.Id == contentId);
            content.Content = [9, 9, 9];
            content.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await dbContext.SaveChangesAsync();
        });

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.NotEqual(originalETag, response.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task GetContent_NonMatchingIfNoneMatch_Returns200WithFullBody()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"mc-stale\"");
        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }
}
