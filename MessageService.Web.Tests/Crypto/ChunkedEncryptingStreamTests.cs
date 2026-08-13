using MessageService.Data.Crypto;

namespace MessageService.Tests.Crypto;

public class ChunkedEncryptingStreamTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private const byte TestKeyId = 0x42;

    /// <summary>把 ChunkedEncryptingStream 的完整輸出讀出來，照格式解析回明文——
    /// 這是 ContentStreamService 讀取端邏輯的簡化版本，只用來驗證寫入端格式正確。</summary>
    private static async Task<byte[]> ReadAllAndDecryptAsync(Stream encryptingStream, long plaintextLength)
    {
        using var buffer = new MemoryStream();
        await encryptingStream.CopyToAsync(buffer);
        var onDisk = buffer.ToArray();

        Assert.Equal(ChunkedBlobCipher.ComputeEncryptedLength(plaintextLength), onDisk.LongLength);

        var header = onDisk.AsSpan(0, ChunkedBlobCipher.HeaderSize);
        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(header));
        Assert.Equal(plaintextLength, ChunkedBlobCipher.ReadPlaintextLength(header));
        Assert.Equal(ChunkedBlobCipher.ChunkSize, ChunkedBlobCipher.ReadChunkSize(header));
        Assert.Equal(TestKeyId, ChunkedBlobCipher.ReadKeyId(header));

        var result = new byte[plaintextLength];
        var resultOffset = 0;
        var (_, lastChunkIndex) = ChunkedBlobCipher.ChunksCovering(0, Math.Max(plaintextLength, 1), ChunkedBlobCipher.ChunkSize);
        if (plaintextLength == 0)
        {
            return result;
        }

        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var (offset, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(i, plaintextLength, ChunkedBlobCipher.ChunkSize);
            var encryptedChunk = onDisk.AsSpan((int)offset, length);
            var plaintextChunk = ChunkedBlobCipher.DecryptChunk(encryptedChunk, Key);
            plaintextChunk.CopyTo(result, resultOffset);
            resultOffset += plaintextChunk.Length;
        }

        return result;
    }

    [Fact]
    public async Task SmallPayload_RoundTripsExactly()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5 };
        using var source = new MemoryStream(plaintext);
        using var encrypting = new ChunkedEncryptingStream(source, plaintext.Length, Key, TestKeyId);

        var decrypted = await ReadAllAndDecryptAsync(encrypting, plaintext.Length);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task EmptyPayload_RoundTripsExactly()
    {
        using var source = new MemoryStream([]);
        using var encrypting = new ChunkedEncryptingStream(source, 0, Key, TestKeyId);

        var decrypted = await ReadAllAndDecryptAsync(encrypting, 0);

        Assert.Empty(decrypted);
    }

    [Fact]
    public async Task ExactlyOneChunk_RoundTripsExactly()
    {
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize];
        new Random(1).NextBytes(plaintext);
        using var source = new MemoryStream(plaintext);
        using var encrypting = new ChunkedEncryptingStream(source, plaintext.Length, Key, TestKeyId);

        var decrypted = await ReadAllAndDecryptAsync(encrypting, plaintext.Length);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task MultipleChunksWithPartialLastChunk_RoundTripsExactly()
    {
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize * 2 + 12345];
        new Random(2).NextBytes(plaintext);
        using var source = new MemoryStream(plaintext);
        using var encrypting = new ChunkedEncryptingStream(source, plaintext.Length, Key, TestKeyId);

        var decrypted = await ReadAllAndDecryptAsync(encrypting, plaintext.Length);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public async Task ReadWithSmallCallerBuffers_StillProducesCorrectOutput()
    {
        // 驗證串流實作對「呼叫端一次只要求少量位元組」（例如 SqlClient 內部的讀取步調）也正確——
        // 不能假設每次 ReadAsync 都會被要求一整個 chunk
        var plaintext = new byte[ChunkedBlobCipher.ChunkSize + 500];
        new Random(3).NextBytes(plaintext);
        using var source = new MemoryStream(plaintext);
        using var encrypting = new ChunkedEncryptingStream(source, plaintext.Length, Key, TestKeyId);

        using var manualBuffer = new MemoryStream();
        var smallBuffer = new byte[37]; // 刻意用一個很小、跟 chunk 大小不對齊的緩衝區
        int read;
        while ((read = await encrypting.ReadAsync(smallBuffer, 0, smallBuffer.Length)) > 0)
        {
            manualBuffer.Write(smallBuffer, 0, read);
        }

        var onDisk = manualBuffer.ToArray();
        Assert.Equal(ChunkedBlobCipher.ComputeEncryptedLength(plaintext.Length), onDisk.LongLength);

        var (_, lastChunkIndex) = ChunkedBlobCipher.ChunksCovering(0, plaintext.Length, ChunkedBlobCipher.ChunkSize);
        var result = new byte[plaintext.Length];
        var resultOffset = 0;
        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var (offset, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(i, plaintext.Length, ChunkedBlobCipher.ChunkSize);
            var plaintextChunk = ChunkedBlobCipher.DecryptChunk(onDisk.AsSpan((int)offset, length), Key);
            plaintextChunk.CopyTo(result, resultOffset);
            resultOffset += plaintextChunk.Length;
        }

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public async Task SourceStreamEndsEarly_ThrowsInsteadOfSilentlyTruncating()
    {
        // 宣稱的 plaintextLength 比來源串流實際能提供的還長——這代表呼叫端算錯 contentLength，
        // 必須讓它炸出來，不能悄悄寫入一份比宣稱短的加密內容（Range 請求之後會算錯位移量）
        var actualBytes = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(actualBytes);
        using var encrypting = new ChunkedEncryptingStream(source, 100, Key, TestKeyId); // 宣稱 100 bytes，實際只有 3

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var buffer = new MemoryStream();
            await encrypting.CopyToAsync(buffer);
        });
    }
}
