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

    [Fact]
    public async Task RunCleanupAsync_RemovesMessagesOlderThanRetentionPeriod_AndCascadesContent()
    {
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
                    EventTimestamp = DateTimeOffset.UtcNow.AddYears(-3).AddDays(-1),
                    ReceivedAt = DateTimeOffset.UtcNow.AddYears(-3).AddDays(-1),
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Content = [1, 2, 3],
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

        var service = new RetentionCleanupService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new RetentionOptions { Years = 3 }),
            NullLogger<RetentionCleanupService>.Instance);

        await service.RunCleanupAsync(CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var remaining = await verifyContext.GroupMessages.ToListAsync();
        var remainingContents = await verifyContext.MessageContents.ToListAsync();

        var remainingMessage = Assert.Single(remaining);
        Assert.Equal("recent", remainingMessage.WebhookEventId);
        Assert.Empty(remainingContents);
    }
}
