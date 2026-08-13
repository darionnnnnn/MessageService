using MessageService.Data.Crypto;

namespace MessageService.Tests.Crypto;

public class ChunkedBlobCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Theory]
    [InlineData(0, 0)] // 空內容：0 個 chunk
    [InlineData(1, 1)]
    [InlineData(ChunkedBlobCipher.ChunkSize, 1)] // 剛好一整塊
    [InlineData(ChunkedBlobCipher.ChunkSize + 1, 2)] // 多一個 byte 就要多開一塊
    [InlineData(ChunkedBlobCipher.ChunkSize * 3, 3)]
    public void ComputeEncryptedLength_MatchesExpectedChunkCount(long plaintextLength, long expectedChunkCount)
    {
        var expected = ChunkedBlobCipher.HeaderSize + expectedChunkCount * (12 + 16) + plaintextLength;

        var actual = ChunkedBlobCipher.ComputeEncryptedLength(plaintextLength);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildHeader_IsEncryptedHeader_ReadPlaintextLength_RoundTrip()
    {
        var header = ChunkedBlobCipher.BuildHeader(123456789);

        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(header));
        Assert.Equal(123456789, ChunkedBlobCipher.ReadPlaintextLength(header));
        Assert.Equal(ChunkedBlobCipher.ChunkSize, ChunkedBlobCipher.ReadChunkSize(header));
    }

    // === MSE2：表頭挪一個 byte 塞 key id，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E ===

    [Fact]
    public void BuildHeaderV1_ReadKeyId_ReturnsNull()
    {
        // 舊格式沒有 key id 欄位——呼叫端據此判斷「這筆資料寫入時還沒有 key id 概念，
        // 不需要比對指紋」，見 ContentStreamService 的說明
        var header = ChunkedBlobCipher.BuildHeader(123);

        Assert.Null(ChunkedBlobCipher.ReadKeyId(header));
    }

    [Fact]
    public void BuildHeaderV2_IsEncryptedHeader_ReadPlaintextLengthAndChunkSize_RoundTrip()
    {
        var header = ChunkedBlobCipher.BuildHeader(123456789, keyId: 0xAB);

        Assert.True(ChunkedBlobCipher.IsEncryptedHeader(header));
        Assert.Equal(123456789, ChunkedBlobCipher.ReadPlaintextLength(header));
        // key id 借用的是 chunkSize 欄位的最高 byte——讀出來的 chunkSize 必須完全不受影響，
        // 否則 ContentStreamService 拿它算 chunk 邊界會整個算錯
        Assert.Equal(ChunkedBlobCipher.ChunkSize, ChunkedBlobCipher.ReadChunkSize(header));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xAB)]
    [InlineData((byte)0xFF)]
    public void BuildHeaderV2_ReadKeyId_RoundTrips(byte keyId)
    {
        var header = ChunkedBlobCipher.BuildHeader(1, keyId);

        Assert.Equal(keyId, ChunkedBlobCipher.ReadKeyId(header));
    }

    [Fact]
    public void BuildHeaderV1AndV2_ProduceSameLength_HeaderSizeUnchanged()
    {
        // 表頭大小不變是這個設計的重點——所有既有的 Range 位移數學（ChunkByteRangeOnDisk／
        // ComputeEncryptedLength）完全不需要因為新增 key id 而改寫
        Assert.Equal(ChunkedBlobCipher.BuildHeader(999).Length, ChunkedBlobCipher.BuildHeader(999, 0x01).Length);
        Assert.Equal(ChunkedBlobCipher.HeaderSize, ChunkedBlobCipher.BuildHeader(999, 0x01).Length);
    }

    [Fact]
    public void IsEncryptedHeader_RandomBytes_ReturnsFalse()
    {
        var random = new byte[ChunkedBlobCipher.HeaderSize];
        Array.Fill(random, (byte)0xAB);

        Assert.False(ChunkedBlobCipher.IsEncryptedHeader(random));
    }

    [Fact]
    public void IsEncryptedHeader_TooShort_ReturnsFalse()
    {
        Assert.False(ChunkedBlobCipher.IsEncryptedHeader(new byte[3]));
    }

    [Theory]
    [InlineData(0, 100, 0, 0)] // 第一塊裡的一小段
    [InlineData(0, ChunkedBlobCipher.ChunkSize, 0, 0)] // 剛好第一塊整塊
    [InlineData(0, ChunkedBlobCipher.ChunkSize + 1, 0, 1)] // 跨到第二塊
    [InlineData(ChunkedBlobCipher.ChunkSize, 10, 1, 1)] // 完全落在第二塊
    [InlineData(ChunkedBlobCipher.ChunkSize - 1, 2, 0, 1)] // 跨越塊邊界的兩個位元組
    public void ChunksCovering_ReturnsCorrectChunkRange(long start, long length, long expectedFirst, long expectedLast)
    {
        var (first, last) = ChunkedBlobCipher.ChunksCovering(start, length, ChunkedBlobCipher.ChunkSize);

        Assert.Equal(expectedFirst, first);
        Assert.Equal(expectedLast, last);
    }

    [Fact]
    public void ChunkByteRangeOnDisk_SumOfAllChunks_MatchesComputeEncryptedLengthMinusHeader()
    {
        const long plaintextLength = ChunkedBlobCipher.ChunkSize * 2 + 500; // 3 個 chunk，最後一塊只有 500 bytes
        var (_, lastChunkIndex) = ChunkedBlobCipher.ChunksCovering(0, plaintextLength, ChunkedBlobCipher.ChunkSize);

        long sumOfChunkLengths = 0;
        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var (_, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(i, plaintextLength, ChunkedBlobCipher.ChunkSize);
            sumOfChunkLengths += length;
        }

        var expectedTotal = ChunkedBlobCipher.ComputeEncryptedLength(plaintextLength) - ChunkedBlobCipher.HeaderSize;
        Assert.Equal(expectedTotal, sumOfChunkLengths);
    }

    [Fact]
    public void ChunkByteRangeOnDisk_ChunksAreContiguous_NoGapsOrOverlaps()
    {
        const long plaintextLength = ChunkedBlobCipher.ChunkSize * 3 + 12345;
        var (_, lastChunkIndex) = ChunkedBlobCipher.ChunksCovering(0, plaintextLength, ChunkedBlobCipher.ChunkSize);

        long expectedNextOffset = ChunkedBlobCipher.HeaderSize;
        for (var i = 0; i <= lastChunkIndex; i++)
        {
            var (offset, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(i, plaintextLength, ChunkedBlobCipher.ChunkSize);
            Assert.Equal(expectedNextOffset, offset);
            expectedNextOffset = offset + length;
        }

        Assert.Equal(ChunkedBlobCipher.ComputeEncryptedLength(plaintextLength), expectedNextOffset);
    }

    [Fact]
    public void ChunkByteRangeOnDisk_LastChunk_HasShortPlaintextLength()
    {
        const long plaintextLength = ChunkedBlobCipher.ChunkSize + 777;
        var (_, length) = ChunkedBlobCipher.ChunkByteRangeOnDisk(1, plaintextLength, ChunkedBlobCipher.ChunkSize);

        Assert.Equal(12 + 16 + 777, length);
    }

    [Fact]
    public void EncryptChunk_ThenDecryptChunk_RoundTripsExactly()
    {
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 255, 0, 128 };

        var encrypted = ChunkedBlobCipher.EncryptChunk(plaintext, Key);
        var decrypted = ChunkedBlobCipher.DecryptChunk(encrypted, Key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptChunk_SamePlaintextTwice_ProducesDifferentCiphertext()
    {
        var plaintext = new byte[] { 1, 2, 3 };

        var first = ChunkedBlobCipher.EncryptChunk(plaintext, Key);
        var second = ChunkedBlobCipher.EncryptChunk(plaintext, Key);

        Assert.NotEqual(first, second); // nonce 隨機
    }

    [Fact]
    public void DecryptChunk_WrongKey_Throws()
    {
        var plaintext = new byte[] { 1, 2, 3 };
        var encrypted = ChunkedBlobCipher.EncryptChunk(plaintext, Key);
        var wrongKey = Enumerable.Repeat((byte)0xFF, 32).ToArray();

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ChunkedBlobCipher.DecryptChunk(encrypted, wrongKey));
    }

    [Fact]
    public void EncryptChunk_EmptyChunk_RoundTrips()
    {
        var encrypted = ChunkedBlobCipher.EncryptChunk(ReadOnlySpan<byte>.Empty, Key);
        var decrypted = ChunkedBlobCipher.DecryptChunk(encrypted, Key);

        Assert.Empty(decrypted);
    }
}
