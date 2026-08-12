using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Api;

// Anonymous 模式下這支端點要跟全站其他地方（訊息串、搜尋、側欄）一樣不外流真名——這是
// 體檢揪出的原始 bug：/api/users 之前不管模式一律回真實 DisplayName，任何能開設定 modal 的人
// （＝任何進得來的人，因為沒有登入）都能一次拿到全部真名與 UserId。
public class UsersControllerTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetUsers_DefaultMode_ReturnsRealDisplayNames()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            await Task.CompletedTask;
        });

        var users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");

        var user = Assert.Single(users!);
        Assert.Equal("小明", user.DisplayName);
    }

    [Fact]
    public async Task GetUsers_AnonymousMode_AssignedMember_ReturnsLabelNotRealName()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Anonymous;
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", UserId = "U1", MessageType = "text",
                Text = "hi", EventTimestamp = now, ReceivedAt = now
            });
        });

        // 先讓訊息視窗端點指派一次代號（Anonymous 模式下第一次遇到成員才會指派）
        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G1/messages?days=3");
        var label = page!.Messages.Single().DisplayName;

        var users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");

        var user = Assert.Single(users!);
        Assert.Equal(label, user.DisplayName);
        Assert.NotEqual("小明", user.DisplayName);
    }

    [Fact]
    public async Task GetUsers_AnonymousMode_UnassignedMember_IsExcluded()
    {
        // 別名編輯器對代號只讀不指派——沒被指派過代號的成員應該直接從清單消失，
        // 不能顯示真名，也不該觸發指派
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Anonymous;
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            await Task.CompletedTask;
        });

        var users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");

        Assert.Empty(users!);

        await _fixture.SeedAsync(async dbContext =>
        {
            var count = await dbContext.AnonymousIdentities.CountAsync();
            Assert.Equal(0, count);
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("MaskMiddle")]
    [InlineData("CustomAlias")]
    [InlineData("Original")]
    public async Task GetUsers_NonAnonymousModes_StillReturnRealDisplayNames(string mode)
    {
        // CustomAlias 模式的別名編輯器本質上就需要看到真名才能設定別名，這是刻意的例外
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = Enum.Parse<NameDisplayMode>(mode);
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            await Task.CompletedTask;
        });

        var users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");

        var user = Assert.Single(users!);
        Assert.Equal("小明", user.DisplayName);
    }

    [Fact]
    public async Task GetUsers_AnonymousMode_GroupIdFilter_OnlyReturnsAssignedMembersInThatGroup()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            (await dbContext.ViewerSettings.SingleAsync()).NameDisplayMode = NameDisplayMode.Anonymous;
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G2", UserId = "U2", DisplayName = "小美", UpdatedAt = now });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", UserId = "U1", MessageType = "text",
                Text = "hi", EventTimestamp = now, ReceivedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G2", UserId = "U2", MessageType = "text",
                Text = "hi", EventTimestamp = now, ReceivedAt = now
            });
        });

        await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G1/messages?days=3");
        await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G2/messages?days=3");

        var users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users?groupId=G1");

        var user = Assert.Single(users!);
        Assert.Equal("U1", user.UserId);
    }
}
