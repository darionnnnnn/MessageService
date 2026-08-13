namespace MessageService.Data.Crypto;

/// <summary>把明文來源串流包成分塊加密後的唯讀串流（表頭 + chunk...，格式見 ChunkedBlobCipher），
/// 邊讀邊加密，一次只在記憶體裡放一個 chunk（1MB），不管來源檔案多大都不會整份進記憶體——
/// 直接指派給 SqlParameter.Value 做串流上傳，或用 CopyToAsync 寫進 SqliteBlob，都只會照這個
/// 串流實際被讀取的步調消耗來源串流，跟 DbContentWorkSource 既有的無加密路徑記憶體特性一致。
/// 唯讀、forward-only：只支援 Read／ReadAsync，其餘 Stream 操作一律不支援。
///
/// keyId 是表頭要帶的 1 byte 金鑰指紋（見 ChunkedBlobCipher.BuildHeader 的 MSE2 多載），
/// 由 FieldCipher.CreateEncryptingStream 傳入，跟文字欄位共用同一份指紋來源。</summary>
public sealed class ChunkedEncryptingStream(Stream source, long plaintextLength, byte[] key, byte keyId) : Stream
{
    private readonly long _plaintextLength = plaintextLength;
    private readonly byte[] _readBuffer = new byte[ChunkedBlobCipher.ChunkSize];
    private MemoryStream? _pending;
    private long _plaintextRemaining = plaintextLength;
    private bool _headerEmitted;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending is null || _pending.Position >= _pending.Length)
        {
            if (!_headerEmitted)
            {
                _pending = new MemoryStream(ChunkedBlobCipher.BuildHeader(_plaintextLength, keyId), writable: false);
                _headerEmitted = true;
            }
            else if (_plaintextRemaining > 0)
            {
                _pending = await BuildNextEncryptedChunkAsync(cancellationToken);
            }
            else
            {
                return 0;
            }
        }

        return await _pending.ReadAsync(buffer, cancellationToken);
    }

    private async Task<MemoryStream> BuildNextEncryptedChunkAsync(CancellationToken cancellationToken)
    {
        var toRead = (int)Math.Min(ChunkedBlobCipher.ChunkSize, _plaintextRemaining);
        var read = await ReadExactlyOrThrowAsync(source, _readBuffer.AsMemory(0, toRead), cancellationToken);
        _plaintextRemaining -= read;

        var encryptedChunk = ChunkedBlobCipher.EncryptChunk(_readBuffer.AsSpan(0, read), key);
        return new MemoryStream(encryptedChunk, writable: false);
    }

    private static async Task<int> ReadExactlyOrThrowAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    $"Source stream ended after {totalRead} bytes but {buffer.Length} were expected for this chunk.");
            }
            totalRead += read;
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
