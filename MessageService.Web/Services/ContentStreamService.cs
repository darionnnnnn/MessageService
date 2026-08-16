using System.Data;
using System.Data.Common;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

/// <summary>
/// 把 MessageContents.Content（varbinary(max)／BLOB）串流給 HTTP 回應，支援 Range 請求（影片/語音拖拉進度）。
/// 不用 EF 把整個 blob 讀進記憶體。
/// 在 SQLite 下，Range 請求不能使用 SQL 運算式 substr，因為 substr 屬於純量運算式，
/// SQLite 必須先將整個 blob 欄位物化至記憶體後才能切片；因此 SQLite 改採 Microsoft.Data.Sqlite.SqliteBlob
/// 原生增量讀取，以 Seek() 搭配區間複製只讀取實際需要的位元組，避免記憶體暴增。表頭讀取（判斷是否為密文的前 16 bytes）同樣走 SqliteBlob，不是只有 Range 切片。
/// 而 SQL Server 的 SUBSTRING 原生即支援部分讀取，不會物化整份 blob，因此維持既有的 ADO.NET 串流查詢。
/// 兩種 provider 都不會在單一連線內做 Seek，瀏覽器的拖曳進度條是透過發出新的 Range 請求來達成。
///
/// 加密啟用時 blob 存的是分塊密文（格式見 ChunkedBlobCipher，MSE1／MSE2 兩種都認，
/// DbContentWorkSource 寫入）——讀取端一律先偷看前 16 bytes 表頭判斷是不是這個格式
/// （不看 Encryption:Enabled 設定本身，純粹看資料長什麼樣子），這樣新舊資料可以混存。
/// 注意反過來不成立：加密設定事後被關掉時，既有的加密內容因為沒有金鑰可解，會直接回 404
/// （不是顯示亂碼），見 docs/ENCRYPTION.md。MSE2 表頭多帶一個 byte 的 key id，跟目前設定的
/// 金鑰指紋（cipher.MatchesKeyId）不符時視同內容不可用——輪替金鑰後舊資料還在，但沒有對應
/// 金鑰的那些會乾脆回 404，不會嘗試用錯的金鑰硬解。
/// Range 請求只解密涵蓋所需區間的那幾個 chunk，一次只在記憶體放一個 chunk，
/// 不管請求範圍多大都不會整份解密進記憶體。
///
/// 「是不是密文」既然只看資料本身，那個表頭就是外部可控的輸入（加密關閉期間，任何人都
/// 可以在群組裡傳一個開頭剛好是 MSE1／MSE2 的檔案），所以表頭裡的數字在使用前一定要驗證，
/// 見 StreamAsync 裡的說明。
/// </summary>
public class ContentStreamService(MessageDbContext dbContext, FieldCipher cipher, ILogger<ContentStreamService> logger)
{
    private const int BufferSize = 81920;

