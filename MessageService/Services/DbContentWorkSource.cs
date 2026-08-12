using System.Data;
using System.Data.Common;
using MessageService.Data;
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
/// 改用兩種 provider 各自的串流寫入方式，見該方法說明。</summary>
public class DbContentWorkSource(MessageDbContext dbContext, IOptions<ContentDownloadOptions> options) : IContentWorkSource
{
    private const int BufferSize = 81920;
    private readonly ContentDownloadOptions _options = options.Value;

    public async Task<IReadOnlyList<long>> GetPendingIdsAsync(CancellationToken cancellationToken)
    {
        var pendingIds = await dbContext.MessageContents
            .Where(c => c.DownloadStatus == DownloadStatus.Pending)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        // Failed 只在訊息到達後的保留視窗內、且累計失敗次數未達上限才重新撿回——LINE 的內容
        // 有保存期限，過期的檔案永遠下載不到，不該每次重啟都無限重跑（見 ContentDownloadOptions）。
        // cutoff 比對刻意不下推到 SQL：GroupMessage.ReceivedAt 在 SQLite 上沒有支援範圍比較的
        // DateTimeOffset 轉換（跟 EventTimestamp 不同——這欄位從 InitialCreate 就是預設的文字
        // 格式儲存，現在才加轉換會讓既有 messages.db 的既有資料被讀成亂碼）。Failed 筆數本來
        // 就是小量（下載失敗的內容），先用 FailedAttempts 門檻縮小範圍後在記憶體篩 cutoff
        // 划算得多，不值得為此冒風險改儲存格式。
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.FailedRetryWindowDays);
        var failedCandidates = await dbContext.MessageContents
            .Include(c => c.GroupMessage)
            .Where(c => c.DownloadStatus == DownloadStatus.Failed && c.FailedAttempts < _options.MaxFailedRetries)
            .ToListAsync(cancellationToken);

        var retryableFailed = failedCandidates
            .Where(c => c.GroupMessage is not null && c.GroupMessage.ReceivedAt >= cutoff)
            .ToList();

        foreach (var content in retryableFailed)
        {
            content.DownloadStatus = DownloadStatus.Pending;
        }

        if (retryableFailed.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return pendingIds.Concat(retryableFailed.Select(c => c.Id)).ToList();
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

    /// <summary>blob 寫入先於中繼資料更新：中斷時（例如服務被殺）DownloadStatus 維持原狀，
    /// 下次啟動的 RequeuePendingAsync 會整個重跑，不會留下「狀態是 Completed 但內容是半截」
    /// 的資料。SQL Server 用 SqlParameter 的串流參數（直接把 Stream 指派給 Value，провider
    /// 端會邊讀邊送，不整份進記憶體）；SQLite 沒有這個機制，改用 zeroblob() 先配置定長空間，
    /// 再用 SqliteBlob（Stream 子類別）增量寫入。</summary>
    public async Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (dbContext.Database.IsSqlite())
            {
                await WriteContentSqliteAsync((SqliteConnection)connection, contentId, content, contentLength, cancellationToken);
            }
            else
            {
                await WriteContentSqlServerAsync(connection, contentId, content, cancellationToken);
            }
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
        await dbContext.SaveChangesAsync(cancellationToken);
    }

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
