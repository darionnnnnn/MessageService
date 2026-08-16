using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class RetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    /// <summary>一次刪除的筆數上限——保留期到期時可能一次要清掉數十萬列，含 CASCADE 帶走的
    /// varbinary(max) 內容可達數十 GB。單一交易砍整批會讓交易紀錄檔暴增、GroupMessages
    /// 整表被鎖住數分鐘，期間 webhook 落地與檢視端全部卡住。分批跑，批次之間讓路給其他查詢。</summary>
    private const int BatchSize = 1000;

    private static readonly TimeSpan DelayBetweenBatches = TimeSpan.FromMilliseconds(200);

    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation("Next retention cleanup scheduled at {NextRun:yyyy-MM-dd HH:mm}", DateTime.Now + delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 清除失敗（例如 DB 暫時斷線）不能讓例外冒出 ExecuteAsync，
            // 否則預設的 BackgroundServiceExceptionBehavior.StopHost 會關掉整個服務
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention cleanup failed; will retry at next scheduled run");
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.Now;
        var todayRun = now.Date + _options.CleanupTimeOfDay;
        var nextRun = todayRun > now ? todayRun : todayRun.AddDays(1);
        return nextRun - now;
    }

    public async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var storedRetentionDays = await GetRetentionDaysAsync(dbContext, cancellationToken);

        // 這是不可逆的硬刪除，所以不能無條件相信資料庫讀回來的值：0 會讓 cutoff 等於現在、
        // 負數會讓 cutoff 落在未來——兩者都等於「把整個資料庫清空」。目前所有第一方寫入路徑
        // 都會帶合法值（API 端有範圍驗證、EF 種子與屬性初始設定式都是 1095），但 SQL Server
        // migration 上這個欄位的 DB-level DEFAULT 是 0，只要有任何一次非 EF 的 insert
        // （手動 SQL、還原舊備份後補欄位、日後新增的寫入點）就會踩到。夾擠的成本是一行，
        // 換掉的是「資料全刪且無法復原」。
        var retentionDays = Math.Clamp(storedRetentionDays, 1, ViewerSettings.MaxRetentionDays);
        if (retentionDays != storedRetentionDays)
        {
            logger.LogWarning(
                "ViewerSettings.RetentionDays is {Stored}, which is outside the allowed range 1..{Max}; "
                + "clamped to {Clamped} days for this run",
                storedRetentionDays, ViewerSettings.MaxRetentionDays, retentionDays);
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        var totalDeleted = 0;
        while (true)
        {
            // 先撈這一批要刪的 Id，再用 Id 清單下 ExecuteDeleteAsync——ExecuteDeleteAsync
            // 本身不穩定支援 OrderBy+Take 直接轉譯成單一 SQL，分兩步在兩個 provider 上都可靠
            var idsToDelete = await dbContext.GroupMessages
                .Where(m => m.EventTimestamp < cutoff)
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .Select(m => m.Id)
                .ToListAsync(cancellationToken);

            if (idsToDelete.Count == 0)
            {
                break;
            }

            var deletedCount = await dbContext.GroupMessages
                .Where(m => idsToDelete.Contains(m.Id))
                .ExecuteDeleteAsync(cancellationToken);
            totalDeleted += deletedCount;

            if (idsToDelete.Count < BatchSize)
            {
                break;
            }

            await Task.Delay(DelayBetweenBatches, cancellationToken);
        }

        if (totalDeleted > 0)
        {
            await RefreshStaleGroupPointersAsync(dbContext, cutoff, cancellationToken);
        }

        logger.LogInformation(
            "Retention cleanup removed {Count} group messages older than {Cutoff:yyyy-MM-dd} (retention: {RetentionDays} days)",
            totalDeleted, cutoff, retentionDays);

        if (totalDeleted > 0 && dbContext.Database.IsSqlite())
        {
            try
            {
                var dbPath = dbContext.Database.GetDbConnection().DataSource;
                var sizeMb = new FileInfo(dbPath).Length / (1024.0 * 1024.0);
                logger.LogWarning(
                    "保留期清除已刪除 {Count} 筆訊息，但 SQLite 不會自動回收磁碟空間（目前資料庫檔案大小：{SizeMb:F2} MB），若需釋放空間請人工執行 VACUUM。",
                    totalDeleted, sizeMb);
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "保留期清除已刪除 {Count} 筆訊息，但 SQLite 不會自動回收磁碟空間，若需釋放空間請人工執行 VACUUM。",
                    totalDeleted);
            }
        }
    }

    /// <summary>側欄改讀 Groups.LastMessageId／LastMessageAt（見 GroupsController）——這裡清完
    /// 之後，任何「記錄的最後一則訊息時間早於 cutoff」的群組，那則訊息一定已經被上面的批次
    /// 刪除掃到了，指標已經失效，要重新算一次真正的最後一則（Groups 表只有幾十列，
    /// 逐群組一次 MAX 查詢可接受）；訊息全被清空的群組兩欄設回 null。
    /// LastMessageAt ≥ cutoff 的群組完全沒被這輪清除動到，不用查。</summary>
    private static async Task RefreshStaleGroupPointersAsync(
        MessageDbContext dbContext, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var staleGroupIds = await dbContext.Groups
            .Where(g => g.LastMessageAt != null && g.LastMessageAt < cutoff)
            .Select(g => g.GroupId)
            .ToListAsync(cancellationToken);

        foreach (var groupId in staleGroupIds)
        {
            var latest = await dbContext.GroupMessages
                .Where(m => m.GroupId == groupId)
                .OrderByDescending(m => m.Id)
                .Select(m => new { m.Id, m.EventTimestamp })
                .FirstOrDefaultAsync(cancellationToken);

            var latestId = latest?.Id;
            var latestAt = latest?.EventTimestamp;

            await dbContext.Groups
                .Where(g => g.GroupId == groupId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.LastMessageId, latestId)
                    .SetProperty(g => g.LastMessageAt, latestAt),
                    cancellationToken);
        }
    }

    private static async Task<int> GetRetentionDaysAsync(MessageDbContext dbContext, CancellationToken cancellationToken)
    {
        var retentionDays = await dbContext.ViewerSettings
            .Where(v => v.Id == ViewerSettings.SingletonId)
            .Select(v => (int?)v.RetentionDays)
            .FirstOrDefaultAsync(cancellationToken);
        return retentionDays ?? ViewerSettings.DefaultRetentionDays;
    }
}
