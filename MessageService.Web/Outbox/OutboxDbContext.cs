using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MessageService.Outbox;

/// <summary>收錄端本機的持久化佇列，永遠是 SQLite（這是本機緩衝，不是共用資料庫，
/// 不需要支援 SQL Server）。跟 MessageService.Data 的 MessageDbContext 完全獨立，
/// 兩者互不相依——outbox 排空失敗不該卡住任何跟主資料庫有關的邏輯，反之亦然。</summary>
public class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    public DbSet<OutboxEntry> Entries => Set<OutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxEntry>(entity =>
        {
            // 排空時要挑「到期可處理」的項目，SQLite 的 DateTimeOffset 只支援相等比較，
            // 不支援 </>／range，跟 MessageDbContext 對 EventTimestamp 的處理是同一個原因、同一種修法
            entity.Property(e => e.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            entity.Property(e => e.NextAttemptAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            entity.Property(e => e.DeadLetteredAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            entity.HasIndex(e => e.NextAttemptAt);
            // LINE redelivery 送同一個 WebhookEventId 兩次是預期行為，靠 SqliteOutboxWriter 撞鍵
            // 判定「已在佇列中」擋掉重複列——只對全新檔案生效，既有 outbox.db 由
            // OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex 補建
            entity.HasIndex(e => e.WebhookEventId).IsUnique();
        });
    }
}

public static class OutboxQueryExtensions
{
    /// <summary>尚待落地的項目：只排除死信。這是兩個消費端（推送轉發器、Core 的拉取）
    /// **唯一**共用的條件。</summary>
    public static IQueryable<OutboxEntry> WhereDeliverable(this IQueryable<OutboxEntry> query) =>
        query.Where(e => e.DeadLetteredAt == null);

    /// <summary>推送轉發器要送的項目：待落地、且退避時間已到。
    ///
    /// <b>NextAttemptAt 是推送方向專屬的概念</b>，拉取端不可以套用——那是「上次推給 Core
    /// 失敗、隔多久再推」的排程。防火牆只開通 core→edge 時推送本來就會一直失敗，退避會把
    /// NextAttemptAt 一路推到 MaxRetryDelaySeconds（預設 300 秒）之後，若拉取端也照著過濾，
    /// Core 就會有長達五分鐘查不到任何訊息——訊息其實好端端躺在 outbox 裡。</summary>
    public static IQueryable<OutboxEntry> WherePushDue(this IQueryable<OutboxEntry> query, DateTimeOffset now) =>
        query
            .WhereDeliverable()
            .Where(e => e.NextAttemptAt == null || e.NextAttemptAt <= now);
}
