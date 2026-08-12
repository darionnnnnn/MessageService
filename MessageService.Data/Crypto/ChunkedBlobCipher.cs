using System.Security.Cryptography;

namespace MessageService.Data.Crypto;

/// <summary>MessageContents.Content（varbinary(max)）的分塊加密格式。blob 不能像文字欄位那樣
/// 整值加密——影片/語音要支援 Range 拖進度，解密必須能只處理使用者實際要的那段位元組，不能
/// 每次都把整份檔案解出來。格式：[表頭 16 bytes][chunk 0][chunk 1]...，每個 chunk 是
/// [nonce(12)][tag(16)][ciphertext(明文長度)]，固定 1MB 明文塊（最後一塊可能較短）。
/// 表頭：magic(4, "MSE1") + chunkSize(4) + 明文總長度(8)——讀取端看 magic 判斷這個 blob
/// 是不是本格式（跟文字欄位的 ENC1: 前綴是同一種「舊資料混存」設計），明文總長度是 Range
/// 請求算 Content-Length 用的（不能拿 DATALENGTH(Content) 那個是密文長度）。
/// 這個類別只管格式與位元組數學，不管金鑰——金鑰封裝在 FieldCipher，見該類別的
/// CreateEncryptingStream／DecryptChunk。</summary>
public static class ChunkedBlobCipher
{
    public const int ChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>每個 chunk 除了明文內容以外多出來的位元組數（nonce+tag）——讀取端要配置
    /// 「單一 chunk 密文」緩衝區時用得到，見 ContentStreamService。</summary>
    public const int ChunkOnDiskOverhead = NonceSize + TagSize;

    public const int HeaderSize = 16;

    private static readonly byte[] Magic = "MSE1"u8.ToArray();

    public static long ComputeEncryptedLength(long plaintextLength)
    {
        var numChunks = plaintextLength == 0 ? 0 : (plaintextLength + ChunkSize - 1) / ChunkSize;
        return HeaderSize + numChunks * ChunkOnDiskOverhead + plaintextLength;
    }

    public static byte[] BuildHeader(long plaintextLength)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.GetBytes(ChunkSize).CopyTo(header, 4);
        BitConverter.GetBytes(plaintextLength).CopyTo(header, 8);
        return header;
    }

    public static bool IsEncryptedHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize && header[..4].SequenceEqual(Magic);

    public static long ReadPlaintextLength(ReadOnlySpan<byte> header) =>
        BitConverter.ToInt64(header.Slice(8, 8));

    public static int ReadChunkSize(ReadOnlySpan<byte> header) =>
        BitConverter.ToInt32(header.Slice(4, 4));

    /// <summary>明文位元組區間 [start, start+length) 落在哪些 chunk 索引範圍內（含頭尾）。</summary>
    public static (long FirstChunkIndex, long LastChunkIndex) ChunksCovering(long start, long length, int chunkSize)
    {
        var firstChunkIndex = start / chunkSize;
        var lastByte = start + length - 1;
        var lastChunkIndex = lastByte / chunkSize;
        return (firstChunkIndex, lastChunkIndex);
    }

    /// <summary>指定 chunk 索引在密文 blob 裡的位元組偏移與長度（給 SUBSTRING/substr 用）。
    /// 除了整份檔案的最後一個 chunk，其餘 chunk 都固定 ChunkOnDiskOverhead+chunkSize；偏移量是
    /// 前面完整 chunk 的累加，所以即使最後一塊比較短也不影響前面每一塊的偏移量計算。</summary>
    public static (long Offset, int Length) ChunkByteRangeOnDisk(long chunkIndex, long plaintextLength, int chunkSize)
    {
        var chunkPlaintextStart = chunkIndex * (long)chunkSize;
        var chunkPlaintextLength = (int)Math.Min(chunkSize, plaintextLength - chunkPlaintextStart);
        var onDiskOffset = HeaderSize + chunkIndex * (long)(ChunkOnDiskOverhead + chunkSize);
        return (onDiskOffset, ChunkOnDiskOverhead + chunkPlaintextLength);
    }

    public static byte[] EncryptChunk(ReadOnlySpan<byte> plaintextChunk, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextChunk.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintextChunk, ciphertext, tag);
        }

        var output = new byte[ChunkOnDiskOverhead + plaintextChunk.Length];
        nonce.CopyTo(output.AsSpan(0, NonceSize));
        tag.CopyTo(output.AsSpan(NonceSize, TagSize));
        ciphertext.CopyTo(output.AsSpan(NonceSize + TagSize));
        return output;
    }

    public static byte[] DecryptChunk(ReadOnlySpan<byte> encryptedChunk, byte[] key)
    {
        var nonce = encryptedChunk[..NonceSize];
        var tag = encryptedChunk.Slice(NonceSize, TagSize);
        var ciphertext = encryptedChunk[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
