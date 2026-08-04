using MessageService.Data;
using MessageService.Models;
using MessageService.Models.Line;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

public class WebhookEventHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MessageDbContext _dbContext;
    private readonly FakeContentDownloadQueue _queue = new();
    private readonly FakeProfileRefreshQueue _profileRefreshQueue = new();
    private readonly WebhookEventHandler _handler;

    public WebhookEventHandlerTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        _dbContext = new MessageDbContext(options);
        _dbContext.Database.EnsureCreated();
        _handler = new WebhookEventHandler(_dbContext, _queue, _profileRefreshQueue, NullLogger<WebhookEventHandler>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static WebhookEvent GroupMessageEvent(string webhookEventId, LineMessage message, string groupId = "G1", string? userId = "U1") =>
        new()
        {
            Type = "message",
            WebhookEventId = webhookEventId,
            Timestamp = 1700000000000,
            Source = new EventSource { Type = "group", GroupId = groupId, UserId = userId },
            Message = message
        };

    [Fact]
    public async Task TextMessage_InGroup_IsSaved()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hello" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Equal("text", saved.MessageType);
        Assert.Equal("hello", saved.Text);
        Assert.Equal("G1", saved.GroupId);
        Assert.Equal("U1", saved.UserId);
        Assert.Empty(_dbContext.MessageContents);
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task SavedMessage_EnqueuesProfileRefreshForGroupAndUser()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hello" }, groupId: "G1", userId: "U1");

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var task = Assert.Single(_profileRefreshQueue.Enqueued);
        Assert.Equal("G1", task.GroupId);
        Assert.Equal("U1", task.UserId);
    }

    [Fact]
    public async Task DuplicateOrIgnoredEvent_DoesNotEnqueueProfileRefresh()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "location" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_profileRefreshQueue.Enqueued);
    }

    [Fact]
    public async Task StickerMessage_SavesPlaceholderText()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "sticker" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Equal("sticker", saved.MessageType);
        Assert.Equal("(貼圖)", saved.Text);
        Assert.Empty(_dbContext.MessageContents);
    }

    [Fact]
    public async Task StickerMessage_SavesStickerIdAndPackageId()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "sticker", StickerId = "52002734", PackageId = "11537" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Equal("52002734", saved.StickerId);
        Assert.Equal("11537", saved.PackageId);
    }

    [Fact]
    public async Task NonStickerMessage_LeavesStickerIdAndPackageIdNull()
    {
        // LineMessage 理論上不該混著貼圖欄位，但防禦性驗證：非貼圖訊息一律不寫入這兩個欄位
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hi", StickerId = "should-be-ignored" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var saved = Assert.Single(_dbContext.GroupMessages);
        Assert.Null(saved.StickerId);
        Assert.Null(saved.PackageId);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ImageOrVideoOrAudioMessage_CreatesPendingContentAndEnqueues(string messageType)
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = messageType });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var savedMessage = Assert.Single(_dbContext.GroupMessages);
        Assert.Null(savedMessage.Text);
        var content = Assert.Single(_dbContext.MessageContents);
        Assert.Equal(DownloadStatus.Pending, content.DownloadStatus);
        Assert.Equal(savedMessage.Id, content.GroupMessageId);
        Assert.Equal(content.Id, Assert.Single(_queue.Enqueued));
    }

    [Fact]
    public async Task FileMessage_StoresFileNameAndEnqueues()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "file", FileName = "report.pdf" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var content = Assert.Single(_dbContext.MessageContents);
        Assert.Equal("report.pdf", content.FileName);
        Assert.Equal(DownloadStatus.Pending, content.DownloadStatus);
        Assert.Single(_queue.Enqueued);
    }

    [Fact]
    public async Task NonGroupSource_IsIgnored()
    {
        var evt = new WebhookEvent
        {
            Type = "message",
            WebhookEventId = "evt-1",
            Source = new EventSource { Type = "user", UserId = "U1" },
            Message = new LineMessage { Id = "m1", Type = "text", Text = "hi" }
        };

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_dbContext.GroupMessages);
    }

    [Fact]
    public async Task UnsupportedMessageType_IsIgnored()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "location" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_dbContext.GroupMessages);
    }

    [Fact]
    public async Task NonMessageEvent_IsIgnored()
    {
        var evt = new WebhookEvent
        {
            Type = "join",
            WebhookEventId = "evt-1",
            Source = new EventSource { Type = "group", GroupId = "G1" }
        };

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_dbContext.GroupMessages);
    }

    [Fact]
    public async Task DuplicateWebhookEventId_IsSkipped()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hello" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);
        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Single(_dbContext.GroupMessages);
    }
}
