using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MessageService.Data.Crypto;
using MessageService.Models;

namespace MessageService.Data;

/// <summary>EF Core 預設的模型快取只用 DbContext 的 CLR 型別當鍵，不會管建構子額外收到的
/// cipher 是誰——同一個型別的第一個實例決定了「有沒有套加密轉換器」，之後不管後續實例
/// 傳進來的 cipher 是什麼都沿用那份快取的模型，加密就悄悄失效或誤套用。要讓模型依
/// EncryptionEnabled 分開快取，必須自訂 IModelCacheKeyFactory，見官方文件對「模型仰賴
/// 外部設定建構」情境的說明，透過 MessageDbContext.OnConfiguring 套用。</summary>
internal class MessageDbContextModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is MessageDbContext db
            ? (context.GetType(), db.EncryptionEnabled, designTime)
            : (context.GetType(), designTime);
}

/// <summary>cipher 預設 null（未加密）——刻意用預設參數而非必要參數，讓既有直接
/// `new MessageDbContext(options)` 的測試不必逐一改動；正式環境透過 DI 由
/// AddDbContext 自動解析已註冊的 FieldCipher 單例注入。
///
/// 建構子吃非泛型的 <see cref="DbContextOptions"/> 而非 <c>DbContextOptions&lt;MessageDbContext&gt;</c>——
/// 這樣 <see cref="SqliteMessageDbContext"/>／<see cref="SqlServerMessageDbContext"/> 兩個只為了
/// 區分 migrations 集合而存在的衍生類別才能把各自的 <c>DbContextOptions&lt;TDerived&gt;</c>
/// 往上轉型傳進來（<c>DbContextOptions&lt;TDerived&gt;</c> 是 <c>DbContextOptions&lt;MessageDbContext&gt;</c>
/// 的相容型別，但兩個不同的封閉泛型型別之間沒有隱含轉換，只有都收斂到非泛型基底才行）——
/// 這是 EF Core 官方文件對「同一個 DbContext 支援多個 provider、各自獨立 migrations」情境
/// 建議的寫法。既有透過 <c>DbContextOptionsBuilder&lt;MessageDbContext&gt;</c> 建構子呼叫的測試
/// 完全不用改，<c>DbContextOptions&lt;MessageDbContext&gt;</c> 本來就是 <c>DbContextOptions</c>
/// 的子型別，可以直接傳。</summary>
public class MessageDbContext(DbContextOptions options, FieldCipher? cipher = null) : DbContext(options)
{
    internal bool EncryptionEnabled => cipher is { Enabled: true };

    public DbSet<GroupMessage> GroupMessages => Set<GroupMessage>();
    public DbSet<MessageContent> MessageContents => Set<MessageContent>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ViewerSettings> ViewerSettings => Set<ViewerSettings>();
    public DbSet<MaskKeyword> MaskKeywords => Set<MaskKeyword>();
    public DbSet<MaskKeywordGroup> MaskKeywordGroups => Set<MaskKeywordGroup>();
    public DbSet<UserAlias> UserAliases => Set<UserAlias>();
    public DbSet<AnonymousIdentity> AnonymousIdentities => Set<AnonymousIdentity>();
    public DbSet<HostHeartbeat> HostHeartbeats => Set<HostHeartbeat>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, MessageDbContextModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMessage>(entity =>
        {
            entity.HasIndex(m => m.WebhookEventId).IsUnique();
            entity.HasOne(m => m.Content)
                .WithOne(c => c.GroupMessage)
                .HasForeignKey<MessageContent>(c => c.GroupMessageId)
                .OnDelete(DeleteBehavior.Cascade);

            // LINE 的 group id／user id 是 33 字元定長；原本的 nvarchar(max) 在 SQL Server 上
            // 是 LOB 型別，索引鍵不能用 LOB，所以檢視端每一種查詢（側欄、未讀數、訊息視窗、搜尋）
            // 用 GroupId 過濾時全部是全表掃描。收斂成有限長度才建得了下面兩個索引。
            entity.Property(m => m.GroupId).HasMaxLength(64);
            entity.Property(m => m.UserId).HasMaxLength(64);
            entity.Property(m => m.MessageType).HasMaxLength(20);

            entity.HasIndex(m => new { m.GroupId, m.Id }); // 未讀數／afterId／beforeId／hasMore
            entity.HasIndex(m => new { m.GroupId, m.EventTimestamp }); // 天數視窗／aroundId
        });

