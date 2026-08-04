using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class ContentDownloadService(
    IContentDownloadQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ContentDownloadOptions> options,
    ILogger<ContentDownloadService> logger) : BackgroundService
{
    private readonly ContentDownloadOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動接續失敗（例如 DB 還沒就緒）不能讓例外冒出 ExecuteAsync，
        // 否則 BackgroundServiceExceptionBehavior.StopHost 會關掉整個服務；
        // 漏掉的 Pending 會在下次服務重啟時再被撈回
        try
        {
            await RequeuePendingAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to requeue pending downloads at startup");
        }

        await foreach (var contentId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(contentId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error processing message content {MessageContentId}", contentId);
            }
        }
    }

    public async Task RequeuePendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        // Failed 也一併重排：常見成因是設定錯誤（例如 access token 打錯）而非內容本身有問題，
        // 修好設定重啟服務後應該自動補跑，不需要手動改 DB
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
            queue.Enqueue(content.Id);
        }

        if (failedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (contents.Count > 0)
        {
            logger.LogInformation(
                "Requeued {Count} content downloads from previous run ({FailedCount} previously failed)",
                contents.Count, failedCount);
        }
    }

    public async Task ProcessAsync(long messageContentId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var contentClient = scope.ServiceProvider.GetRequiredService<ILineContentClient>();

        var content = await dbContext.MessageContents
            .Include(c => c.GroupMessage)
            .FirstOrDefaultAsync(c => c.Id == messageContentId, cancellationToken);

        if (content?.GroupMessage is null || content.DownloadStatus != DownloadStatus.Pending)
        {
            return;
        }

        var lineMessageId = content.GroupMessage.LineMessageId;

        // 影片與語音在 LINE 端都要等轉檔完成才能下載原檔，圖片/檔案不需要
        if (content.GroupMessage.MessageType is "video" or "audio")
        {
            var transcoded = await WaitForTranscodingAsync(contentClient, lineMessageId, cancellationToken);
            if (!transcoded)
            {
                logger.LogWarning("Transcoding did not succeed for message {LineMessageId}, marking content {MessageContentId} as Failed",
                    lineMessageId, messageContentId);
                content.DownloadStatus = DownloadStatus.Failed;
                await dbContext.SaveChangesAsync(cancellationToken);
                return;
            }
        }

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var result = await contentClient.GetContentAsync(lineMessageId, cancellationToken);
                content.Content = result.Content;
                content.ContentType = result.ContentType;
                content.DownloadStatus = DownloadStatus.Completed;
                content.CompletedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Downloaded content {MessageContentId} for message {LineMessageId} ({Bytes} bytes, {ContentType})",
                    messageContentId, lineMessageId, result.Content.Length, result.ContentType);
                return;
            }
            // 停機取消不是下載失敗：往外拋讓內容維持 Pending，重啟後由啟動接續補跑
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Download attempt {Attempt}/{MaxRetries} failed for message content {MessageContentId}",
                    attempt, _options.MaxRetries, messageContentId);

                if (attempt < _options.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.RetryDelayMilliseconds * attempt), cancellationToken);
                }
            }
        }

        logger.LogError("All {MaxRetries} download attempts failed for message content {MessageContentId}, marking as Failed",
            _options.MaxRetries, messageContentId);
        content.DownloadStatus = DownloadStatus.Failed;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> WaitForTranscodingAsync(ILineContentClient contentClient, string lineMessageId, CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < _options.TranscodingMaxPolls; poll++)
        {
            var status = await contentClient.GetTranscodingStatusAsync(lineMessageId, cancellationToken);
            switch (status)
            {
                case TranscodingStatus.Succeeded:
                    return true;
                case TranscodingStatus.Failed:
                    return false;
                default:
                    await Task.Delay(TimeSpan.FromSeconds(_options.TranscodingPollSeconds), cancellationToken);
                    break;
            }
        }

        return false;
    }
}
