using System.Data;
using System.Data.Common;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

/// <summary>
/// 把 MessageContents.Content（varbinary(max)）串流給 HTTP 回應，支援 Range 請求（影片/語音拖拉進度）。
/// 不用 EF 把整個 blob 讀進記憶體：Range 請求直接在 SQL 端用 SUBSTRING/substr 切出所需片段，
/// 只讀取實際要傳送的位元組；ADO.NET 的 blob stream 是 forward-only，不支援 Seek，
/// 所以「拖進度」是靠瀏覽器對同一個 URL 發出新的 Range 請求，而非在單一連線內做 Seek。
///
/// 加密啟用時 blob 存的是分塊密文（格式見 ChunkedBlobCipher，DbContentWorkSource 寫入）——
/// 讀取端一律先偷看前 16 bytes 表頭判斷是不是這個格式（不看 Encryption:Enabled 設定本身，
/// 純粹看資料長什麼樣子），這樣新舊資料混存、甚至加密設定事後被關掉，既有的加密內容
/// 還是讀得到。Range 請求只解密涵蓋所需區間的那幾個 chunk，一次只在記憶體放一個 chunk，
/// 不管請求範圍多大都不會整份解密進記憶體。
/// </summary>
public class ContentStreamService(MessageDbContext dbContext, FieldCipher cipher)
{
    private const int BufferSize = 81920;

