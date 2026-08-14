using MessageService.Data;
using MessageService.Models;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Services;

public class StickerContentBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<StickerContentBackfillService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunBackfillAsync(stoppingToken);

    public async Task RunBackfillAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

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

                foreach (var m in messages)
                {
                    m.Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Pending
                    };
                    totalCreated++;
                    lastProcessedId = m.Id;
                }

                await dbContext.SaveChangesAsync(stoppingToken);

                // 分批的意義是「同時間只有一批在記憶體裡」，但變更追蹤器會累積每一批的實體，
                // 舊訊息可能有數萬則、跑上百批，不清掉的話記憶體與後續 SaveChanges 的
                // DetectChanges 成本會隨批次數一路墊高。已經存檔完畢，清空是安全的
                dbContext.ChangeTracker.Clear();
            }

            if (totalCreated > 0)
            {
                logger.LogInformation("Backfilled {TotalCreated} pending content rows for existing sticker messages.", totalCreated);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to backfill sticker content rows.");
        }
    }
}
