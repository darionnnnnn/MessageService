using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MessageService.Models;

namespace MessageService.Data;

public class MessageDbContext(DbContextOptions<MessageDbContext> options) : DbContext(options)
{
    public DbSet<GroupMessage> GroupMessages => Set<GroupMessage>();
    public DbSet<MessageContent> MessageContents => Set<MessageContent>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ViewerSettings> ViewerSettings => Set<ViewerSettings>();
    public DbSet<MaskKeyword> MaskKeywords => Set<MaskKeyword>();
    public DbSet<MaskKeywordGroup> MaskKeywordGroups => Set<MaskKeywordGroup>();
    public DbSet<UserAlias> UserAliases => Set<UserAlias>();
    public DbSet<AnonymousIdentity> AnonymousIdentities => Set<AnonymousIdentity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupMessage>(entity =>
        {
            entity.HasIndex(m => m.WebhookEventId).IsUnique();
            entity.HasOne(m => m.Content)
                .WithOne(c => c.GroupMessage)
                .HasForeignKey<MessageContent>(c => c.GroupMessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageContent>(entity =>
        {
            entity.Property(c => c.DownloadStatus).HasConversion<string>();
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

        modelBuilder.Entity<AnonymousIdentity>().HasKey(a => new { a.GroupId, a.UserId });

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
            modelBuilder.Entity<GroupMember>()
                .Property(m => m.UpdatedAt)
                .HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
