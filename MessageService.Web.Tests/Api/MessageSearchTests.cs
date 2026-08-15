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
        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=腳踏車");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=密碼");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=腳踏車");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>(
            $"/api/messages/search?q={Uri.EscapeDataString(label)}");
        var results = response?.Results;

        var hit = Assert.Single(results!);
        Assert.Equal(label, hit.DisplayName);
        Assert.NotEqual("小明", hit.DisplayName);

        var realNameResponse = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明");
        var realNameResults = realNameResponse?.Results;
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

        await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=早安");

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

        var scopedResponse = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=共同關鍵字&groupId=G1");
        var scoped = scopedResponse?.Results;
        Assert.Single(scoped!);
        Assert.Equal("G1", scoped!.Single().GroupId);

        var allResponse = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=共同關鍵字");
        var all = allResponse?.Results;
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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明");
        var results = response?.Results;

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

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=排序關鍵字");
        var results = response?.Results;

        Assert.Equal(["排序關鍵字 newer", "排序關鍵字 older"], results!.Select(r => r.Snippet));
    }

    [Fact]
    public async Task GetMessages_AroundId_ReturnsWindowCenteredOnAnchor_IgnoringDaysParam()
    {
        // 問題6修正後 aroundId 改成純粹依 Id 兩側各查一次，不再套用 days 天數視窗——
        // 時間上很久遠的訊息只要 Id 落在半窗額度內一樣會回傳，跟 days 參數無關
        // （這點跟 afterId 分頁本來就不套用 days 一致，見 GetMessages 的分支邏輯）
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

        Assert.Equal(["too-old", "before-anchor", "anchor", "after-anchor", "too-new"], page!.Messages.Select(m => m.Text));
        Assert.Null(page.LatestId);
    }

    [Fact]
    public async Task GetMessages_AroundId_UnknownId_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync("/api/groups/G1/messages?aroundId=999999");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_ContentAndNameMatchesBothExceedQuota_EachCategoryCappedIndependently()
    {
        // 沒有各自配額時：姓名命中比較新，會把「整體最近 100 筆」全灌成姓名命中，內容命中
        // 幾乎被擠光——這裡刻意讓姓名命中（90 筆）比內容命中（60 筆）更新，重現這個情境
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U-name", DisplayName = "小明", UpdatedAt = now });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Original;

            // 姓名命中：90 則，內容跟關鍵字無關，時間最新（Id 遞增＝時間遞增，最後一筆最新）
            for (var i = 0; i < 90; i++)
            {
                dbContext.GroupMessages.Add(TextMessage($"name-{i}", "G1", "U-name", now.AddMinutes(-(89 - i)), $"name-hit-{i}"));
            }

            // 內容命中：60 則，文字含關鍵字，時間較舊（一天前那個區間內）
            for (var i = 0; i < 60; i++)
            {
                dbContext.GroupMessages.Add(TextMessage($"content-{i}", "G1", "U-content", now.AddDays(-1).AddMinutes(-(59 - i)), $"提到小明的訊息-{i}"));
            }
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明");
        var results = response?.Results;

        Assert.NotNull(results);
        var nameHitCount = results.Count(r => r.Snippet.StartsWith("name-hit-"));
        var contentHitCount = results.Count(r => r.Snippet.Contains("提到小明的訊息"));
        Assert.Equal(100, results.Count);
        Assert.Equal(50, nameHitCount);
        Assert.Equal(50, contentHitCount);
    }

    [Fact]
    public async Task Search_ContentMatchesWithinQuota_AllReturned()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            for (var i = 0; i < 10; i++)
            {
                dbContext.GroupMessages.Add(TextMessage($"c{i}", "G1", "U1", now.AddMinutes(-(9 - i)), $"提到腳踏車-{i}"));
            }
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=腳踏車");
        var results = response?.Results;

        Assert.Equal(10, results!.Count);
    }

    [Fact]
    public async Task Search_MemberWithPictureContent_ScopedToGroupId_ReturnsMatchingMessages()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U1",
                DisplayName = "小明",
                PictureContent = new byte[] { 1, 2, 3 },
                UpdatedAt = now
            });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Original;
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "晚安"));
        });

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明&groupId=G1");
        var results = response?.Results;

        var hit = Assert.Single(results!);
        Assert.Equal("G1", hit.GroupId);
        Assert.Equal("小明", hit.DisplayName);
        Assert.Equal("晚安", hit.Snippet);
    }

    [Fact]
    public async Task Search_MemberWithPictureContent_CrossGroupSearch_ReturnsOnlyMatchingGroupMessages()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U1",
                DisplayName = "小明",
                PictureContent = new byte[] { 1, 2, 3 },
                UpdatedAt = now
            });
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G2",
                UserId = "U2",
                DisplayName = "小華",
                PictureContent = new byte[] { 4, 5, 6 },
                UpdatedAt = now
            });
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Original;
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", "U1", now, "G1訊息"));
            dbContext.GroupMessages.Add(TextMessage("e2", "G2", "U2", now, "G2訊息"));
        });

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=小明");
        var results = response?.Results;

        var hit = Assert.Single(results!);
        Assert.Equal("G1", hit.GroupId);
        Assert.Equal("小明", hit.DisplayName);
        Assert.Equal("G1訊息", hit.Snippet);
    }

    [Fact]
    public async Task Search_SystemMessageWithNullUserId_ReturnsUnknownDisplayNameWithoutException()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(TextMessage("e1", "G1", null, now, "系統訊息通知"));
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=系統訊息");
        var results = response?.Results;

        var hit = Assert.Single(results!);
        Assert.Equal("(未知)", hit.DisplayName);
        Assert.Equal("系統訊息通知", hit.Snippet);
    }

    [Fact]
    public async Task Search_EncryptionDisabled_ReturnsLimitedByEncryptionFalse()
    {
        var response = await _fixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=測試");

        Assert.NotNull(response);
        Assert.False(response.LimitedByEncryption);
    }

    [Fact]
    public async Task Search_EncryptionEnabled_ReturnsLimitedByEncryptionTrue()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        using var encryptedFixture = new WebAppFactoryFixture(encryptionKey: key);

        var response = await encryptedFixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=測試");

        Assert.NotNull(response);
        Assert.True(response.LimitedByEncryption);
    }

    [Fact]
    public async Task Search_EmptyQuery_EncryptionEnabled_ReturnsLimitedByEncryptionTrue()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        using var encryptedFixture = new WebAppFactoryFixture(encryptionKey: key);

        var response = await encryptedFixture.Client.GetFromJsonAsync<MessageSearchResponseDto>("/api/messages/search?q=");

        Assert.NotNull(response);
        Assert.True(response.LimitedByEncryption);
        Assert.Empty(response.Results);
    }
}
