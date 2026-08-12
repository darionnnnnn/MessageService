using System.Net;
using System.Net.Http.Headers;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Crypto;

// ContentStreamService 讀取分塊加密 blob（見 ChunkedBlobCipher／DbContentWorkSource 寫入端）——
// 這裡直接組出「已經加密好」的位元組（模擬 DbContentWorkSource.CompleteAsync 寫進去之後
// 磁碟上長什麼樣子）塞進 MessageContents.Content，測讀取端解密與 Range 映射是否正確。
// 沒有透過 DbContentWorkSource 寫入，因為 MessageContent.Content 沒有 EF ValueConverter
// （blob 加密是手刻的，繞過 EF，見 MessageDbContext 的說明），SeedAsync 直接設 byte[]
// 屬性本來就是模擬「磁碟上的原始位元組」最直接的方式。
public class EncryptedContentStreamTests : IDisposable
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private readonly WebAppFactoryFixture _fixture = new(encryptionKey: Convert.ToBase64String(Key));

    public void Dispose() => _fixture.Dispose();

    private static byte[] BuildEncryptedBlob(byte[] plaintext)
    {
        using var onDisk = new MemoryStream();
        onDisk.Write(ChunkedBlobCipher.BuildHeader(plaintext.Length));

        var (_, lastChunkIndex) = plaintext.Length == 0
            ? (0L, -1L)
            : ChunkedBlobCipher.ChunksCovering(0, plaintext.Length, ChunkedBlobCipher.ChunkSize);

        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var chunkStart = i * ChunkedBlobCipher.ChunkSize;
            var chunkLength = (int)Math.Min(ChunkedBlobCipher.ChunkSize, plaintext.Length - chunkStart);
            var encryptedChunk = ChunkedBlobCipher.EncryptChunk(plaintext.AsSpan((int)chunkStart, chunkLength), Key);
            onDisk.Write(encryptedChunk);
        }

        return onDisk.ToArray();
    }

    private async Task<long> SeedEncryptedContentAsync(byte[] plaintext, string contentType = "video/mp4", string? fileName = null)
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
                    Content = BuildEncryptedBlob(plaintext),
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
    public async Task GetContent_EncryptedBlob_NoRange_ReturnsFullDecryptedContent()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var contentId = await SeedEncryptedContentAsync(plaintext);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(plaintext.Length, response.Content.Headers.ContentLength);
        Assert.Equal(plaintext, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetContent_EncryptedBlob_WithinSingleChunk_Returns206WithCorrectSlice()
    {
        var plaintext = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var contentId = await SeedEncryptedContentAsync(plaintext);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(100, 199);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-199/1000", response.Content.Headers.ContentRange?.ToString());
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(plaintext.Skip(100).Take(100), body);
    }

    [Fact]
    public async Task GetContent_EncryptedBlob_RangeSpanningTwoChunks_ReturnsCorrectSlice()
    {
        // 跨過 chunk 邊界的 Range 請求——驗證讀取端正確銜接兩個 chunk 解密後的明文，
        // 沒有錯位、少一個 byte 或多一個 byte
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize + 2000];
        new Random(11).NextBytes(plaintext);
        var contentId = await SeedEncryptedContentAsync(plaintext);

        var rangeStart = ChunkedBlobCipher.ChunkSize - 500;
        var rangeEnd = ChunkedBlobCipher.ChunkSize + 500;
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(rangeStart, rangeEnd);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(plaintext.Skip(rangeStart).Take(rangeEnd - rangeStart + 1), body);
    }

    [Fact]
    public async Task GetContent_EncryptedBlob_RangeExactlyAtChunkBoundary_ReturnsCorrectSlice()
    {
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize * 2];
        new Random(12).NextBytes(plaintext);
        var contentId = await SeedEncryptedContentAsync(plaintext);

        // 第二個 chunk 的第一個 byte 到最後
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(ChunkedBlobCipher.ChunkSize, null);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(plaintext.Skip(ChunkedBlobCipher.ChunkSize), body);
    }

    [Fact]
    public async Task GetContent_EncryptedBlob_FullMultiChunkDownload_RoundTripsExactly()
    {
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize * 2 + 12345];
        new Random(13).NextBytes(plaintext);
        var contentId = await SeedEncryptedContentAsync(plaintext);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(plaintext, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetContent_EncryptedFileName_DecryptsCorrectlyInContentDisposition()
    {
        var contentId = await SeedEncryptedContentAsync([1, 2, 3], "application/pdf", "報告.pdf");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("報告.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task GetContent_EncryptedBlob_SetsCorrectEtagAndCacheControl()
    {
        var contentId = await SeedEncryptedContentAsync([1, 2, 3]);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        // ETag 格式是 "mc-{id}-{CompletedAt ticks}"，見 ContentStreamTests 對 Id 重用的說明
        Assert.StartsWith($"\"mc-{contentId}-", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Fact]
    public async Task GetContent_LegacyPlaintextBlob_StillServedCorrectly_EvenWithEncryptionEnabled()
    {
        // 加密啟用前就存在的舊 blob（沒有 MSE1 表頭）——加密啟用後這筆還是要讀得到，
        // 不需要一次性轉換作業，跟文字欄位的 ENC1: 前綴是同一種設計哲學
        long contentId = 0;
        var plaintext = new byte[] { 9, 8, 7, 6, 5 };
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "video",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Content = plaintext, // 沒有表頭，直接是舊格式的明文 blob
                    ContentType = "video/mp4",
                    CompletedAt = DateTimeOffset.UtcNow
                }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(plaintext, await response.Content.ReadAsByteArrayAsync());
    }
}