    /// <summary>可以 inline 顯示的 MIME type，真正的白名單（不是「前綴符合再扣掉黑名單」）。
    /// 刻意不放 image/svg+xml——SVG 跟 HTML 一樣會被瀏覽器當成可執行文件解析、能跑 &lt;script&gt;，
    /// 讓它 inline 等於允許任何丟進群組的 .svg 在檢視端同源執行任意腳本（本站沒有登入機制，
    /// 腳本可以直接把整個對話撈出去外送）。用列舉而不是前綴比對，是因為前綴比對會讓未來任何
    /// 新的、可執行的 image/* 型別自動獲得放行。</summary>
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg", "image/pjpeg", "image/png", "image/gif", "image/webp",
        "image/bmp", "image/avif", "image/heic", "image/heif",
        "video/mp4", "video/quicktime", "video/webm", "video/3gpp",
        "audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/wav", "audio/x-m4a",
    };

    /// <summary>把資料庫存的 Content-Type 正規化成可以拿來比對的形式：去掉 `; charset=…` 這類
    /// 參數、去空白、轉小寫。MIME type 依 RFC 2045 §5.1 本來就是大小寫不敏感，瀏覽器也一律
    /// 當小寫處理——不正規化就比對的話，`IMAGE/SVG+XML` 或 `image/svg+xml; charset=utf-8`
    /// 都能繞過黑名單再被前綴規則判成安全。收錄端兩條寫入路徑的值格式並不一致
    /// （LineContentClient 取 MediaType 已剝參數，IngestController 是把 Request.ContentType
    /// 原樣存下、會帶參數），所以正規化必須在這裡做。</summary>
    public static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';');
        var mediaType = separator >= 0 ? contentType[..separator] : contentType;
        mediaType = mediaType.Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? null : mediaType;
    }

    public static bool IsSafeToInline(string? normalizedContentType) =>
        normalizedContentType is not null && InlineSafeContentTypes.Contains(normalizedContentType);

    public async Task<ContentStreamResult> StreamAsync(
        long messageContentId, string? rangeHeader, string? ifNoneMatch, HttpResponse response, CancellationToken cancellationToken)
    {
        var meta = await dbContext.MessageContents
            .Where(c => c.Id == messageContentId)
            .Select(c => new { c.DownloadStatus, c.ContentType, c.FileName, c.CompletedAt })
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

                // 表頭本身沒有被任何認證標籤保護，而「這是不是密文」純粹看前 4 bytes 是不是
                // MSE1——也就是說這兩個數字是可以被餵進來的：加密關閉期間，任何人只要在群組
                // 傳一個開頭剛好是 MSE1 的檔案，之後管理者一打開加密，這裡就會照著檔案內容
                // 去配置陣列、去做除法。chunkSize 極大會直接配置到 OOM、為 0 會 DivideByZero。
                // 寫入端永遠只會寫 ChunkedBlobCipher.ChunkSize 這個編譯期常數，所以任何其他值
                // 都不是本格式；長度也必須跟實際 blob 大小對得起來，順帶擋掉截斷與一般資料損毀。
                var storedLength = await GetContentLengthAsync(connection, isSqlite, messageContentId, cancellationToken);
                if (chunkSize != ChunkedBlobCipher.ChunkSize
                    || totalLength < 0
                    || ChunkedBlobCipher.ComputeEncryptedLength(totalLength) != storedLength)
                {
                    logger.LogWarning(
                        "Message content {MessageContentId} looks like an encrypted blob but its header failed validation "
                        + "(chunkSize={ChunkSize}, plaintextLength={PlaintextLength}, storedLength={StoredLength}); treating as unavailable",
                        messageContentId, chunkSize, totalLength, storedLength);
                    return ContentStreamResult.NotFound;
                }

                // MSE2 表頭帶的 key id 跟目前設定的金鑰指紋不符——這顆 blob 是用另一把金鑰加的
                // （金鑰輪替，或多台主機的 Encryption:Key 沒對齊）。在這裡（回應還沒開始寫）
                // 判定失敗，比讓 AES-GCM 認證標籤驗證失敗才發現快，也才有機會乾淨地回 404——
                // 真的走到 DecryptChunk 才失敗的話，串流可能已經開始寫進 response，來不及改狀態碼
                if (!cipher.MatchesKeyId(ChunkedBlobCipher.ReadKeyId(header)))
                {
                    logger.LogWarning(
                        "Message content {MessageContentId} was encrypted with a different key (key id mismatch); treating as unavailable",
                        messageContentId);
                    return ContentStreamResult.NotFound;
                }
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

            // 內容一旦 Completed 就不會再變（見 DbContentWorkSource），所以不需要為了 ETag 把整包
            // 內容讀出來算雜湊——但也不能只用 Id：SQLite 的 Id 是 rowid 別名、沒有 AUTOINCREMENT，
            // 整張表被保留期清除清空之後新資料會從 1 重新發號。搭配 immutable + 一年 max-age，
            // 使用者的瀏覽器會把「舊的 id 1」永久快取成「新的 id 1」，看到已經被刪掉的舊圖而且
            // 連 revalidate 都不做。把 CompletedAt 一起折進去（已在上面同一次投影撈回來，零額外查詢），
            // 順帶也讓還原備份、換資料庫這類情境不會撞快取。
            var etag = $"\"mc-{messageContentId}-{meta.CompletedAt?.UtcTicks ?? 0:x}\"";
            // 看這顆 blob「實際上是不是密文」（isEncrypted，跟上面判斷要不要解密同一個依據），
            // 不是看 Encryption:Enabled 現在開著沒開——體檢輪抓到的間隙：先前寫的是
            // cipher.Enabled，會導致啟用加密前就存在、從未加密過的舊 blob，在管理者事後打開
            // 加密設定後，明明本身仍是明文，也被連坐套上 no-store，白白讓瀏覽器不再快取這些
            // 本來就不涉及個資合規顧慮的舊內容。加密的動機通常是個資合規，把解密後的內容長期
            // 存在每台值班電腦的瀏覽器快取磁碟上，是稽核會問的一條，但這個顧慮只適用於「這顆
            // blob 真的是加密寫入的」那些。ETag／304 仍照常運作（那是記憶體內的協商快取，
            // 不涉及磁碟落地），只是加密的那些不再允許瀏覽器跨工作階段保留內容本身
            response.Headers.CacheControl = isEncrypted ? "no-store" : "private, max-age=31536000, immutable";
            response.Headers.ETag = etag;

            if (MatchesIfNoneMatch(ifNoneMatch, etag))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return ContentStreamResult.Handled;
            }

            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.AcceptRanges = "bytes";

            // 送出去的是正規化後的值，不是資料庫原值——原值可能帶著 `; charset=…` 之類的參數，
            // 或大小寫混雜，直接回給瀏覽器等於把未經整理的上游輸入放進回應標頭
            var normalizedContentType = NormalizeContentType(meta.ContentType);
            var safe = IsSafeToInline(normalizedContentType);
            response.ContentType = safe ? normalizedContentType! : "application/octet-stream";
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

            if (isSqlite && isPartial)
            {
                // SQLite 的 substr 是 SQL 純量運算式，執行時必須先把整個 blob 物化到記憶體才能進行切片；
                // 當檔案較大（例如數百 MB 的影片或音訊）且使用者拖拉進度條時，每一次 Range 請求都會把整份 blob 物化進記憶體再丟掉絕大部分。
                // 改用 Microsoft.Data.Sqlite.SqliteBlob 進行原生增量讀取，以 Seek() 搭配 CopyRangeAsync()，
                // 只讀取實際請求的區間，不會物化整個 blob。
                // 註：SQL Server 的 SUBSTRING 原生支援局部讀取，無此物化問題，因此維持既有的 ADO.NET 串流查詢。
                await using var blob = new SqliteBlob((SqliteConnection)connection, "MessageContents", "Content", messageContentId, readOnly: true);
                blob.Seek(start.Value, SeekOrigin.Begin);
                await CopyRangeAsync(blob, response.Body, length, cancellationToken);
                return ContentStreamResult.Handled;
            }

            await using var command = connection.CreateCommand();
            if (isPartial)
            {
                command.CommandText = "SELECT SUBSTRING(Content, @start, @length) FROM MessageContents WHERE Id = @id";
                AddParameter(command, "@start", start.Value + 1); // SUBSTRING 是 1-indexed
                AddParameter(command, "@length", length);
            }
            else
            {
                // 沒有 Range 就是要整份——直接 SELECT 原始欄位，不必再繞去 SUBSTRING 切出
                // 「從頭到尾」這個等於整份的區間，省掉 SQL Server 端多一次的整份複製。
                // SQLite 非 Range 路徑透過 reader.GetStream(0) 本身即為增量串流，亦無物化問題。
                command.CommandText = "SELECT Content FROM MessageContents WHERE Id = @id";
            }
            AddParameter(command, "@id", messageContentId);

            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && !await reader.IsDBNullAsync(0, cancellationToken))
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
    /// response。SQLite 下使用 SqliteBlob 原生增量串流並 Seek 到 spanStart；SQL Server 下
    /// 則用一次 SUBSTRING 把這些 chunk 的密文連續段一次撈回來（chunk 在磁碟上是緊接著排列的，
    /// 見 ChunkedBlobCipher.ChunkByteRangeOnDisk），透過 SequentialAccess 的 DbDataReader 串流讀取。
    /// 兩者皆一次只解一個 chunk（至多 1MB）進記憶體，不管請求範圍多大都不會整份解密進記憶體。</summary>
    private async Task StreamEncryptedContentAsync(
        DbConnection connection, bool isSqlite, long messageContentId,
        long start, long length, int chunkSize, long totalPlaintextLength,
        HttpResponse response, CancellationToken cancellationToken)
    {
        // 空內容（0 bytes）的加密 blob 只有 16 bytes 表頭、零個 chunk。ChunksCovering(0, 0, C)
        // 因為 lastByte = -1 而算出「第 0 塊」，接著就會去讀一個不存在的 chunk 然後拋例外——
        // 未加密路徑同情境是正常的 200 + Content-Length: 0，這裡不擋就變成加密專屬的 500。
        if (length <= 0)
        {
            return;
        }

        var (firstChunk, lastChunk) = ChunkedBlobCipher.ChunksCovering(start, length, chunkSize);
        var (spanStart, _) = ChunkedBlobCipher.ChunkByteRangeOnDisk(firstChunk, totalPlaintextLength, chunkSize);
        var (lastChunkOffset, lastChunkOnDiskLength) = ChunkedBlobCipher.ChunkByteRangeOnDisk(lastChunk, totalPlaintextLength, chunkSize);
        var spanLength = lastChunkOffset + lastChunkOnDiskLength - spanStart;

        Stream onDiskStream;
        await using var sqliteBlob = isSqlite
            ? new SqliteBlob((SqliteConnection)connection, "MessageContents", "Content", messageContentId, readOnly: true)
            : null;
        await using var command = isSqlite ? null : connection.CreateCommand();
        DbDataReader? reader = null;

        if (isSqlite)
        {
            sqliteBlob!.Seek(spanStart, SeekOrigin.Begin);
            onDiskStream = sqliteBlob;
        }
        else
        {
            command!.CommandText = "SELECT SUBSTRING(Content, @start, @length) FROM MessageContents WHERE Id = @id";
            AddParameter(command, "@id", messageContentId);
            AddParameter(command, "@start", spanStart + 1); // SUBSTRING 是 1-indexed
            AddParameter(command, "@length", spanLength);

            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                // 這個提早 return 在下面的 try 之外，finally 不會執行，所以要自己收掉 reader
                await reader.DisposeAsync();
                return;
            }

            onDiskStream = reader.GetStream(0);
        }

        try
        {
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
        finally
        {
            // SQLite 那條的 onDiskStream 就是 sqliteBlob，已由上面的 await using 收掉；
            // SQL Server 這條的 onDiskStream 是 reader 開出來的，reader 之外要自己收
            if (reader is not null)
            {
                await onDiskStream.DisposeAsync();
                await reader.DisposeAsync();
            }
        }
    }

    /// <summary>把來源串流的前 length 個位元組複製到目的地串流。不能直接用 Stream.CopyToAsync，
    /// 因為 CopyToAsync 會一路複製到串流結尾，Range 請求會多送出區間之外的資料。</summary>
    private static async Task CopyRangeAsync(
        Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
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
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>讀取 blob 前 ChunkedBlobCipher.HeaderSize（16 bytes）表頭，用以判斷是否為 MSE1／MSE2 密文格式。
    /// 在 SQLite 下，每個 Range 請求都會呼叫此處；若使用 SQL 運算式 substr 會將整份 blob 物化至記憶體，
    /// 抵銷 Range 切片路徑的優化，因此改採 SqliteBlob 原生增量讀取。
    /// 而 SQL Server 的 SUBSTRING 原生即支援局部讀取且無物化問題，維持既有的 SQL 查詢。
    /// 捕獲 SqliteException 是防禦性設計：當 Content 為 NULL 或資料列異常時，SqliteBlob 建構子會拋出
    /// SqliteException，此處回傳空陣列退化為未加密內容處理，避免產生未處理的 500 例外。</summary>
    private static async Task<byte[]> ReadHeaderBytesAsync(
        DbConnection connection, bool isSqlite, long messageContentId, CancellationToken cancellationToken)
    {
        if (isSqlite)
        {
            try
            {
                await using var blob = new SqliteBlob((SqliteConnection)connection, "MessageContents", "Content", messageContentId, readOnly: true);
                var length = (int)Math.Min(ChunkedBlobCipher.HeaderSize, blob.Length);
                if (length <= 0)
                {
                    return [];
                }

                var header = new byte[length];
                await ReadExactlyAsync(blob, header, cancellationToken);
                return header;
            }
            catch (SqliteException)
            {
                return [];
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUBSTRING(Content, 1, @length) FROM MessageContents WHERE Id = @id";
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
