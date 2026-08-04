using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class GroupsControllerTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetGroups_NoData_ReturnsEmptyList()
    {
        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.NotNull(groups);
        Assert.Empty(groups!);
    }

    [Fact]
    public async Task GetGroups_ReturnsOnlyGroupsWithMessages_UsingCachedNameWithFallback()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "工作群組A", UpdatedAt = DateTimeOffset.UtcNow });
            // G2 has messages but no cached Group row (profile fetch failed) -> should fall back to GroupId
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G2", MessageType = "text", Text = "hi",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);
        Assert.Contains(groups, g => g.GroupId == "G1" && g.DisplayName == "工作群組A");
        Assert.Contains(groups, g => g.GroupId == "G2" && g.DisplayName == "G2");
    }

    [Fact]
    public async Task GetGroups_OrdersByLastMessageTime_MostRecentFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "old",
                EventTimestamp = now.AddDays(-1), ReceivedAt = now.AddDays(-1)
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G2", MessageType = "text", Text = "new",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Equal(["G2", "G1"], groups!.Select(g => g.GroupId));
    }

    [Fact]
    public async Task GetGroups_TextPreview_IsMaskedAndTruncated()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "我的密碼是這一長串超過三十個字元的訊息內容用來驗證預覽文字會被正確截斷不會整段塞進側欄清單裡面顯示出來",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var preview = Assert.Single(groups!).LastMessagePreview;
        Assert.NotNull(preview);
        Assert.DoesNotContain("密碼", preview);
        Assert.EndsWith("…", preview);
        Assert.True(preview!.Length <= 31);
    }

    [Theory]
    [InlineData("sticker", "[貼圖]")]
    [InlineData("image", "[圖片]")]
    [InlineData("video", "[影片]")]
    [InlineData("audio", "[語音訊息]")]
    [InlineData("file", "[檔案]")]
    public async Task GetGroups_NonTextPreview_UsesTypeLabel(string messageType, string expectedPreview)
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = messageType,
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Equal(expectedPreview, Assert.Single(groups!).LastMessagePreview);
    }

    [Fact]
    public async Task GetGroups_ReturnsMemberCount()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "A", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U2", DisplayName = "B", UpdatedAt = now });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Equal(2, Assert.Single(groups!).MemberCount);
    }
}
