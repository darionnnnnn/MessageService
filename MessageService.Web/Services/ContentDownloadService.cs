using System.Collections.Concurrent;
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

    /// <summary>每個 messageContentId 目前查了幾次轉檔狀態——單例服務的生命週期內有效，行程
    /// 重啟會歸零，等同重新給滿額度：重啟後由 RequeuePendingAsync 撈回，語意跟現狀一致，
    /// 不需要跨重啟持久化。查詢達到 Succeeded／Failed 或門檻上限時務必移除，防止洩漏。</summary>
    private readonly ConcurrentDictionary<long, int> _transcodingPollCounts = new();

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

        // 多個 worker 共讀同一個 Channel（ChannelReader 天生支援多讀者，每筆項目只會被
        // 其中一個 worker 拿到）：一支大檔案要等轉檔的期間，其他 worker 仍能繼續處理
        // 排在後面的圖片/檔案，不會被整條佇列卡住
        var workerCount = Math.Max(1, _options.MaxConcurrency);
        var workers = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var contentId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(contentId, stoppingToken);
                }
                // 只有「真的在停機」才讓例外往外走結束這個 worker。HttpClient 逾時丟的是
                // TaskCanceledException（OperationCanceledException 的子類別）但 stoppingToken
                // 並沒有被取消——早期版本用 `when (ex is not OperationCanceledException)` 過濾，
                // 那種逾時會直接穿過 catch 讓 worker 靜默結束、再也不讀 Channel：單 worker 時代
                // 會立刻炸掉 host（吵但看得見），改成多 worker 之後卻變成並行度默默從 3 掉到 2，
                // 維運端完全看不出徵兆。判斷依據因此改看 stoppingToken 本身，不看例外型別。
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error processing message content {MessageContentId}", contentId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停機
        }
        catch (Exception ex)
        {
            // 走到這裡代表 Channel 本身壞了（而不是單筆內容失敗），這個 worker 收攤了；
            // 一定要留下紀錄，不然並行度悄悄下降沒有人會發現
            logger.LogCritical(ex, "Content download worker exited unexpectedly and will no longer process downloads");
            throw;
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
            _transcodingPollCounts.TryRemove(messageContentId, out _);
            return;
        }

        // 影片與語音在 LINE 端都要等轉檔完成才能下載原檔，圖片/檔案不需要
        if (item.MessageType is "video" or "audio")
        {
            var outcome = await CheckTranscodingAsync(contentClient, messageContentId, item.LineMessageId, cancellationToken);
            switch (outcome)
            {
                case TranscodingCheckOutcome.StillProcessing:
                    // 查一次就回去服務佇列裡下一個項目，不要睡在原地等——這支還在轉檔的期間，
                    // worker 才能繼續處理排在後面的圖片/檔案，見 IContentDownloadQueue.EnqueueDelayed
                    queue.EnqueueDelayed(messageContentId, TimeSpan.FromSeconds(_options.TranscodingPollSeconds), cancellationToken);
                    return;
                case TranscodingCheckOutcome.Failed:
                    _transcodingPollCounts.TryRemove(messageContentId, out _);
                    await workSource.FailAsync(messageContentId, cancellationToken);
                    return;
                case TranscodingCheckOutcome.Succeeded:
                    _transcodingPollCounts.TryRemove(messageContentId, out _);
                    break; // 繼續往下走下載邏輯
            }
        }

        if (item.MessageType == "sticker" && item.StickerId == null)
        {
            logger.LogWarning("Message content {MessageContentId} has MessageType 'sticker' but null StickerId, marking as Failed", messageContentId);
            await workSource.FailAsync(messageContentId, cancellationToken);
            return;
        }

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                await using var result = item.MessageType == "sticker"
                    ? await contentClient.GetStickerAsync(item.StickerId!, cancellationToken)
                    : await contentClient.GetContentAsync(item.LineMessageId, cancellationToken);
                var bytesWritten = await CompleteFromResultAsync(workSource, messageContentId, result, cancellationToken);
                logger.LogInformation("Downloaded content {MessageContentId} for message {LineMessageId} ({Bytes} bytes, {ContentType})",
                    messageContentId, item.LineMessageId, bytesWritten, result.ContentType);
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

    /// <summary>LINE 的回應通常帶 Content-Length，直接串流交給 workSource。少數沒帶的情況下
    /// （SQLite 的分塊寫入需要預知總長度）先落暫存檔量出實際長度，量完就砍掉，不佔用磁碟。</summary>
    private static async Task<long> CompleteFromResultAsync(
        IContentWorkSource workSource, long messageContentId, LineContentResult result, CancellationToken cancellationToken)
    {
        if (result.ContentLength is { } knownLength)
        {
            await workSource.CompleteAsync(messageContentId, result.Content, knownLength, result.ContentType, cancellationToken);
            return knownLength;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"msgsvc-content-{Guid.NewGuid():N}.tmp");
        await using (var tempFile = new FileStream(
            tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous))
        {
            await result.Content.CopyToAsync(tempFile, cancellationToken);
            var length = tempFile.Length;
            tempFile.Position = 0;
            await workSource.CompleteAsync(messageContentId, tempFile, length, result.ContentType, cancellationToken);
            return length;
        }
    }

    private enum TranscodingCheckOutcome
    {
        Succeeded,
        Failed,
        StillProcessing,
    }

    /// <summary>只查一次，不在這裡迴圈等待——還在處理中時交給呼叫端用 EnqueueDelayed
    /// 延遲重排，worker 才能立刻回去服務佇列裡的下一個項目（見類別註解／ProcessAsync）。</summary>
    private async Task<TranscodingCheckOutcome> CheckTranscodingAsync(
        ILineContentClient contentClient, long messageContentId, string lineMessageId, CancellationToken cancellationToken)
    {
        var status = await contentClient.GetTranscodingStatusAsync(lineMessageId, cancellationToken);
        switch (status)
        {
            case TranscodingStatus.Succeeded:
                return TranscodingCheckOutcome.Succeeded;
            case TranscodingStatus.Failed:
                logger.LogWarning(
                    "Transcoding failed for message {LineMessageId}, marking content {MessageContentId} as Failed",
                    lineMessageId, messageContentId);
                return TranscodingCheckOutcome.Failed;
            default:
                var pollCount = _transcodingPollCounts.AddOrUpdate(messageContentId, 1, static (_, count) => count + 1);
                if (pollCount >= _options.TranscodingMaxPolls)
                {
                    logger.LogWarning(
                        "Transcoding did not succeed for message {LineMessageId} after {MaxPolls} polls, marking content {MessageContentId} as Failed",
                        lineMessageId, _options.TranscodingMaxPolls, messageContentId);
                    return TranscodingCheckOutcome.Failed;
                }
                return TranscodingCheckOutcome.StillProcessing;
        }
    }
}