    /// <summary>inline 顯示的白名單：圖片／影片／語音在瀏覽器裡開啟本身就是預期用途。
    /// image/svg+xml 明確排除——SVG 跟 HTML 一樣會被瀏覽器當成可執行文件解析、能跑 &lt;script&gt;，
    /// 讓它 inline 等於允許任何丟進群組的 .svg 在檢視端同源執行任意腳本。</summary>
    private static bool IsSafeToInline(string? contentType) =>
        contentType is not null
        && contentType != "image/svg+xml"
        && (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    public async Task<ContentStreamResult> StreamAsync(
        long messageContentId, string? rangeHeader, string? ifNoneMatch, HttpResponse response, CancellationToken cancellationToken)
    {
        var meta = await dbContext.MessageContents
            .Where(c => c.Id == messageContentId)
            .Select(c => new { c.DownloadStatus, c.ContentType, c.FileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (meta is null || meta.DownloadStatus != DownloadStatus.Completed)
        {
            return ContentStreamResult.NotFound;
        }

        var isSqlite = dbContext.Database.IsSqlite();
        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var header = await ReadHeaderBytesAsync(connection, isSqlite, messageContentId, cancellationToken);
            var isEncrypted = ChunkedBlobCipher.IsEncryptedHeader(header);
            long totalLength;
            var chunkSize = ChunkedBlobCipher.ChunkSize;

            if (isEncrypted)
            {
                // 沒有金鑰解不開：視同內容不可用，不要在這裡才半途拋例外——回應還沒開始寫，
                // 乾脆當作找不到，跟其他「內容不可用」情境一致
                if (!cipher.Enabled)
                {
                    return ContentStreamResult.NotFound;
                }

                totalLength = ChunkedBlobCipher.ReadPlaintextLength(header);
                chunkSize = ChunkedBlobCipher.ReadChunkSize(header);
            }
            else
            {
                totalLength = await GetContentLengthAsync(connection, isSqlite, messageContentId, cancellationToken);
            }

            var (start, length, isPartial) = ParseRange(rangeHeader, totalLength);

            if (start is null)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{totalLength}";
                return ContentStreamResult.Handled;
            }

            // 內容一旦 Completed 就不會再變（見 DbContentWorkSource），ETag 純粹用 Id 推算即可，
            // 不需要算內容雜湊；可以放心用 immutable 快取，重複瀏覽同一段對話不用再打資料庫拉圖
            var etag = $"\"mc-{messageContentId}\"";
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            response.Headers.ETag = etag;

            if (MatchesIfNoneMatch(ifNoneMatch, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return ContentStreamResult.Handled;
            }

            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.AcceptRanges = "bytes";

            var safe = IsSafeToInline(meta.ContentType);
            response.ContentType = safe ? meta.ContentType! : "application/octet-stream";
            if (meta.FileName is not null)
            {
                var disposition = safe ? "inline" : "attachment";
                var asciiFallback = BuildAsciiFallbackFileName(meta.FileName);
                response.Headers.ContentDisposition =
                    $"{disposition}; filename=\"{asciiFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(meta.FileName)}";
            }

            if (isPartial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{totalLength}";
            }

            response.ContentLength = length;

            if (isEncrypted)
            {
                await StreamEncryptedContentAsync(
                    connection, isSqlite, messageContentId, start.Value, length, chunkSize, totalLength, response, cancellationToken);
                return ContentStreamResult.Handled;
            }

            await using var command = connection.CreateCommand();
            if (isPartial)
            {
                command.CommandText = isSqlite
                    ? "SELECT substr(Content, @start, @length) FROM MessageContents WHERE Id = @id"
                    : "SELECT SUBSTRING(Content, @start, @length) FROM MessageContents WHERE Id = @id";
                AddParameter(command, "@start", start.Value + 1); // SUBSTRING/substr 都是 1-indexed
                AddParameter(command, "@length", length);
            }
            else
            {
                // 沒有 Range 就是要整份——直接 SELECT 原始欄位，不必再繞去 SUBSTRING/substr 切出
                // 「從頭到尾」這個等於整份的區間，省掉 SQL Server 端多一次的整份複製
                command.CommandText = "SELECT Content FROM MessageContents WHERE Id = @id";
            }
            AddParameter(command, "@id", messageContentId);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                await using var blobStream = reader.GetStream(0);
                await blobStream.CopyToAsync(response.Body, BufferSize, cancellationToken);
            }

            return ContentStreamResult.Handled;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>只把使用者實際要的明文區間 [start, start+length) 涵蓋到的 chunk 解密、寫進
    /// response——用一次 SUBSTRING/substr 把這些 chunk 的密文連續段一次撈回來（chunk 在磁碟上
    /// 是緊接著排列的，見 ChunkedBlobCipher.ChunkByteRangeOnDisk），透過 SequentialAccess 的
    /// DbDataReader 串流讀取，一次只解一個 chunk（至多 1MB）進記憶體，不管請求範圍多大都
    /// 不會整份解密進記憶體。</summary>
    private async Task StreamEncryptedContentAsync(
        DbConnection connection, bool isSqlite, long messageContentId,
        long start, long length, int chunkSize, long totalPlaintextLength,
        HttpResponse response, CancellationToken cancellationToken)
    {
        var (firstChunk, lastChunk) = ChunkedBlobCipher.ChunksCovering(start, length, chunkSize);
        var (spanStart, _) = ChunkedBlobCipher.ChunkByteRangeOnDisk(firstChunk, totalPlaintextLength, chunkSize);
        var (lastChunkOffset, lastChunkOnDiskLength) = ChunkedBlobCipher.ChunkByteRangeOnDisk(lastChunk, totalPlaintextLength, chunkSize);
        var spanLength = lastChunkOffset + lastChunkOnDiskLength - spanStart;

        await using var command = connection.CreateCommand();
        command.CommandText = isSqlite
            ? "SELECT substr(Content, @start, @length) FROM MessageContents WHERE Id = @id"
            : "SELECT SUBSTRING(Content, @start, @length) FROM MessageContents WHERE Id = @id";
        AddParameter(command, "@id", messageContentId);
        AddParameter(command, "@start", spanStart + 1); // SUBSTRING/substr 都是 1-indexed
        AddParameter(command, "@length", spanLength);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return;
        }

        await using var onDiskStream = reader.GetStream(0);
        var chunkBuffer = new byte[ChunkedBlobCipher.ChunkOnDiskOverhead + chunkSize];
        var plaintextCursor = firstChunk * (long)chunkSize;

        for (var chunkIndex = firstChunk; chunkIndex <= lastChunk; chunkIndex++)
        {
            var (_, onDiskChunkLength) = ChunkedBlobCipher.ChunkByteRangeOnDisk(chunkIndex, totalPlaintextLength, chunkSize);
            await ReadExactlyAsync(onDiskStream, chunkBuffer.AsMemory(0, onDiskChunkLength), cancellationToken);
            var plaintextChunk = cipher.DecryptChunk(chunkBuffer.AsSpan(0, onDiskChunkLength));

            // 這塊明文對應到整份檔案的 [chunkStart, chunkEnd)，跟使用者實際要的
            // [start, start+length) 取交集，只把交集部分寫進 response（頭尾兩塊通常只需要部分）
            var chunkStart = plaintextCursor;
            var chunkEnd = plaintextCursor + plaintextChunk.Length;
            var wantStart = Math.Max(chunkStart, start);
            var wantEnd = Math.Min(chunkEnd, start + length);
            if (wantEnd > wantStart)
            {
                var sliceOffset = (int)(wantStart - chunkStart);
                var sliceLength = (int)(wantEnd - wantStart);
                await response.Body.WriteAsync(plaintextChunk.AsMemory(sliceOffset, sliceLength), cancellationToken);
            }

            plaintextCursor = chunkEnd;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException(
                    $"Encrypted blob ended after {totalRead} bytes but {buffer.Length} were expected for this chunk.");
            }
            totalRead += read;
        }
    }

    private static bool MatchesIfNoneMatch(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrEmpty(ifNoneMatch))
        {
            return false;
        }

        var trimmed = ifNoneMatch.Trim();
        if (trimmed == "*")
        {
            return true;
        }

        return trimmed.Split(',').Select(s => s.Trim()).Any(s => s == etag);
    }

