using System.Data;
using System.Data.Common;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ContentDownloadService.RequeuePendingAsync／ProcessAsync 原本
/// 直接開 scope 拿 MessageDbContext 的那段邏輯搬過來，行為刻意保持一致。CompleteAsync 不透過
/// EF 的 byte[] 屬性寫 blob（那樣整份要先進記憶體、變成 change tracker 的一個大字串），
/// 改用兩種 provider 各自的串流寫入方式，見該方法說明。cipher.Enabled 時再包一層
/// ChunkedEncryptingStream（見 FieldCipher.CreateEncryptingStream）——blob 不像文字欄位能用
/// EF ValueConverter 整值加密，Range 拖進度需要分塊，格式與讀取端見 ChunkedBlobCipher。</summary>
public class DbContentWorkSource(MessageDbContext dbContext, IOptions<ContentDownloadOptions> options, FieldCipher cipher) : IContentWorkSource
{
    private const int BufferSize = 81920;
    private readonly ContentDownloadOptions _options = options.Value;

    public async Task<IReadOnlyList<long>> GetPendingIdsAsync(CancellationToken cancellationToken)
    {
        // Downloading：上次行程被殺／當機時卡在「已認領但沒做完」的列，見 GetAsync 的認領邏輯
        // 與 DownloadStatus.Downloading 的說明。啟動接續沒辦法分辨「真的還在下載中」跟「已經
        // 沒有 worker 在處理」，一律當成中斷、整批撿回改回 Pending 重跑——單一行程模式下這是
        // 唯一的回收路徑，接受「worker 崩潰但行程沒重啟」這段期間該筆會卡住的已知限制
        var pendingIds = await dbContext.MessageContents
            .Where(c => c.DownloadStatus == DownloadStatus.Pending || c.DownloadStatus == DownloadStatus.Downloading)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (pendingIds.Count > 0)
        {
            await dbContext.MessageContents
                .Where(c => pendingIds.Contains(c.Id) && c.DownloadStatus == DownloadStatus.Downloading)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DownloadStatus, DownloadStatus.Pending), cancellationToken);
        }

        // Failed 只在訊息到達後的保留視窗內、且累計失敗次數未達上限才重新撿回——LINE 的內容
        // 有保存期限，過期的檔案永遠下載不到，不該每次重啟都無限重跑（見 ContentDownloadOptions）。
        // cutoff 比對刻意不下推到 SQL：GroupMessage.ReceivedAt 在 SQLite 上沒有支援範圍比較的
        // DateTimeOffset 轉換（跟 EventTimestamp 不同——這欄位從 InitialCreate 就是預設的文字
        // 格式儲存，現在才加轉換會讓既有 messages.db 的既有資料被讀成亂碼）。Failed 筆數本來
        // 就是小量（下載失敗的內容），先用 FailedAttempts 門檻縮小範圍後在記憶體篩 cutoff
        // 划算得多，不值得為此冒風險改儲存格式。
        // 只投影 Id 與 ReceivedAt，絕對不要用 Include + ToListAsync 把整個 MessageContent
        // 實體撈回來——那會連 Content（varbinary(max)）一起載進記憶體。而且「Failed 的列不會
        // 有內容」這個假設並不成立：SQLite 的 zeroblob(N) 是先 commit 才開始串流填入，中途
        // 失敗會留下一顆 N bytes 的 blob，重試耗盡後那列就變成「Failed 且帶著一顆大 blob」。
        // 一支 300MB 的影片失敗幾次，每次重新入列都會把幾百 MB 載進記憶體，正好違反本批次
        // 串流化要達成的目的。
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
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DownloadStatus, DownloadStatus.Pending), cancellationToken);
        }

        return pendingIds.Concat(retryableIds).ToList();
    }

    public async Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.MessageContents
            .Include(c => c.GroupMessage)
            .FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);

        if (content?.GroupMessage is null || content.DownloadStatus != DownloadStatus.Pending)
        {
            return null;
        }

        return new ContentWorkItem(content.Id, content.GroupMessage.LineMessageId, content.GroupMessage.MessageType);
    }

    /// <summary>blob 寫入先於中繼資料更新：中斷時（例如服務被殺）狀態最壞停在 Downloading，
    /// 下次啟動的 RequeuePendingAsync 會整個重跑（見 GetPendingIdsAsync 對 Downloading 的回收
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
        var claimed = await dbContext.MessageContents
            .Where(c => c.Id == contentId && c.DownloadStatus == DownloadStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DownloadStatus, DownloadStatus.Downloading), cancellationToken);

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
            // 只有整個行程重啟才會被 GetPendingIdsAsync 撿回）。
            // 回退本身失敗（多半跟原始失敗同因，例如資料庫斷線）就放手讓原始例外照常往外拋，
            // 不讓回退的例外把真正的失敗原因蓋掉——這種情況下狀態留在 Downloading，
            // 由啟動接續的掃描當最後防線
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

        var entity = await dbContext.MessageContents.FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.ContentType = contentType;
        entity.DownloadStatus = DownloadStatus.Completed;
        entity.CompletedAt = DateTimeOffset.UtcNow;
        // 歸零，不然「失敗 9 次後終於成功」的內容會永遠帶著 FailedAttempts=9，
        // 日後若加上「重新下載」之類的功能會從 9 起跳、一次就撞到 MaxFailedRetries
        entity.FailedAttempts = 0;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>把認領失敗的列改回 Pending，讓 ProcessAsync 的重試迴圈下一次呼叫 CompleteAsync
    /// 時能重新認領到（見上面 catch 區塊的說明）。用 ExecuteUpdateAsync 直接下 SQL，不透過
    /// change tracker——這裡不需要、也不該去查詢或建立 change tracker 對這個實體的追蹤。</summary>
    private Task RevertClaimAsync(long contentId, CancellationToken cancellationToken) =>
        dbContext.MessageContents
            .Where(c => c.Id == contentId && c.DownloadStatus == DownloadStatus.Downloading)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DownloadStatus, DownloadStatus.Pending), cancellationToken);

    private static async Task WriteContentSqlServerAsync(
        DbConnection connection, long contentId, Stream content, CancellationToken cancellationToken)
    {
        await using var command = (SqlCommand)connection.CreateCommand();
        command.CommandText = "UPDATE MessageContents SET Content = @content WHERE Id = @id";

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
            init.CommandText = "UPDATE MessageContents SET Content = zeroblob(@length) WHERE Id = @id";
            AddParameter(init, "@length", contentLength);
            AddParameter(init, "@id", contentId);
            await init.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var blob = new SqliteBlob(connection, "MessageContents", "Content", contentId);
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
        var entity = await dbContext.MessageContents.FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.DownloadStatus = DownloadStatus.Failed;
        entity.FailedAttempts++;
        entity.LastAttemptAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
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
