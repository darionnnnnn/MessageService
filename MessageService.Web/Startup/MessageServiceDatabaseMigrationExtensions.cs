using MessageService.Data;
using MessageService.Outbox;
using MessageService.Services;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Startup;

/// <summary>Program.cs 里純機械式拆出來的結果——不改任何邏輯／順序／條件，只是把 schema
/// migration（含跨行程 mutex 保護、SQLite baseline 橋接、outbox schema 升級）這段搬進這個
/// 檔案。唯一例外是下面 AutoMigrate 判斷式多 OR 了 SqliteFallbackTriggered，見那裡的說明——
/// 這是體檢輪抓到的真 bug，不是原本邏輯的一部分。</summary>
public static class MessageServiceDatabaseMigrationExtensions
{
    public static void MigrateMessageServiceDatabase(
        this WebApplication app, DeploymentCapabilities capabilities, MessageServiceCoreRegistration registration)
    {
        // Database:AutoMigrate=false 的原意是「schema 由外部 dotnet ef database update 管理」——
        // 這個假設只對「設定裡指名的那顆資料庫」成立。SQLite 救場產生的資料庫是執行期才決定
        // 存不存在的，沒有人能預先對它跑外部 migration 工具；救場觸發時如果還是尊重
        // AutoMigrate=false 跳過 migrate，這顆全新的 SQLite 檔案永遠不會有任何資料表，
        // 第一筆寫入就會直接炸掉，等於救場機制在這個設定組合下完全失效。所以救場觸發時
        // 無條件跑 migrate，不受 AutoMigrate 影響（真正的 SQL Server 主資料庫不受影響——
        // 沒觸發救場時這裡的行為跟改之前完全一樣）
        if (capabilities.HasDatabaseAccess
            && (registration.AutoMigrate || registration.DatabaseStartupDecision.SqliteFallbackTriggered))
        {
            using var scope = app.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            var migrationLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            // 同機多站台（或同一台上多個 worker process）同時啟動時，兩邊都跑橋接／Migrate() 有機會
            // 撞在一起：Baseliner 兩邊同時通過「無 __EFMigrationsHistory」檢查後同時 ALTER TABLE／插
            // baseline 紀錄，或 Migrate() 一邊建 __EFMigrationsHistory 表、一邊也在建，其中一邊會直接
            // 炸掉。具名 mutex（DatabaseMigrationMutex，跟啟動時的 SQL Server 探測共用同一把鎖）讓它們
            // 排隊而不是打架——Baseliner 必須也包在裡面（它的偵測→補齊不是原子操作，正是最需要排隊
            // 的那段）。單一實例的情況下這裡幾乎瞬間就能拿到鎖，沒有實際延遲。
            DatabaseMigrationMutex.RunExclusive(
                () =>
                {
                    if (registration.DatabaseProvider == "Sqlite")
                    {
                        // SqliteConnectionString 在這裡必然已解析好：這個區塊由 capabilities.HasDatabaseAccess
                        // 圍住（見上面 AutoMigrate 判斷），跟 AddMessageServiceCore 註冊 DbContext 時賦值的
                        // 條件完全一致
                        LegacySqliteBaseliner.EnsureBaseline(registration.SqliteConnectionString!, migrationLogger);

                        dbContext.Database.Migrate();

                        // Core+Viewer 同機兩站台、或 IIS 重疊回收過渡期都可能有兩個行程同時開 messages.db——
                        // 跟 outbox.db 同一個理由（見 EnableWalMode 說明），持久屬性設一次即可
                        OutboxSchemaUpgrader.EnableWalMode(registration.SqliteConnectionString!);
                    }
                    else
                    {
                        // SQL Server 且救場沒有觸發：啟動探測（DatabaseStartupProbe）多半已經在稍早
                        // 跑過一次 Migrate()，這裡再跑一次是冪等的空操作（確認 schema 版本一致），
                        // 換來的是「DI 註冊要用哪個 provider」跟「migration 到底跑了沒」只有一個
                        // 決定點，不用另外維護一條「探測時已經 migrate 過，這裡跳過」的旁路
                        dbContext.Database.Migrate();
                    }
                },
                onLockUnavailable: () => migrationLogger.LogWarning(
                    "無法取得跨行程 migration 鎖（Global\\MessageService.Migrate，通常是同機另一個應用程式" +
                    "集區身分已建立過這個具名物件）——改為不加鎖執行 migration。單站台部署不受影響；" +
                    "同機多站台情境請確認集區身分一致，或錯開兩者的啟動時間降低競爭視窗。"));
        }

        if (capabilities.ReceivesWebhook)
        {
            using var scope = app.Services.CreateScope();
            var outboxDbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            outboxDbContext.Database.EnsureCreated();

            // EnsureCreated() 只在資料庫檔案完全不存在時建表——既有的 outbox.db 補上新增的
            // DeadLetteredAt 欄位要在這裡另外處理，見 OutboxSchemaUpgrader 的說明。
            // OutboxConnectionString 在這裡必然已解析好：這個區塊跟 AddMessageServiceCore 賦值時的
            // capabilities.ReceivesWebhook 條件完全一致
            OutboxSchemaUpgrader.EnsureDeadLetterColumn(registration.OutboxConnectionString!);

            // 既有 outbox.db 可能已經因為 LINE redelivery 累積了重複 WebhookEventId（P0）——
            // 必須先去重才能補建唯一索引，見 EnsureWebhookEventIdUniqueIndex 說明
            OutboxSchemaUpgrader.EnsureWebhookEventIdUniqueIndex(registration.OutboxConnectionString!);

            // webhook 執行緒寫、forwarder 執行緒讀刪；rollback journal 模式下兩邊會互相 block
            // （busy_timeout 預設 30 秒，遠超 LINE 的 webhook 逾時），WAL 讓讀寫不互相阻塞
            OutboxSchemaUpgrader.EnableWalMode(registration.OutboxConnectionString!);

            // 死信不會自動消失，只會在 OutboxForwarderService 的 log 被看到（啟動時先報一次、
            // 之後每小時再報一次）——沒有專用的重送介面，量大時要靠那行 log 提醒維運人員去查
        }
    }
}
