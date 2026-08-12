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
