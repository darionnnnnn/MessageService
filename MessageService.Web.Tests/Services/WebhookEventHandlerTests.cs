using MessageService.Models.Line;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Services;

// WebhookEventHandler 現在只負責「這個 webhook 事件該不該收、該組成什麼 IngestEnvelope」，
// 不再碰資料庫——落地邏輯搬到 DirectIngestSinkTests。這份測試把原本斷言 DB 狀態的地方
// 全部改成斷言寫進 outbox 的 envelope 內容，涵蓋範圍與原本相同。
public class WebhookEventHandlerTests
{
    private readonly FakeOutboxWriter _outbox = new();
    private readonly WebhookEventHandler _handler;

    public WebhookEventHandlerTests()
    {
        _handler = new WebhookEventHandler(_outbox, NullLogger<WebhookEventHandler>.Instance);
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
    public async Task TextMessage_InGroup_IsQueued()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hello" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Equal("text", envelope.MessageType);
        Assert.Equal("hello", envelope.Text);
        Assert.Equal("G1", envelope.GroupId);
        Assert.Equal("U1", envelope.UserId);
        Assert.False(envelope.HasContent);
    }

    [Fact]
    public async Task IgnoredEvent_IsNotQueued()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "location" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_outbox.Enqueued);
    }

    [Fact]
    public async Task StickerMessage_QueuesPlaceholderText()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "sticker" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Equal("sticker", envelope.MessageType);
        Assert.Equal("(貼圖)", envelope.Text);
        Assert.False(envelope.HasContent);
    }

    [Fact]
    public async Task StickerMessage_QueuesStickerIdAndPackageId()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "sticker", StickerId = "52002734", PackageId = "11537" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Equal("52002734", envelope.StickerId);
        Assert.Equal("11537", envelope.PackageId);
    }

    [Fact]
    public async Task NonStickerMessage_LeavesStickerIdAndPackageIdNull()
    {
        // LineMessage 理論上不該混著貼圖欄位，但防禦性驗證：非貼圖訊息一律不帶這兩個欄位
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hi", StickerId = "should-be-ignored" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Null(envelope.StickerId);
        Assert.Null(envelope.PackageId);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("audio")]
    public async Task ImageOrVideoOrAudioMessage_MarksHasContent(string messageType)
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = messageType });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Null(envelope.Text);
        Assert.True(envelope.HasContent);
    }

    [Fact]
    public async Task FileMessage_QueuesFileNameAndMarksHasContent()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "file", FileName = "report.pdf" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        var envelope = Assert.Single(_outbox.Enqueued);
        Assert.Equal("report.pdf", envelope.ContentFileName);
        Assert.True(envelope.HasContent);
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

        Assert.Empty(_outbox.Enqueued);
    }

    [Fact]
    public async Task UnsupportedMessageType_IsIgnored()
    {
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "location" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Empty(_outbox.Enqueued);
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

        Assert.Empty(_outbox.Enqueued);
    }

    [Fact]
    public async Task DuplicateWebhookEventId_IsQueuedTwice()
    {
        // 防重送不再是 handler 的責任——它只負責解析並寫進 outbox，同一個 WebhookEventId
        // 出現兩次就寫兩筆，去重交給落地那端的資料庫唯一索引（見 DirectIngestSinkTests）
        var evt = GroupMessageEvent("evt-1", new LineMessage { Id = "m1", Type = "text", Text = "hello" });

        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);
        await _handler.HandleAsync(new WebhookRequest { Events = [evt] }, CancellationToken.None);

        Assert.Equal(2, _outbox.Enqueued.Count);
    }
}
