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