        modelBuilder.Entity<MessageContent>(entity =>
        {
            entity.Property(c => c.DownloadStatus).HasConversion<string>().HasMaxLength(20);

            // GetPendingIdsAsync／認領邏輯只關心「還沒下載完」的列，但這張表裝著所有 blob——
            // 沒有索引就是全表掃描。篩選索引只蓋未完成的列，兩個 provider 的篩選子句語法不同
            // （方括號 vs 雙引號），但都是相同的邏輯條件
            entity.HasIndex(c => c.DownloadStatus)
                .HasFilter(Database.IsSqlite() ? "\"DownloadStatus\" <> 'Completed'" : "[DownloadStatus] <> 'Completed'");
        });

        modelBuilder.Entity<Group>().HasKey(g => g.GroupId);
        modelBuilder.Entity<GroupMember>().HasKey(m => new { m.GroupId, m.UserId });

        modelBuilder.Entity<ViewerSettings>(entity =>
        {
            // 單列設定，Id 固定為 SingletonId 而非資料庫產生：若留成 identity，
            // 程式碼補建這列時帶著 Id=1 會在 SQL Server 撞上 IDENTITY_INSERT OFF
            entity.Property(v => v.Id).ValueGeneratedNever();
            entity.Property(v => v.NameDisplayMode).HasConversion<string>();
            entity.HasData(new ViewerSettings { Id = Models.ViewerSettings.SingletonId });
        });

        modelBuilder.Entity<MaskKeyword>()
            .HasMany(k => k.Groups)
            .WithOne(g => g.MaskKeyword)
            .HasForeignKey(g => g.MaskKeywordId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MaskKeywordGroup>()
            .HasKey(g => new { g.MaskKeywordId, g.GroupId });

        modelBuilder.Entity<UserAlias>().HasKey(a => a.UserId);

        modelBuilder.Entity<AnonymousIdentity>(entity =>
        {
            entity.HasKey(a => new { a.GroupId, a.UserId });

            // 併發指派代號時避免同群組撞名（不同使用者分到相同 Label），
            // 靠資料庫唯一索引擋下並轉成衝突例外，供服務端捕捉重試。
            entity.HasIndex(a => new { a.GroupId, a.Label }).IsUnique();
        });

        modelBuilder.Entity<HostHeartbeat>(entity =>
        {
            entity.HasKey(h => new { h.Role, h.MachineName });
            entity.Property(h => h.Role).HasMaxLength(20);
            entity.Property(h => h.MachineName).HasMaxLength(128);
        });

        // SQLite only supports equality on DateTimeOffset, not <, > comparisons — needed for
        // retention cleanup's and profile cache staleness date-range queries. SQL Server keeps the
        // native datetimeoffset column since it supports range comparisons natively and other tools
        // may query these tables directly.
        if (Database.IsSqlite())
        {
            modelBuilder.Entity<GroupMessage>()
                .Property(m => m.EventTimestamp)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<Group>()
                .Property(g => g.UpdatedAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<Group>()
                .Property(g => g.LastMessageAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<GroupMember>()
                .Property(m => m.UpdatedAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<MessageContent>()
                .Property(c => c.LastAttemptAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<HostHeartbeat>()
                .Property(h => h.LastSeenAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
        }

        // GroupId／UserId 保持明文——它們是隨機識別碼、不含個資，索引與 GroupBy 需要它們留在
        // 明文才能運作（見上面的索引與批次 A 的收斂）。這裡加密的都是實際承載個資的欄位；
        // blob（MessageContents.Content）不走這裡，需要保留 Range 拖進度能力，改在
        // DbContentWorkSource／ContentStreamService 用分塊加解密，見 ChunkedBlobCipher。
        if (cipher is { Enabled: true })
        {
            ApplyFieldEncryption(modelBuilder, cipher);
        }
    }

    private static void ApplyFieldEncryption(ModelBuilder modelBuilder, FieldCipher cipher)
    {
        var nullableConverter = new ValueConverter<string?, string?>(
            v => v == null ? null : cipher.Encrypt(v),
            v => v == null ? null : cipher.Decrypt(v));
        var requiredConverter = new ValueConverter<string, string>(
            v => cipher.Encrypt(v),
            v => cipher.Decrypt(v));

        modelBuilder.Entity<GroupMessage>().Property(m => m.Text).HasConversion(nullableConverter);
        modelBuilder.Entity<MessageContent>().Property(c => c.FileName).HasConversion(nullableConverter);
        modelBuilder.Entity<Group>().Property(g => g.GroupName).HasConversion(nullableConverter);
        modelBuilder.Entity<Group>().Property(g => g.PictureUrl).HasConversion(nullableConverter);
        modelBuilder.Entity<GroupMember>().Property(m => m.DisplayName).HasConversion(nullableConverter);
        modelBuilder.Entity<GroupMember>().Property(m => m.PictureUrl).HasConversion(nullableConverter);
        modelBuilder.Entity<UserAlias>().Property(a => a.Alias).HasConversion(requiredConverter);
    }
}
