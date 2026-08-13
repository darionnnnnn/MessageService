using System.Security.Cryptography;

namespace MessageService.Data.Crypto;

/// <summary>MessageContents.Content（varbinary(max)）的分塊加密格式。blob 不能像文字欄位那樣
/// 整值加密——影片/語音要支援 Range 拖進度，解密必須能只處理使用者實際要的那段位元組，不能
/// 每次都把整份檔案解出來。格式：[表頭 16 bytes][chunk 0][chunk 1]...，每個 chunk 是
/// [nonce(12)][tag(16)][ciphertext(明文長度)]，固定 1MB 明文塊（最後一塊可能較短）。
/// 表頭：magic(4, "MSE1"／"MSE2") + chunkSize 相關欄位(4) + 明文總長度(8)——讀取端看 magic
/// 判斷這個 blob 是不是本格式（跟文字欄位的 ENC1:／ENC2: 前綴是同一種「舊資料混存」設計），
/// 明文總長度是 Range 請求算 Content-Length 用的（不能拿 DATALENGTH(Content) 那個是密文長度）。
///
/// MSE2（見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E）在同一個 4 bytes 欄位裡多塞了
/// 1 byte 的 key id：chunkSize 固定 1MB，用不到完整 32 位元，所以「挪」（不是「加」）這個欄位
/// 最高位元組給 key id，表頭總長度不變、既有的 offset／Range 數學完全不用改。ReadChunkSize
/// 一律遮掉最高位元組——MSE1 的這個位元組本來就恆為 0（chunkSize 遠小於 16MB 封頂），
/// 對舊格式讀出來的值沒有任何影響，兩種格式可以共用同一份讀取邏輯。
/// 這個類別只管格式與位元組數學，不管金鑰——金鑰封裝在 FieldCipher，見該類別的
/// CreateEncryptingStream／DecryptChunk／KeyId。</summary>
public static class ChunkedBlobCipher
{
    public const int ChunkSize = 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const uint ChunkSizeFieldMask = 0x00FFFFFF; // 低 3 bytes 是 chunkSize，最高 byte 是 MSE2 的 key id

    /// <summary>每個 chunk 除了明文內容以外多出來的位元組數（nonce+tag）——讀取端要配置
    /// 「單一 chunk 密文」緩衝區時用得到，見 ContentStreamService。</summary>
    public const int ChunkOnDiskOverhead = NonceSize + TagSize;

    public const int HeaderSize = 16;

    private static readonly byte[] MagicV1 = "MSE1"u8.ToArray();
    private static readonly byte[] MagicV2 = "MSE2"u8.ToArray();

    public static long ComputeEncryptedLength(long plaintextLength)
    {
        var numChunks = plaintextLength == 0 ? 0 : (plaintextLength + ChunkSize - 1) / ChunkSize;
        return HeaderSize + numChunks * ChunkOnDiskOverhead + plaintextLength;
    }

    /// <summary>舊格式（無 key id）——只留給測試模擬「加密啟用前就寫入的既有 blob」用，
    /// 正式寫入路徑（ChunkedEncryptingStream）一律呼叫下面帶 keyId 的多載，見該類別說明。</summary>
    public static byte[] BuildHeader(long plaintextLength)
    {
        var header = new byte[HeaderSize];
        MagicV1.CopyTo(header, 0);
        BitConverter.GetBytes(ChunkSize).CopyTo(header, 4);
        BitConverter.GetBytes(plaintextLength).CopyTo(header, 8);
        return header;
    }

    /// <summary>新格式（MSE2，帶 key id）——見類別說明的欄位挪用方式。keyId 是
    /// FieldCipher.KeyId（金鑰 SHA-256 前 4 bytes）的第一個 byte，跟文字欄位共用同一份
    /// 指紋來源，只是這裡礙於表頭空間只能留 1 byte。</summary>
    public static byte[] BuildHeader(long plaintextLength, byte keyId)
    {
        var header = new byte[HeaderSize];
        MagicV2.CopyTo(header, 0);
        var packed = (uint)ChunkSize | ((uint)keyId << 24);
        BitConverter.GetBytes(packed).CopyTo(header, 4);
        BitConverter.GetBytes(plaintextLength).CopyTo(header, 8);
        return header;
    }

    public static bool IsEncryptedHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize
        && (header[..4].SequenceEqual(MagicV1) || header[..4].SequenceEqual(MagicV2));

    public static long ReadPlaintextLength(ReadOnlySpan<byte> header) =>
        BitConverter.ToInt64(header.Slice(8, 8));

    public static int ReadChunkSize(ReadOnlySpan<byte> header) =>
        (int)(BitConverter.ToUInt32(header.Slice(4, 4)) & ChunkSizeFieldMask);

    /// <summary>MSE2 才有 key id，MSE1（舊格式）一律回傳 null——呼叫端據此判斷要不要比對
    /// 目前設定的金鑰指紋，見 ContentStreamService 的說明。</summary>
    public static byte? ReadKeyId(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderSize && header[..4].SequenceEqual(MagicV2) ? header[7] : null;

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
