using System.Data;
using System.Data.Common;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ContentDownloadService.RequeuePendingAsync／ProcessAsync 原本
/// 直接開 scope 拿 MessageDbContext 的那段邏輯搬過來，行為刻意保持一致。CompleteAsync 不透過
/// EF 的 byte[] 屬性寫 blob（那樣整份要先進記憶體、變成 change tracker 的一個大字串），
/// 改用兩種 provider 各自的串流寫入方式，見該方法說明。cipher.Enabled 時再包一層
/// ChunkedEncryptingStream（見 FieldCipher.CreateEncryptingStream）——blob 不像文字欄位能用
/// EF ValueConverter 整值加密，Range 拖進度需要分塊，格式與讀取端見 ChunkedBlobCipher。</summary>
public class DbContentWorkSource(
    MessageDbContext dbContext,
    IOptions<ContentDownloadOptions> options,
    FieldCipher cipher,
    ILogger<DbContentWorkSource> logger) : IContentWorkSource
{
    private const int BufferSize = 81920;
    private const string BlobTableName = "MessageContentBlobs";
    private const string BlobColumnName = "Content";
    private const string BlobIdColumnName = "MessageContentId";
    private readonly ContentDownloadOptions _options = options.Value;

    public async Task<IReadOnlyList<long>> GetPendingIdsAsync(bool reclaimDownloading, CancellationToken cancellationToken)
    {
        // Downloading：卡在「已認領但沒做完」的列。當 reclaimDownloading=true 時，只回收
        // 逾期（ClaimedAt < UtcNow - ClaimLeaseMinutes）或 ClaimedAt 為 null（舊資料／異常遺留）的列，
        // 並將狀態改回 Pending、清空 ClaimedAt 重新撿回。租約未逾期的 Downloading 表示其他主機或本機
        // worker 仍在正常下載中，一律不碰也不列入待處理清單。
        var leaseCutoff = DateTimeOffset.UtcNow.AddMinutes(-_options.ClaimLeaseMinutes);
        var pendingIds = await dbContext.MessageContents
            .Where(c => c.DownloadStatus == DownloadStatus.Pending
                || (reclaimDownloading && c.DownloadStatus == DownloadStatus.Downloading
                    && (c.ClaimedAt == null || c.ClaimedAt < leaseCutoff)))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (reclaimDownloading && pendingIds.Count > 0)
        {
            await dbContext.MessageContents
                .Where(c => pendingIds.Contains(c.Id) && c.DownloadStatus == DownloadStatus.Downloading)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.DownloadStatus, DownloadStatus.Pending)
                    .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null), cancellationToken);
        }

        // Failed 只在訊息到達後的保留視窗內、且累計失敗次數未達上限才重新撿回——LINE 的內容
        // 有保存期限，過期的檔案永遠下載不到，不該每次重啟都無限重跑（見 ContentDownloadOptions）。
        // cutoff 比對刻意不下推到 SQL：GroupMessage.ReceivedAt 在 SQLite 上沒有支援範圍比較的
        // DateTimeOffset 轉換（跟 EventTimestamp 不同——這欄位從 InitialCreate 就是預設的文字
        // 格式儲存，現在才加轉換會讓既有 messages.db 的既有資料被讀成亂碼）。Failed 筆數本來
        // 就是小量（下載失敗的內容），先用 FailedAttempts 門檻縮小範圍後在記憶體篩 cutoff
        // 划算得多，不值得為此冒風險改儲存格式。
        // 只投影 Id 與 ReceivedAt。附檔 blob 已拆到獨立的 MessageContentBlobs 表，父表這邊
        // 撈整列不再有 blob 的代價；FailAsync 也會順手刪掉失敗留下的 blob 列（行程被殺在串流
        // 中途留下的殘留則由下次 CompleteAsync 先刪後插覆蓋掉）。
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.FailedRetryWindowDays);
        var failedCandidates = await dbContext.MessageContents
            .Where(c => c.DownloadStatus == DownloadStatus.Failed
                && c.FailedAttempts < _options.MaxFailedRetries
                && c.GroupMessage != null)
            .Select(c => new { c.Id, c.GroupMessage!.ReceivedAt })
            .ToListAsync(cancellationToken);

        var retryableIds = failedCandidates
            .Where(c => c.ReceivedAt >= cutoff)
            .Select(c => c.Id)
            .ToList();

        if (retryableIds.Count > 0)
        {
            await dbContext.MessageContents
                .Where(c => retryableIds.Contains(c.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.DownloadStatus, DownloadStatus.Pending)
                    .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null), cancellationToken);
        }

        return pendingIds.Concat(retryableIds).ToList();
    }

    public Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken) =>
        dbContext.MessageContents
            .Where(c => c.Id == contentId && c.DownloadStatus == DownloadStatus.Pending && c.GroupMessage != null)
            .Select(c => new ContentWorkItem(
                c.Id,
                c.GroupMessage!.LineMessageId,
                c.GroupMessage.MessageType,
                c.GroupMessage.StickerId))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>blob 寫入先於中繼資料更新：中斷時（例如服務被殺）狀態最壞停在 Downloading，
    /// 租約逾期後由 RequeuePendingAsync 回收重跑（見 GetPendingIdsAsync 對 Downloading 的回收
    /// 邏輯），不會留下「狀態是 Completed 但內容是半截」的資料。
    ///
    /// 認領（Pending → Downloading）刻意放在這裡而不是 GetAsync：影片／語音要先靠 GetAsync
    /// 反覆查詢轉檔狀態（見 ContentDownloadService.CheckTranscodingAsync），同一個 worker 對
    /// 同一筆內容會多次呼叫 GetAsync，若在那裡認領，第二次查詢會因為狀態已經不是 Pending
    /// 而被自己擋下來。真正需要獨占的是「寫入同一顆 blob」這個動作本身——ContentDownloadService
    /// 有多個 worker 共讀同一個 Channel，同一個 contentId 有機會被入列兩次，沒有認領的話兩個
    /// worker 會同時對同一顆 blob 下載並交錯寫入（SQLite 的 zeroblob + SqliteBlob 尤其明顯），
    /// 而且兩邊最後都標 Completed，沒有機制能再把它們揪出來重試。ExecuteUpdateAsync 的 WHERE
    /// 帶著 DownloadStatus==Pending 條件，第二個 worker 的更新會影響 0 列，claimed==0 時直接
    /// return，不重複下載寫入。
    ///
    /// SQL Server 用 SqlParameter 的串流參數（直接把 Stream 指派給 Value，provider 端會邊讀
    /// 邊送，不整份進記憶體）；SQLite 沒有這個機制，改用 zeroblob() 先配置定長空間，再用
    /// SqliteBlob（Stream 子類別）增量寫入。</summary>
    public async Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, CancellationToken cancellationToken)
    {
        var claimTime = DateTimeOffset.UtcNow;
        var claimed = await dbContext.MessageContents
            .Where(c => c.Id == contentId && c.DownloadStatus == DownloadStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.DownloadStatus, DownloadStatus.Downloading)
                .SetProperty(c => c.ClaimedAt, claimTime), cancellationToken);

        if (claimed == 0)
        {
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            // 沒啟用加密時 CreateEncryptingStream 直接傳回 content 本身（不包一層），
            // effectiveLength 也維持明文長度不變——兩個 provider 的寫入邏輯完全不用知道
            // 加密有沒有開，只管把手上這個串流／長度寫進去就對了
            var effectiveStream = cipher.CreateEncryptingStream(content, contentLength);
            var effectiveLength = cipher.Enabled ? ChunkedBlobCipher.ComputeEncryptedLength(contentLength) : contentLength;

            // 包一層計數器來核對「實際寫進去的位元組數」是否等於宣稱的長度。這不是防禦性
            // 潔癖：SQLite 是先 zeroblob(N) 配置好 N bytes 再往裡面填，Stream.CopyToAsync 在
            // 來源提早結束時「不會」拋例外，於是尾巴那段零就留在資料庫裡，接著下面照樣把狀態
            // 標成 Completed——結果是一個補零的半截檔案，讀取端還會照 LENGTH(Content) 把整包
            // 送給瀏覽器，而且因為狀態是 Completed，重試機制永遠不會再撿它回來。
            // contentLength 來自 LINE 回應的 Content-Length 標頭，本來就不是可以無條件相信的值。
            var countingStream = new ByteCountingStream(effectiveStream);

            if (dbContext.Database.IsSqlite())
            {
                await WriteContentSqliteAsync((SqliteConnection)connection, contentId, countingStream, effectiveLength, cancellationToken);
            }
            else
            {
                await WriteContentSqlServerAsync(connection, contentId, countingStream, cancellationToken);
            }

            if (countingStream.BytesRead != effectiveLength)
            {
                // 往外拋而不是標成 Failed：這是一次性的傳輸問題，交給 ContentDownloadService
                // 既有的重試迴圈處理
                throw new InvalidOperationException(
                    $"Content stream for message content {contentId} produced {countingStream.BytesRead} bytes "
                    + $"but {effectiveLength} were declared; refusing to mark it as completed.");
            }
        }
        catch
        {
            // 認領已經把狀態從 Pending 改成 Downloading，任何失敗（上面的長度不符、連線中斷、
            // SQL 逾時等）往外拋之前都要改回 Pending——不然 ProcessAsync 重試時呼叫的
            // CompleteAsync 會因為 WHERE DownloadStatus==Pending 認領不到（claimed==0）而
            // 直接靜默 return，既不寫入也不拋例外，重試迴圈會把這次失敗誤判成功，
            // 這顆內容永遠卡在 Downloading（見 DownloadStatus.Downloading 的回收說明，
            // 逾期後由 RequeuePendingAsync 撿回）。
            // 回退本身失敗（多半跟原始失敗同因，例如資料庫斷線）就放手讓原始例外照常往外拋，
            // 不讓回退的例外把真正的失敗原因蓋掉——這種情況下狀態留在 Downloading，
            // 由租約逾期後的掃描當最後防線
            try
            {
                await RevertClaimAsync(contentId, cancellationToken);
            }
            catch
            {
                // 保留原始例外
            }
            throw;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            await dbContext.MessageContents
                .Where(c => c.Id == contentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.ContentType, contentType)
                    .SetProperty(c => c.DownloadStatus, DownloadStatus.Completed)
                    .SetProperty(c => c.CompletedAt, now)
                    .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null)
                    // 歸零，不然「失敗 9 次後終於成功」的內容會永遠帶著 FailedAttempts=9，
                    // 日後若加上「重新下載」之類的功能會從 9 起跳、一次就撞到 MaxFailedRetries
                    .SetProperty(c => c.FailedAttempts, 0),
                    cancellationToken);
        }
        catch (Exception ex)
        {
            // blob 已經寫進去了，中繼資料卻沒更新——這一筆會卡在 Downloading，
            // 租約逾期後由 RequeuePendingAsync 回收整個重跑，所以特別記下來
            logger.LogError(ex, "內容 {ContentId} 的 blob 已寫入，但中繼資料更新失敗", contentId);
            throw;
        }
    }

    /// <summary>把認領失敗的列改回 Pending 並清空 ClaimedAt，讓 ProcessAsync 的重試迴圈下一次呼叫 CompleteAsync
    /// 時能重新認領到（見上面 catch 區塊的說明）。用 ExecuteUpdateAsync 直接下 SQL，不透過
    /// change tracker——這裡不需要、也不該去查詢或建立 change tracker 對這個實體的追蹤。</summary>
    private Task RevertClaimAsync(long contentId, CancellationToken cancellationToken) =>
        dbContext.MessageContents
            .Where(c => c.Id == contentId && c.DownloadStatus == DownloadStatus.Downloading)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.DownloadStatus, DownloadStatus.Pending)
                .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null), cancellationToken);

    private static async Task WriteContentSqlServerAsync(
        DbConnection connection, long contentId, Stream content, CancellationToken cancellationToken)
    {
        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = $"DELETE FROM {BlobTableName} WHERE {BlobIdColumnName} = @id";
            AddParameter(deleteCmd, "@id", contentId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = (SqlCommand)connection.CreateCommand();
        command.CommandText = $"INSERT INTO {BlobTableName} ({BlobIdColumnName}, {BlobColumnName}) VALUES (@id, @content)";

        var contentParam = new SqlParameter("@content", SqlDbType.VarBinary, -1) { Value = content };
        command.Parameters.Add(contentParam);
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.BigInt) { Value = contentId });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteContentSqliteAsync(
        SqliteConnection connection, long contentId, Stream content, long contentLength, CancellationToken cancellationToken)
    {
        await using (var init = connection.CreateCommand())
        {
            init.CommandText = $"DELETE FROM {BlobTableName} WHERE {BlobIdColumnName} = @id; " +
                               $"INSERT INTO {BlobTableName} ({BlobIdColumnName}, {BlobColumnName}) VALUES (@id, zeroblob(@length));";
            AddParameter(init, "@id", contentId);
            AddParameter(init, "@length", contentLength);
            await init.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var blob = new SqliteBlob(connection, BlobTableName, BlobColumnName, contentId);
        await content.CopyToAsync(blob, BufferSize, cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    public async Task FailAsync(long contentId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await dbContext.MessageContents
            .Where(c => c.Id == contentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.DownloadStatus, DownloadStatus.Failed)
                .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null)
                .SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1)
                .SetProperty(c => c.LastAttemptAt, now),
                cancellationToken);

        await dbContext.MessageContentBlobs
            .Where(b => b.MessageContentId == contentId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>唯讀的直通包裝，只多做一件事：累計實際被讀走的位元組數，讓 CompleteAsync 能在
/// 寫入結束後核對「宣稱的長度」與「真的寫進去多少」。兩個 provider 的寫入路徑對短讀的反應
/// 不一致（SQLite 的 zeroblob 會靜默補零、SQL Server 是有多少寫多少），統一在這裡把關比在
/// 兩條路徑各自處理可靠。</summary>
internal sealed class ByteCountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }

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

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        BytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
