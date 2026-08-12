using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>Full／Db 模式用：ContentDownloadService.RequeuePendingAsync／ProcessAsync 原本
/// 直接開 scope 拿 MessageDbContext 的那段邏輯搬過來，行為刻意保持一致。</summary>
public class DbContentWorkSource(MessageDbContext dbContext) : IContentWorkSource
{
    public async Task<IReadOnlyList<long>> GetPendingIdsAsync(CancellationToken cancellationToken)
    {
        var contents = await dbContext.MessageContents
            .Where(c => c.DownloadStatus == DownloadStatus.Pending || c.DownloadStatus == DownloadStatus.Failed)
            .ToListAsync(cancellationToken);

        var failedCount = 0;
        foreach (var content in contents)
        {
            if (content.DownloadStatus == DownloadStatus.Failed)
            {
                content.DownloadStatus = DownloadStatus.Pending;
                failedCount++;
            }
        }

        if (failedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return contents.Select(c => c.Id).ToList();
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

    public async Task CompleteAsync(long contentId, byte[] content, string? contentType, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MessageContents.FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Content = content;
        entity.ContentType = contentType;
        entity.DownloadStatus = DownloadStatus.Completed;
        entity.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(long contentId, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MessageContents.FirstOrDefaultAsync(c => c.Id == contentId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.DownloadStatus = DownloadStatus.Failed;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
