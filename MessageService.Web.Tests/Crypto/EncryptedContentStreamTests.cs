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

    private static byte[] BuildEncryptedBlob(byte[] plaintext, byte? keyId = null)
    {
        using var onDisk = new MemoryStream();
        onDisk.Write(keyId is { } id ? ChunkedBlobCipher.BuildHeader(plaintext.Length, id) : ChunkedBlobCipher.BuildHeader(plaintext.Length));

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

    // Key 陣列是 0,1,2,...,31——跟 FieldCipher.ComputeKeyId 同一份 SHA-256(前4 bytes) 邏輯
    // 手動算一次，取第一個 byte 當表頭要塞的 MSE2 key id
    private static readonly byte CorrectKeyId =
        System.Security.Cryptography.SHA256.HashData(Key)[0];

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
    public async Task GetContent_EncryptedBlob_SetsCorrectEtagAndNoStoreCacheControl()
    {
        var contentId = await SeedEncryptedContentAsync([1, 2, 3]);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        // ETag 格式是 "mc-{id}-{CompletedAt ticks}"，見 ContentStreamTests 對 Id 重用的說明
        Assert.StartsWith($"\"mc-{contentId}-", response.Headers.ETag?.Tag);
        // 加密啟用時不進瀏覽器磁碟快取——跟未加密時的 immutable+一年 max-age（見
        // ContentStreamTests.GetContent_SetsImmutableCacheControlAndETag）刻意不同，
        // 見 ContentStreamService 對 no-store 的說明
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.CacheControl?.Private ?? false);
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

    // === MSE2 key id：見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E ===

    [Fact]
    public async Task GetContent_Mse2BlobWithMatchingKeyId_DecryptsCorrectly()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "video",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Content = BuildEncryptedBlob(plaintext, CorrectKeyId),
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

    [Fact]
    public async Task GetContent_Mse2BlobWithMismatchedKeyId_Returns404_NotAttemptDecryptWithWrongKey()
    {
        // 模擬金鑰輪替後，這台主機設定的金鑰指紋跟這顆 blob 表頭記的不一樣——見
        // FieldCipher.MatchesKeyId／ContentStreamService 的說明：直接判定內容不可用，
        // 不會硬著用現在的金鑰去解（那樣要嘛 AES-GCM 認證標籤失敗炸例外，要嘛萬一 1 byte
        // key id 剛好撞到才更危險——都不該讓它發生）
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        var wrongKeyId = (byte)(CorrectKeyId + 1);
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "video",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Content = BuildEncryptedBlob(plaintext, wrongKeyId),
                    ContentType = "video/mp4",
                    CompletedAt = DateTimeOffset.UtcNow
                }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
