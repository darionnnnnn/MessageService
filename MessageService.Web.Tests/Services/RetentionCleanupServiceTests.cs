using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class RetentionCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public RetentionCleanupServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private RetentionCleanupService CreateService() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        OptionsFactory.Create(new RetentionOptions()),
        NullLogger<RetentionCleanupService>.Instance);

    private async Task SetRetentionDaysAsync(int days)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var settings = await dbContext.ViewerSettings.SingleAsync(v => v.Id == ViewerSettings.SingletonId);
        settings.RetentionDays = days;
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task RunCleanupAsync_RemovesMessagesOlderThanRetentionDays_AndCascadesContent()
    {
        await SetRetentionDaysAsync(1095); // 3 年，跟改版前的預設值對齊
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "old",
                    LineMessageId = "m-old",
                    GroupId = "G1",
                    MessageType = "image",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1096),
                    ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1096),
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Blob = new MessageContentBlob { Content = [1, 2, 3] },
                        ContentType = "image/jpeg"
                    }
                },
                new GroupMessage
                {
                    WebhookEventId = "recent",
                    LineMessageId = "m-recent",
                    GroupId = "G1",
                    MessageType = "text",
                    Text = "still here",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
                    ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1)
                });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var remaining = await verifyContext.GroupMessages.ToListAsync();
        var remainingContents = await verifyContext.MessageContents.ToListAsync();

        var remainingMessage = Assert.Single(remaining);
        Assert.Equal("recent", remainingMessage.WebhookEventId);
        Assert.Empty(remainingContents);
    }

    [Fact]
    public async Task RunCleanupAsync_GroupsLastMessagePointer_RecalculatedAfterItsMessageIsDeleted()
    {
        await SetRetentionDaysAsync(1095);
        GroupMessage oldMessage, recentMessage;
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            oldMessage = new GroupMessage
            {
                WebhookEventId = "old", LineMessageId = "m-old", GroupId = "G1", MessageType = "text", Text = "old",
                EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1096), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1096)
            };
            recentMessage = new GroupMessage
            {
                WebhookEventId = "recent", LineMessageId = "m-recent", GroupId = "G1", MessageType = "text", Text = "recent",
                EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1)
            };
            dbContext.GroupMessages.AddRange(oldMessage, recentMessage);
            await dbContext.SaveChangesAsync();

            // 模擬「Groups.LastMessageId 目前指向即將被清除的那則」——側欄快取還沒追上
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1", UpdatedAt = DateTimeOffset.UtcNow,
                LastMessageId = oldMessage.Id, LastMessageAt = oldMessage.EventTimestamp
            });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var group = await verifyContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Equal(recentMessage.Id, group.LastMessageId);
        // SQLite 的 DateTimeOffsetToBinaryConverter 來回轉換會有微秒等級的精度損失，
        // 不是這裡要驗證的重點，只比對到毫秒
        Assert.Equal(recentMessage.EventTimestamp, group.LastMessageAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task RunCleanupAsync_GroupWithAllMessagesDeleted_ClearsLastMessagePointer()
    {
        await SetRetentionDaysAsync(1095);
        GroupMessage oldMessage;
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            oldMessage = new GroupMessage
            {
                WebhookEventId = "old", LineMessageId = "m-old", GroupId = "G1", MessageType = "text", Text = "old",
                EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1096), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1096)
            };
            dbContext.GroupMessages.Add(oldMessage);
            await dbContext.SaveChangesAsync();

            dbContext.Groups.Add(new Group
            {
                GroupId = "G1", UpdatedAt = DateTimeOffset.UtcNow,
                LastMessageId = oldMessage.Id, LastMessageAt = oldMessage.EventTimestamp
            });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var group = await verifyContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Null(group.LastMessageId);
        Assert.Null(group.LastMessageAt);
    }

    [Fact]
    public async Task RunCleanupAsync_NoViewerSettingsRow_FallsBackToDefaultRetentionDays()
    {
        // EnsureCreated() 的 HasData 一定會種好單列設定，這裡模擬萬一那筆不存在的防禦性情境
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.ViewerSettings.RemoveRange(dbContext.ViewerSettings);
            await dbContext.SaveChangesAsync();

            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "very-old", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "x",
                EventTimestamp = DateTimeOffset.UtcNow.AddDays(-(ViewerSettings.DefaultRetentionDays + 1)),
                ReceivedAt = DateTimeOffset.UtcNow.AddDays(-(ViewerSettings.DefaultRetentionDays + 1))
            });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        Assert.Empty(await verifyContext.GroupMessages.ToListAsync());
    }

    [Fact]
    public async Task RunCleanupAsync_UsesConfiguredRetentionDays_ShortWindow()
    {
        await SetRetentionDaysAsync(7);
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "old",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-8), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-8)
                },
                new GroupMessage
                {
                    WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "text", Text = "kept",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-6), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-6)
                });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var remaining = Assert.Single(await verifyContext.GroupMessages.ToListAsync());
        Assert.Equal("e2", remaining.WebhookEventId);
    }

    [Fact]
    public async Task RunCleanupAsync_MoreRowsThanBatchSize_DeletesAllAcrossMultipleBatches()
    {
        // BatchSize=1000，塞 1005 筆要被刪的舊訊息，驗證分批迴圈真的會跑到全部清完，
        // 不會在第一批之後就停下來
        await SetRetentionDaysAsync(1);
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            for (var i = 0; i < 1005; i++)
            {
                dbContext.GroupMessages.Add(new GroupMessage
                {
                    WebhookEventId = $"old-{i}", LineMessageId = $"m{i}", GroupId = "G1", MessageType = "text", Text = "x",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-2), ReceivedAt = DateTimeOffset.UtcNow.AddDays(-2)
                });
            }
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "kept", LineMessageId = "m-kept", GroupId = "G1", MessageType = "text", Text = "kept",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var remaining = Assert.Single(await verifyContext.GroupMessages.ToListAsync());
        Assert.Equal("kept", remaining.WebhookEventId);
    }

    [Fact]
    public async Task RunCleanupAsync_OrphanBlobWithNoParentMessageContent_IsRemovedByCleanup()
    {
        // 模擬「行程寫 blob 寫到一半被砍掉」或 cascade 失效的情境：
        // MessageContentBlobs 中存在一列，但沒有對應的 MessageContents 父列
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

            // 先插入一筆完整的訊息（帶 Content 與 Blob），儲存後再直接刪掉 MessageContent 父列，
            // 讓 MessageContentBlob 成為孤兒（模擬 cascade 失效或行程中途中斷的情境）
            var msg = new GroupMessage
            {
                WebhookEventId = "orphan-test",
                LineMessageId = "m-orphan",
                GroupId = "G1",
                MessageType = "image",
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Blob = new MessageContentBlob { Content = [9, 8, 7] },
                    ContentType = "image/jpeg"
                }
            };
            dbContext.GroupMessages.Add(msg);
            await dbContext.SaveChangesAsync();

            // 直接刪除 MessageContent，讓 MessageContentBlob 成為孤兒
            await dbContext.MessageContents
                .Where(c => c.GroupMessageId == msg.Id)
                .ExecuteDeleteAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();

        // 孤兒 blob 應已被清除
        Assert.Empty(await verifyContext.MessageContentBlobs.ToListAsync());
    }

    [Fact]
    public async Task RunCleanupAsync_NoCascadeFailure_OrphanCleanupDeletesZeroAndDoesNotAffectRetainedContent()
    {
        // cascade 正常運作時：到期訊息刪後其 blob 由 cascade 一起清，孤兒回收不應刪任何 blob，
        // 且不影響保留期內訊息的 blob
        await SetRetentionDaysAsync(1095);
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "old-blob",
                    LineMessageId = "m-old",
                    GroupId = "G1",
                    MessageType = "image",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1096),
                    ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1096),
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Blob = new MessageContentBlob { Content = [1, 2, 3] },
                        ContentType = "image/jpeg"
                    }
                },
                new GroupMessage
                {
                    WebhookEventId = "recent-blob",
                    LineMessageId = "m-recent",
                    GroupId = "G1",
                    MessageType = "image",
                    EventTimestamp = DateTimeOffset.UtcNow.AddDays(-1),
                    ReceivedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Blob = new MessageContentBlob { Content = [4, 5, 6] },
                        ContentType = "image/jpeg"
                    }
                });
            await dbContext.SaveChangesAsync();
        }

        await CreateService().RunCleanupAsync(CancellationToken.None);

        using var verifyScope2 = _provider.CreateScope();
        var verifyContext2 = verifyScope2.ServiceProvider.GetRequiredService<MessageDbContext>();

        // 舊訊息（含其 blob）應已由 cascade 清除；保留期內訊息的 blob 應仍存在
        var remainingMessages = await verifyContext2.GroupMessages.ToListAsync();
        var remainingBlobs = await verifyContext2.MessageContentBlobs.ToListAsync();
        var remainingContents = await verifyContext2.MessageContents.ToListAsync();

        var remaining = Assert.Single(remainingMessages);
        Assert.Equal("recent-blob", remaining.WebhookEventId);

        // blob 數量與 content 數量相等——沒有孤兒，也沒有誤刪保留期內的 blob
        Assert.Equal(remainingContents.Count, remainingBlobs.Count);
        Assert.Single(remainingBlobs);
    }
}
