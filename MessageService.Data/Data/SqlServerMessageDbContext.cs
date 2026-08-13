using MessageService.Data.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MessageService.Data;

/// <summary>跟 <see cref="MessageDbContext"/> 完全一樣，唯一的存在理由是讓 EF Core 的
/// migrations 工具能區分「這是 SQL Server 的 migrations 集合」——不新增任何成員、不覆寫
/// OnModelCreating，模型建構邏輯全部沿用基底類別。見 Data/Migrations/SqlServer/
/// （這批是既有 messages.db 正式環境一路用 dotnet ef database update 建起來的 migrations，
/// 搬過來時 migration Id 刻意不變，既有資料庫的 __EFMigrationsHistory 比對不受影響）。</summary>
public class SqlServerMessageDbContext(DbContextOptions<SqlServerMessageDbContext> options, FieldCipher? cipher = null)
    : MessageDbContext(options, cipher);

/// <summary>只給 `dotnet ef migrations add -c SqlServerMessageDbContext` 這類設計期工具用——
/// 連線字串是假的，不會真的連線，只是讓 EF 知道要用哪個 provider 建立設計期模型。
/// 執行期一律透過 Program.cs 的 DI 走真正的連線字串。</summary>
public class SqlServerMessageDbContextFactory : IDesignTimeDbContextFactory<SqlServerMessageDbContext>
{
    public SqlServerMessageDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SqlServerMessageDbContext>()
            .UseSqlServer("Server=(local);Database=design-time-only;Trusted_Connection=True;TrustServerCertificate=True;")
            .Options;
        return new SqlServerMessageDbContext(options);
    }
}
