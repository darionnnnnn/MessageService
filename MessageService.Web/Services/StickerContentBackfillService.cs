using System.Diagnostics;
using MessageService.Data;
using MessageService.Models;
using MessageService.Services;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class StickerContentBackfillService(
    IServiceScopeFactory scopeFactory,
    IContentDownloadQueue downloadQueue,
    ILogger<StickerContentBackfillService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunBackfillAsync(stoppingToken);

    public async Task RunBackfillAsync(CancellationToken stoppingToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

            // 開始分批掃描之前先以 AnyAsync 判斷有無待補資料，避免每次啟動都做無索引的反連結全表掃描
            var hasPending = await dbContext.GroupMessages
                .AnyAsync(m => m.MessageType == "sticker"
                            && m.StickerId != null
                            && m.Content == null, stoppingToken);

            if (!hasPending)
            {
                stopwatch.Stop();
                logger.LogDebug("No pending sticker content rows to backfill ({ElapsedMilliseconds} ms).", stopwatch.ElapsedMilliseconds);
                return;
            }

            long lastProcessedId = 0;
            const int batchSize = 500;
            int totalCreated = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                // 用 Id 當游標往前推（keyset），不是 Skip/Take：每批都會把撈到的訊息補上 Content 列，
                // 下一輪的 m.Content == null 條件就不再命中它們，用偏移量分頁反而會跳過資料
                var messages = await dbContext.GroupMessages
                    .Where(m => m.Id > lastProcessedId
                             && m.MessageType == "sticker"
                             && m.StickerId != null
                             && m.Content == null)
                    .OrderBy(m => m.Id)
                    .Take(batchSize)
                    .ToListAsync(stoppingToken);

                if (messages.Count == 0)
                {
                    break;
                }

                var createdContents = new List<MessageContent>(messages.Count);
                foreach (var m in messages)
                {
                    var content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Pending
                    };
                    m.Content = content;
                    createdContents.Add(content);
                }

                var batchLastId = messages[^1].Id;

                try
                {
                    await dbContext.SaveChangesAsync(stoppingToken);

                    totalCreated += createdContents.Count;
                    lastProcessedId = batchLastId;

                    // 存檔後 EF Core 會回填自增的主鍵 Id，在清空 ChangeTracker 前把 Id 入列到 IContentDownloadQueue。
                    // 註：在「本機不下載、由另一台主機下載」的拆機部署下，本機的佇列是 Null 實作，入列是無操作；
                    // 那種拓撲靠 ContentDownloadService 的週期重掃（設定項 RequeueIntervalMinutes）收回。
                    foreach (var content in createdContents)
                    {
                        downloadQueue.Enqueue(content.Id);
                    }

                    // 分批的意義是「同時間只有一批在記憶體裡」，但變更追蹤器會累積每一批的實體，
                    // 舊訊息可能有數萬則、跑上百批，不清掉的話記憶體與後續 SaveChanges 的
                    // DetectChanges 成本會隨批次數一路墊高。已經存檔完畢，清空是安全的
                    dbContext.ChangeTracker.Clear();
                }
                catch (DbUpdateException ex)
                {
                    // 撞鍵時（如另一台主機／行程已先補好同一批）清空追蹤器並略過該批，游標推至該批末尾繼續下一批
                    dbContext.ChangeTracker.Clear();
                    lastProcessedId = batchLastId;
                    logger.LogInformation(ex, "Sticker content batch up to message ID {LastProcessedId} was already backfilled elsewhere, skipping batch.", lastProcessedId);
                }
            }

            stopwatch.Stop();
            if (totalCreated > 0)
            {
                logger.LogInformation("Backfilled {TotalCreated} pending content rows for existing sticker messages ({ElapsedMilliseconds} ms).", totalCreated, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to backfill sticker content rows.");
        }
    }
}
