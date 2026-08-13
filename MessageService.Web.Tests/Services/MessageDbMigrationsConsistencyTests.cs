using MessageService.Data;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Tests.Services;

// 守門測試：兩個 provider 各自的 migrations 集合都要跟目前的 EF model 完全同步——
// 改了 MessageDbContext 的模型卻忘了幫兩邊都跑 dotnet ef migrations add，這裡就會紅燈，
// 不必等到真的部署到某個 provider 才發現漏了一邊。HasPendingModelChanges() 是純粹的
// model 比對，不需要真的連得上資料庫（SqlServer 這邊本機也沒有真的實例可連）。
public class MessageDbMigrationsConsistencyTests
{
    [Fact]
    public void Sqlite_Migrations_HaveNoPendingModelChanges()
    {
        var options = new DbContextOptionsBuilder<SqliteMessageDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new SqliteMessageDbContext(options);

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }

    [Fact]
    public void SqlServer_Migrations_HaveNoPendingModelChanges()
    {
        var options = new DbContextOptionsBuilder<SqlServerMessageDbContext>()
            .UseSqlServer("Server=(local);Database=consistency-check-only;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        using var dbContext = new SqlServerMessageDbContext(options);

        Assert.False(dbContext.Database.HasPendingModelChanges());
    }
}
