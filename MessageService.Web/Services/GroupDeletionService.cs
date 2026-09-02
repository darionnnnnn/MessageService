using MessageService.Data;
using MessageService.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MessageService.Web.Services;

public sealed record GroupMessageDeletionResult(int MessageCount);

public sealed record GroupDeletionResult(
    int MessageCount, int MemberCount, int AnonymousIdentityCount, int MaskKeywordScopeCount);

/// <summary>
/// 提供群組歷史訊息刪除與群組本體刪除服務。
/// 分批刪除以避免長交易鎖表，刪除後視需要清理孤兒 Blob、重置指標與使遮蔽快取失效。
/// </summary>
public class GroupDeletionService(
    MessageDbContext dbContext,
    IMaskingService maskingService,
    ILogger<GroupDeletionService> logger)
{
    private const int BatchSize = 1000;
    private static readonly TimeSpan DelayBetweenBatches = TimeSpan.FromMilliseconds(200);

    /// <summary>刪除指定群組的全部歷史訊息，群組本體與成員快取保留。</summary>
    public async Task<GroupMessageDeletionResult?> DeleteMessagesAsync(string groupId, CancellationToken ct)
    {
        var exists = await dbContext.Groups.AnyAsync(g => g.GroupId == groupId, ct);
        if (!exists)
        {
            return null;
        }

        var messageCount = await DeleteMessagesCoreAsync(groupId, clearPointers: true, ct);

        logger.LogWarning("Deleted {Count} historical message(s) for group {GroupId}", messageCount, groupId);
        LogSqliteSpaceWarningIfApplicable(messageCount, "刪除歷史訊息");

        return new GroupMessageDeletionResult(messageCount);
    }

    /// <summary>刪除指定群組本體及其所有相關資料（歷史訊息、成員快取、匿名代號、關鍵字遮蔽範圍列）。</summary>
    public async Task<GroupDeletionResult?> DeleteGroupAsync(string groupId, CancellationToken ct)
    {
        var exists = await dbContext.Groups.AnyAsync(g => g.GroupId == groupId, ct);
        if (!exists)
        {
            return null;
        }

        var messageCount = await DeleteMessagesCoreAsync(groupId, clearPointers: false, ct);

        var memberCount = await dbContext.GroupMembers
            .Where(m => m.GroupId == groupId)
            .ExecuteDeleteAsync(ct);

        var anonymousIdentityCount = await dbContext.AnonymousIdentities
            .Where(a => a.GroupId == groupId)
            .ExecuteDeleteAsync(ct);

        var maskKeywordScopeCount = await dbContext.MaskKeywordGroups
            .Where(g => g.GroupId == groupId)
            .ExecuteDeleteAsync(ct);

        await dbContext.Groups
            .Where(g => g.GroupId == groupId)
            .ExecuteDeleteAsync(ct);

        if (maskKeywordScopeCount > 0)
        {
            maskingService.InvalidateCache();
        }

        logger.LogWarning(
            "Deleted group {GroupId}: {MessageCount} message(s), {MemberCount} member(s), {AnonymousCount} anonymous identity/identities, {MaskCount} mask keyword scope(s)",
            groupId, messageCount, memberCount, anonymousIdentityCount, maskKeywordScopeCount);

        LogSqliteSpaceWarningIfApplicable(messageCount, "刪除群組");

        return new GroupDeletionResult(messageCount, memberCount, anonymousIdentityCount, maskKeywordScopeCount);
    }

    /// <summary>分批刪除群組訊息、依需要清空指標，並在結尾清理孤兒 Blob。</summary>
    private async Task<int> DeleteMessagesCoreAsync(string groupId, bool clearPointers, CancellationToken ct)
    {
        var totalDeleted = 0;
        while (true)
        {
            var idsToDelete = await dbContext.GroupMessages
                .Where(m => m.GroupId == groupId)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .Select(m => m.Id)
                .ToListAsync(ct);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            var deletedCount = await dbContext.GroupMessages
                .Where(m => idsToDelete.Contains(m.Id))
                .ExecuteDeleteAsync(ct);
            totalDeleted += deletedCount;

            if (idsToDelete.Count < BatchSize)
            {
                break;
            }

            await Task.Delay(DelayBetweenBatches, ct);
        }

        if (clearPointers)
        {
            await dbContext.Groups
                .Where(g => g.GroupId == groupId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.LastMessageId, (long?)null)
                    .SetProperty(g => g.LastMessageAt, (DateTimeOffset?)null),
                    ct);
        }

        await DeleteOrphanBlobsAsync(ct);

        return totalDeleted;
    }

    /// <summary>刪除沒有對應 MessageContent 列的孤兒 MessageContentBlob。</summary>
    private async Task DeleteOrphanBlobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await dbContext.MessageContentBlobs
                .Where(b => !dbContext.MessageContents.Any(c => c.Id == b.MessageContentId))
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted > 0)
            {
                logger.LogWarning(
                    "Deleted {Count} orphaned MessageContentBlob(s) with no parent MessageContent row; " +
                    "cascade should have removed these — investigate whether DB-level cascade is working correctly",
                    deleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端真的取消了才往外丟；HttpClient/DB 逾時的 TaskCanceledException 走下面的一般 catch
            throw;
        }
        catch (Exception ex)
        {
            // 孤兒回收是補強措施，失敗不可連累刪除主流程
            logger.LogError(ex, "Orphaned blob cleanup failed; group deletion will continue");
        }
    }

    /// <summary>若使用 SQLite 且有刪除訊息，記錄磁碟空間回收提醒。</summary>
    private void LogSqliteSpaceWarningIfApplicable(int deletedCount, string operation)
    {
        if (deletedCount <= 0 || !dbContext.Database.IsSqlite())
        {
            return;
        }

        try
        {
            var dbPath = dbContext.Database.GetDbConnection().DataSource;
            var sizeMb = new FileInfo(dbPath).Length / (1024.0 * 1024.0);
            logger.LogWarning(
                "{Operation}已刪除 {Count} 筆訊息，但 SQLite 不會自動回收磁碟空間（目前資料庫檔案大小：{SizeMb:F2} MB），若需釋放空間請人工執行 VACUUM。",
                operation, deletedCount, sizeMb);
        }
        catch (Exception)
        {
            logger.LogWarning(
                "{Operation}已刪除 {Count} 筆訊息，但 SQLite 不會自動回收磁碟空間，若需釋放空間請人工執行 VACUUM。",
                operation, deletedCount);
        }
    }
}
