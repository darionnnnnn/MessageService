using MessageService.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Services;

/// <summary>AllInOne 主機設定要用 SQL Server 時，啟動當下先驗證連得上、schema 也對——探測失敗
/// 時呼叫端（Program.cs）會改用本機 SQLite 撐起服務，見該處對「執行中不動態切換、只在啟動時
/// 決定一次」的說明。AutoMigrate 開啟時一併驗證 schema（用同一套 Migrate()，缺資料表也視為
/// 探測失敗，跟連不上的處理方式一致）；關閉時只驗連線本身——schema 由外部
/// `dotnet ef database update` 管理，缺表是需要人工介入的設定錯誤，不該被救場悄悄蓋過去。
/// 用 Global\MessageService.Migrate 這個具名鎖跟正式 migration 互相排隊，避免兩邊同時對同一顆
/// 資料庫跑 DDL。</summary>
public static class DatabaseStartupProbe
{
    /// <summary>回傳 null 表示探測成功；非 null 是失敗原因（供記錄與救場狀態曝露用）。</summary>
    public static string? TryPrepareSqlServer(string connectionString, bool autoMigrate)
    {
        try
        {
            // 連線逾時夾到 5 秒內：探測失敗是要走救場的，站台啟動被 SqlClient 預設的 15 秒
            // Connect Timeout 拖住沒有意義；連線字串顯式給了更短的值就從其（0＝無限等待，
            // 也一併夾掉）。只影響這次探測——正式 DI 註冊用的仍是原連線字串，不受影響。
            // Connect Timeout 只管建立連線，不影響探測內 Migrate() 的執行時間。
            var probeConnectionString = new SqlConnectionStringBuilder(connectionString);
            if (probeConnectionString.ConnectTimeout == 0 || probeConnectionString.ConnectTimeout > 5)
            {
                probeConnectionString.ConnectTimeout = 5;
            }

            var options = new DbContextOptionsBuilder<SqlServerMessageDbContext>()
                .UseSqlServer(probeConnectionString.ConnectionString)
                .Options;
            using var dbContext = new SqlServerMessageDbContext(options);

            DatabaseMigrationMutex.RunExclusive(() =>
            {
                if (autoMigrate)
                {
                    dbContext.Database.Migrate();
                }
                else if (!dbContext.Database.CanConnect())
                {
                    throw new InvalidOperationException("無法連線到 SQL Server。");
                }
            });

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
