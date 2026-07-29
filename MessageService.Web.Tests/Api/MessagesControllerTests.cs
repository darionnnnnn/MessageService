using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class MessagesControllerTests : IDisposable
{
    private const string GroupId = "G1";
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static GroupMessage TextMessage(string webhookEventId, string userId, DateTimeOffset timestamp, string text) => new()
    {
        WebhookEventId = webhookEventId,
        LineMessageId = webhookEventId,
        GroupId = GroupId,
        UserId = userId,
        MessageType = "text",
        Text = text,
        EventTimestamp = timestamp,
        ReceivedAt = timestamp
    };

    [Fact]
    public async Task GetMessages_InitialLoad_ReturnsOnlyWithinDaysWindow_OldestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            // Id 遞增順序要跟 EventTimestamp 一致才符合真實情境（訊息依 LINE 送達順序寫入）
            dbContext.GroupMessages.Add(TextMessage("e3", "U1", now.AddDays(-10), "too-old"));
            dbContext.GroupMessages.Add(TextMessage("e1", "U1", now.AddDays(-1), "recent-1"));
            dbContext.GroupMessages.Add(TextMessage("e2", "U1", now.AddHours(-1), "recent-2"));
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/{GroupId}/messages?days=3");

        Assert.NotNull(page);
        Assert.Equal(2, page!.Messages.Count);
        Assert.Equal("recent-1", page.Messages[0].Text);
        Assert.Equal("recent-2", page.Messages[1].Text);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task GetMessages_NoOlderHistory_HasMoreIsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(TextMessage("e1", "U1", now, "only-message"));
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/{GroupId}/messages?days=3");

        Assert.False(page!.HasMore);
    }

    [Fact]
    public async Task GetMessages_BeforeId_ReturnsOlderMessagesWithinWindow()
    {
        var now = DateTimeOffset.UtcNow;
        long cursorId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            // Id 遞增順序要跟 EventTimestamp 一致才符合真實情境（訊息依 LINE 送達順序寫入）
            var tooOld = TextMessage("e3", "U1", now.AddDays(-20), "too-old-for-window");
            var older = TextMessage("e1", "U1", now.AddDays(-5), "older");
            var cursor = TextMessage("e2", "U1", now.AddDays(-1), "cursor");
            dbContext.GroupMessages.AddRange(tooOld, older, cursor);
            await dbContext.SaveChangesAsync();
            cursorId = cursor.Id;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>(
            $"/api/groups/{GroupId}/messages?beforeId={cursorId}&days=7");

        var message = Assert.Single(page!.Messages);
        Assert.Equal("older", message.Text);
        Assert.True(page.HasMore); // too-old-for-window still exists further back
    }

    [Fact]
    public async Task GetMessages_AfterId_ReturnsOnlyNewerMessages()
    {
        var now = DateTimeOffset.UtcNow;
        long firstId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var first = TextMessage("e1", "U1", now.AddMinutes(-2), "first");
            var second = TextMessage("e2", "U1", now.AddMinutes(-1), "second");
            dbContext.GroupMessages.AddRange(first, second);
            await dbContext.SaveChangesAsync();
            firstId = first.Id;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>(
            $"/api/groups/{GroupId}/messages?afterId={firstId}");

        var message = Assert.Single(page!.Messages);
        Assert.Equal("second", message.Text);
    }

    [Fact]
    public async Task GetMessages_DisplayName_IsMaskedByDefault_AndFallsBackToMaskedUserIdWhenNoCachedProfile()
    {
        // 預設 ViewerSettings.NameDisplayMode = MaskMiddle（migration 種子值），驗證真的 MaskingService 有接進來
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = GroupId, UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMessages.Add(TextMessage("e1", "U1", now, "hi"));
            dbContext.GroupMessages.Add(TextMessage("e2", "U2", now, "hi2"));
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/{GroupId}/messages?days=3");

        Assert.Equal("小*", page!.Messages.Single(m => m.UserId == "U1").DisplayName);
        Assert.Equal("U*", page.Messages.Single(m => m.UserId == "U2").DisplayName);
    }

    [Fact]
    public async Task GetMessages_Text_IsMaskedByActiveKeywordRules()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
            dbContext.GroupMessages.Add(TextMessage("e1", "U1", now, "我的密碼是1234"));
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/{GroupId}/messages?days=3");

        Assert.Equal("我的**是1234", Assert.Single(page!.Messages).Text);
    }

    [Fact]
    public async Task GetMessages_ImageMessage_IncludesContentInfo()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = GroupId, MessageType = "image",
                EventTimestamp = now, ReceivedAt = now,
                Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
            });
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/{GroupId}/messages?days=3");

        var message = Assert.Single(page!.Messages);
        Assert.NotNull(message.Content);
        Assert.Equal("Pending", message.Content!.DownloadStatus);
    }

    [Fact]
    public async Task GetStatuses_ReturnsCurrentStatusForRequestedIds()
    {
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = GroupId, MessageType = "image",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent { DownloadStatus = DownloadStatus.Completed, ContentType = "image/png" }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });

        var statuses = await _fixture.Client.GetFromJsonAsync<List<MessageStatusDto>>($"/api/messages/statuses?ids={contentId}");

        var status = Assert.Single(statuses!);
        Assert.Equal("Completed", status.DownloadStatus);
        Assert.Equal("image/png", status.ContentType);
    }

    [Fact]
    public async Task GetStatuses_EmptyIds_ReturnsEmptyList()
    {
        var statuses = await _fixture.Client.GetFromJsonAsync<List<MessageStatusDto>>("/api/messages/statuses?ids=");

        Assert.NotNull(statuses);
        Assert.Empty(statuses!);
    }
}