    /// <summary>filename= 參數（quoted-string，不支援 RFC 5987 編碼）的保守版本：ASCII 檔名原樣
    /// 保留（不支援 filename* 的舊客戶端還是看得到真正的名字），非 ASCII（中文檔名幾乎必然）
    /// 換成 file+副檔名——真正的檔名交給下面的 filename*=UTF-8''... 承載。順手濾掉會弄壞
    /// 標頭語法或允許標頭注入的字元（引號、反斜線、CR/LF）——FileName 來源是 LINE 訊息，
    /// 群組任何成員都能決定內容，不能當成可信輸入。</summary>
    private static string BuildAsciiFallbackFileName(string fileName)
    {
        var candidate = fileName.All(c => c < 128) ? fileName : "file" + AsciiExtensionOrEmpty(fileName);
        return new string(candidate.Where(c => c is not ('"' or '\\' or '\r' or '\n')).ToArray());
    }

    private static string AsciiExtensionOrEmpty(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && ext.All(c => c < 128) ? ext : "";
    }

    private static async Task<long> GetContentLengthAsync(
        DbConnection connection, bool isSqlite, long messageContentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isSqlite
            ? "SELECT LENGTH(Content) FROM MessageContents WHERE Id = @id"
            : "SELECT DATALENGTH(Content) FROM MessageContents WHERE Id = @id";
        AddParameter(command, "@id", messageContentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private static async Task<byte[]> ReadHeaderBytesAsync(
        DbConnection connection, bool isSqlite, long messageContentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = isSqlite
            ? "SELECT substr(Content, 1, @length) FROM MessageContents WHERE Id = @id"
            : "SELECT SUBSTRING(Content, 1, @length) FROM MessageContents WHERE Id = @id";
        AddParameter(command, "@id", messageContentId);
        AddParameter(command, "@length", ChunkedBlobCipher.HeaderSize);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as byte[] ?? [];
    }

    /// <summary>只支援單一區間 "bytes=start-end" 或 "bytes=start-"，影片/語音播放器都只用這種格式。</summary>
    private static (long? Start, long Length, bool IsPartial) ParseRange(string? rangeHeader, long totalLength)
    {
        if (string.IsNullOrEmpty(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return (0, totalLength, false);
        }

        var spec = rangeHeader["bytes=".Length..];
        var parts = spec.Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out var start))
        {
            return (0, totalLength, false);
        }

        var end = totalLength - 1;
        if (parts[1].Length > 0 && long.TryParse(parts[1], out var parsedEnd))
        {
            end = Math.Min(parsedEnd, totalLength - 1);
        }

        if (start < 0 || start >= totalLength || start > end)
        {
            return (null, 0, false);
        }

        return (start, end - start + 1, true);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
