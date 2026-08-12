using MessageService.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>資料存取全部透過 IContentWorkSource（Full／Db 模式查本機 DB，Line 模式打
/// ingest API）——這裡只保留下載重試／轉檔等待的流程本身，不直接碰任何資料庫。</summary>
public class ContentDownloadService(
    IContentDownloadQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ContentDownloadOptions> options,
    ILogger<ContentDownloadService> logger) : BackgroundService
{
    private readonly ContentDownloadOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 啟動接續失敗（例如 DB／ingest API 還沒就緒）不能讓例外冒出 ExecuteAsync，
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
        var workSource = scope.ServiceProvider.GetRequiredService<IContentWorkSource>();

        var pendingIds = await workSource.GetPendingIdsAsync(cancellationToken);
        foreach (var contentId in pendingIds)
        {
            queue.Enqueue(contentId);
        }

        if (pendingIds.Count > 0)
        {
            logger.LogInformation("Requeued {Count} content downloads from previous run", pendingIds.Count);
        }
    }

    public async Task ProcessAsync(long messageContentId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var workSource = scope.ServiceProvider.GetRequiredService<IContentWorkSource>();
        var contentClient = scope.ServiceProvider.GetRequiredService<ILineContentClient>();

        var item = await workSource.GetAsync(messageContentId, cancellationToken);
        if (item is null)
        {
            return;
        }

        // 影片與語音在 LINE 端都要等轉檔完成才能下載原檔，圖片/檔案不需要
        if (item.MessageType is "video" or "audio")
        {
            var transcoded = await WaitForTranscodingAsync(contentClient, item.LineMessageId, cancellationToken);
            if (!transcoded)
            {
                logger.LogWarning("Transcoding did not succeed for message {LineMessageId}, marking content {MessageContentId} as Failed",
                    item.LineMessageId, messageContentId);
                await workSource.FailAsync(messageContentId, cancellationToken);
                return;
            }
        }

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var result = await contentClient.GetContentAsync(item.LineMessageId, cancellationToken);
                await workSource.CompleteAsync(messageContentId, result.Content, result.ContentType, cancellationToken);
                logger.LogInformation("Downloaded content {MessageContentId} for message {LineMessageId} ({Bytes} bytes, {ContentType})",
                    messageContentId, item.LineMessageId, result.Content.Length, result.ContentType);
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
        await workSource.FailAsync(messageContentId, cancellationToken);
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
