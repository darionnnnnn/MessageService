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

public class ContentDownloadServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeLineContentClient _contentClient = new();

    public ContentDownloadServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ILineContentClient>(_contentClient);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ContentDownloadService CreateService(ContentDownloadOptions? options = null) =>
        new(
            new FakeContentDownloadQueue(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new ContentDownloadOptions
            {
                MaxRetries = 3,
                RetryDelayMilliseconds = 0,
                TranscodingPollSeconds = 0,
                TranscodingMaxPolls = 5
            }),
            NullLogger<ContentDownloadService>.Instance);

    private async Task<long> SeedPendingContentAsync(string messageType, string lineMessageId = "line-msg-1")
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var groupMessage = new GroupMessage
        {
            WebhookEventId = Guid.NewGuid().ToString(),
            LineMessageId = lineMessageId,
            GroupId = "G1",
            MessageType = messageType,
            EventTimestamp = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
        };
        dbContext.GroupMessages.Add(groupMessage);
        await dbContext.SaveChangesAsync();
        return groupMessage.Content!.Id;
    }

    private async Task<MessageContent> ReloadContentAsync(long id)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.MessageContents.SingleAsync(c => c.Id == id);
    }

    [Fact]
    public async Task ProcessAsync_ImageDownloadSucceeds_MarksCompleted()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => Task.FromResult(new LineContentResult([1, 2, 3, 4], "image/jpeg"));

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, content.Content);
        Assert.Equal("image/jpeg", content.ContentType);
        Assert.NotNull(content.CompletedAt);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ProcessAsync_VideoOrAudio_WaitsForTranscodingThenDownloads(string messageType)
    {
        var contentId = await SeedPendingContentAsync(messageType);
        var callCount = 0;
        _contentClient.OnGetTranscodingStatus = _ =>
        {
            callCount++;
            return Task.FromResult(callCount < 2 ? TranscodingStatus.Processing : TranscodingStatus.Succeeded);
        };

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Completed, content.DownloadStatus);
        Assert.True(callCount >= 2);
        Assert.Single(_contentClient.ContentCalls);
    }

    [Fact]
    public async Task ProcessAsync_VideoTranscodingFailed_MarksFailedWithoutDownloading()
    {
        var contentId = await SeedPendingContentAsync("video");
        _contentClient.OnGetTranscodingStatus = _ => Task.FromResult(TranscodingStatus.Failed);

        var service = CreateService();
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Empty(_contentClient.ContentCalls);
    }

    [Fact]
    public async Task ProcessAsync_DownloadFailsBeyondMaxRetries_MarksFailed()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => throw new HttpRequestException("network error");

        var service = CreateService(new ContentDownloadOptions { MaxRetries = 3, RetryDelayMilliseconds = 0 });
        await service.ProcessAsync(contentId, CancellationToken.None);

        var content = await ReloadContentAsync(contentId);
        Assert.Equal(DownloadStatus.Failed, content.DownloadStatus);
        Assert.Equal(3, _contentClient.ContentCalls.Count);
    }

    [Fact]
    public async Task RequeuePendingAsync_EnqueuesExistingPendingContent()
    {
        var contentId = await SeedPendingContentAsync("image");
        var queue = new FakeContentDownloadQueue();
        var service = new ContentDownloadService(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions()),
            NullLogger<ContentDownloadService>.Instance);

        await service.RequeuePendingAsync(CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(queue.Enqueued));
    }

    [Fact]
    public async Task RequeuePendingAsync_ResetsFailedContentToPendingAndEnqueues()
    {
        var contentId = await SeedPendingContentAsync("image");
        _contentClient.OnGetContent = _ => throw new HttpRequestException("bad token");
        var failingService = CreateService(new ContentDownloadOptions { MaxRetries = 1, RetryDelayMilliseconds = 0 });
        await failingService.ProcessAsync(contentId, CancellationToken.None);
        Assert.Equal(DownloadStatus.Failed, (await ReloadContentAsync(contentId)).DownloadStatus);

        var queue = new FakeContentDownloadQueue();
        var service = new ContentDownloadService(
            queue,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ContentDownloadOptions()),
            NullLogger<ContentDownloadService>.Instance);

        await service.RequeuePendingAsync(CancellationToken.None);

        Assert.Equal(contentId, Assert.Single(queue.Enqueued));
        Assert.Equal(DownloadStatus.Pending, (await ReloadContentAsync(contentId)).DownloadStatus);
    }
}
