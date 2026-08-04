using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Api;

public class MessageSearchTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static GroupMessage TextMessage(
        string webhookEventId, string groupId, string? userId, DateTimeOffset timestamp, string text) => new()
    {
        WebhookEventId = webhookEventId,
        LineMessageId = webhookEventId,
        GroupId = groupId,
        UserId = userId,
        MessageType = "text",
        Text = text,
        EventTimestamp = timestamp,
        ReceivedAt = timestamp
    };

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmptyList()
    {
        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=");

        Assert.NotNull(results);
        Assert.Empty(results!);
    }

    [Fact]
    public async Task Search_MatchesMessageText()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "今天去騎腳踏車"));
            dbContext.GroupMessages.Add(TextMessage("e2", "G1", "U1", now.AddMinutes(1), "晚上吃火鍋"));
            await Task.CompletedTask;
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=腳踏車");

        var hit = Assert.Single(results!);
        Assert.Equal("今天去騎腳踏車", hit.Snippet);
    }

    [Fact]
    public async Task Search_MatchesSenderDisplayName_EvenWhenTextDoesNotContainQuery()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Original;
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "晚安"));
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=小明");

        var hit = Assert.Single(results!);
        Assert.Equal("小明", hit.DisplayName);
        Assert.Equal("晚安", hit.Snippet);
    }

    [Fact]
    public async Task Search_KeywordMaskedText_DoesNotLeakThroughSearch()
    {
        // 搜尋不能變成遮蔽規則的後門：原文含關鍵字，但遮蔽後文字已經不含，就不該是命中
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "我的密碼是1234"));
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=密碼");

        Assert.Empty(results!);
    }

    [Fact]
    public async Task Search_KeywordMaskedText_SnippetShowsMaskedTextNotOriginal()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "我的密碼是1234，記得騎腳踏車"));
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=腳踏車");

        var hit = Assert.Single(results!);
        Assert.DoesNotContain("密碼", hit.Snippet);
        Assert.Contains("**", hit.Snippet);
    }

    [Fact]
    public async Task Search_AnonymousMode_NameMatchUsesLabel_NotRealName()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Anonymous;
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "早安"));
        });

        // 先讓訊息視窗端點指派一次代號（Anonymous 模式下第一次遇到成員才會指派）
        var assigned = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G1/messages?days=3");
        var label = assigned!.Messages.Single().DisplayName;

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>(
            $"/api/messages/search?q={Uri.EscapeDataString(label)}");

        var hit = Assert.Single(results!);
        Assert.Equal(label, hit.DisplayName);
        Assert.NotEqual("小明", hit.DisplayName);

        var realNameResults = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=小明");
        Assert.Empty(realNameResults!);
    }

    [Fact]
    public async Task Search_AnonymousMode_UnassignedMember_DoesNotTriggerAssignment()
    {
        // 搜尋端點對代號只讀不指派：沒被指派過代號的成員，姓名比對就是找不到，這是正確行為
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Anonymous;
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "早安"));
        });

        await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=早安");

        await _fixture.SeedAsync(async dbContext =>
        {
            var count = await dbContext.AnonymousIdentities.CountAsync();
            Assert.Equal(0, count);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Search_ScopedToGroupId_ExcludesOtherGroups()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "共同關鍵字訊息"));
            dbContext.GroupMessages.Add(TextMessage("e2", "G2", "U1", now, "共同關鍵字訊息"));
            await Task.CompletedTask;
        });

        var scoped = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=共同關鍵字&groupId=G1");
        Assert.Single(scoped!);
        Assert.Equal("G1", scoped!.Single().GroupId);

        var all = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=共同關鍵字");
        Assert.Equal(2, all!.Count);
    }

    [Fact]
    public async Task Search_NonTextMessage_MatchedByNameNotContent_ShowsTypeSnippet()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Original;
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "e1", GroupId = "G1", UserId = "U1",
                MessageType = "image", EventTimestamp = now, ReceivedAt = now
            });
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=小明");

        var hit = Assert.Single(results!);
        Assert.Equal("[圖片]", hit.Snippet);
    }

    [Fact]
    public async Task Search_OrdersByEventTimestampDescending()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "排序關鍵字 older"));
            dbContext.GroupMessages.Add(TextMessage("e2", "G1", "U1", now.AddMinutes(5), "排序關鍵字 newer"));
            await Task.CompletedTask;
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=排序關鍵字");

        Assert.Equal(["排序關鍵字 newer", "排序關鍵字 older"], results!.Select(r => r.Snippet));
    }

    [Fact]
    public async Task GetMessages_AroundId_ReturnsWindowCenteredOnAnchor()
    {
        var now = DateTimeOffset.UtcNow;
        long anchorId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var tooOld = TextMessage("e1", "G1", "U1", now.AddDays(-10), "too-old");
            var before = TextMessage("e2", "G1", "U1", now.AddHours(-1), "before-anchor");
            var anchor = TextMessage("e3", "G1", "U1", now, "anchor");
            var after = TextMessage("e4", "G1", "U1", now.AddHours(1), "after-anchor");
            var tooNew = TextMessage("e5", "G1", "U1", now.AddDays(10), "too-new");
            dbContext.GroupMessages.AddRange(tooOld, before, anchor, after, tooNew);
            await dbContext.SaveChangesAsync();
            anchorId = anchor.Id;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>($"/api/groups/G1/messages?aroundId={anchorId}&days=3");

        Assert.Equal(["before-anchor", "anchor", "after-anchor"], page!.Messages.Select(m => m.Text));
        Assert.Null(page.LatestId);
    }

    [Fact]
    public async Task GetMessages_AroundId_UnknownId_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/api/groups/G1/messages?aroundId=999999");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
