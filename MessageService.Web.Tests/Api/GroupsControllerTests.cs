using System.Net.Http.Json;
using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task GetGroups_NoReadParam_ReturnsLastMessageIdAndZeroUnread()
    {
        var now = DateTimeOffset.UtcNow;
        var last = new GroupMessage
        {
            WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "text", Text = "new",
            EventTimestamp = now, ReceivedAt = now
        };
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "old",
                EventTimestamp = now.AddMinutes(-1), ReceivedAt = now.AddMinutes(-1)
            });
            dbContext.GroupMessages.Add(last);
            await Task.CompletedTask;
        });

        var group = Assert.Single((await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups"))!);

        Assert.Equal(last.Id, group.LastMessageId);
        Assert.Equal(0, group.UnreadCount);
    }

    [Fact]
    public async Task GetGroups_WithReadBaseline_CountsOnlyNewerMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "1",
            EventTimestamp = now.AddMinutes(-2), ReceivedAt = now.AddMinutes(-2)
        };
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(first);
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "text", Text = "2",
                EventTimestamp = now.AddMinutes(-1), ReceivedAt = now.AddMinutes(-1)
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e3", LineMessageId = "m3", GroupId = "G1", MessageType = "text", Text = "3",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        // baseline = 第一則的 Id，之後兩則算未讀
        var response = await _fixture.Client.PostAsJsonAsync("/api/groups/list", new { read = new Dictionary<string, long> { ["G1"] = first.Id } });
        response.EnsureSuccessStatusCode();
        var groups = await response.Content.ReadFromJsonAsync<List<GroupDto>>();

        Assert.Equal(2, Assert.Single(groups!).UnreadCount);
    }

    [Fact]
    public async Task GetGroups_UnreadCount_CappedAt100()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            for (var i = 0; i < 105; i++)
            {
                dbContext.GroupMessages.Add(new GroupMessage
                {
                    WebhookEventId = $"e{i}", LineMessageId = $"m{i}", GroupId = "G1", MessageType = "text", Text = "x",
                    EventTimestamp = now.AddSeconds(i), ReceivedAt = now.AddSeconds(i)
                });
            }
            await Task.CompletedTask;
        });

        // baseline = 0：全部 105 則都算未讀，但要在 SQL 端截斷成上限 100
        var response = await _fixture.Client.PostAsJsonAsync("/api/groups/list", new { read = new Dictionary<string, long> { ["G1"] = 0 } });
        response.EnsureSuccessStatusCode();
        var groups = await response.Content.ReadFromJsonAsync<List<GroupDto>>();

        Assert.Equal(100, Assert.Single(groups!).UnreadCount);
    }

    [Fact]
    public async Task GetGroups_EmptyBody_ReturnsOkWithZeroUnread()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G2", MessageType = "text", Text = "hello",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        // body 為 {}（沒有 read 欄位）時回 200，且群組清單長度正確、未讀數為 0
        var response = await _fixture.Client.PostAsJsonAsync("/api/groups/list", new { });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var groups = await response.Content.ReadFromJsonAsync<List<GroupDto>>();
        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);
        Assert.All(groups, g => Assert.Equal(0, g.UnreadCount));
    }

    [Fact]
    public async Task GetGroups_NonExistentGroupInReadBaseline_ReturnsOkAndDoesNotAffectOtherGroups()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new GroupMessage
        {
            WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "1",
            EventTimestamp = now.AddMinutes(-2), ReceivedAt = now.AddMinutes(-2)
        };
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(first);
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "text", Text = "2",
                EventTimestamp = now.AddMinutes(-1), ReceivedAt = now.AddMinutes(-1)
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e3", LineMessageId = "m3", GroupId = "G2", MessageType = "text", Text = "3",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        // 帶了 G1 的基準（有 1 則新訊息）以及不存在的群組 NonExistentGroup
        var response = await _fixture.Client.PostAsJsonAsync("/api/groups/list", new
        {
            read = new Dictionary<string, long>
            {
                ["G1"] = first.Id,
                ["NonExistentGroup"] = 99999L
            }
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var groups = await response.Content.ReadFromJsonAsync<List<GroupDto>>();
        Assert.NotNull(groups);
        Assert.Equal(2, groups!.Count);

        var g1 = Assert.Single(groups, g => g.GroupId == "G1");
        Assert.Equal(1, g1.UnreadCount);

        var g2 = Assert.Single(groups, g => g.GroupId == "G2");
        Assert.Equal(0, g2.UnreadCount);
    }

    /// <summary>已讀基準改成 JSON body 之後，「壞掉的輸入」分成兩種行為，這裡把兩種都釘住：
    /// 完全沒有 body（缺漏）當成沒帶基準、回 200；值的型別不對則由 [ApiController] 的模型繫結
    /// 擋在動作方法之前、回 400。舊的 ?read= 字串版是自己解析、壞 pair 一律略過，兩種都回 200，
    /// 這是行為差異，不是退化——唯一的呼叫端是自家前端，送的一定是數字。</summary>
    [Fact]
    public async Task ListGroups_MissingBody_TreatedAsNoBaseline_ButMalformedValueTypeReturns400()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var emptyBody = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var emptyResponse = await _fixture.Client.PostAsync("/api/groups/list", emptyBody);
        Assert.Equal(System.Net.HttpStatusCode.OK, emptyResponse.StatusCode);
        var groups = await emptyResponse.Content.ReadFromJsonAsync<List<GroupDto>>();
        Assert.Equal(0, Assert.Single(groups!).UnreadCount);

        var malformed = new StringContent(
            "{\"read\":{\"G1\":\"abc\"}}", System.Text.Encoding.UTF8, "application/json");
        var malformedResponse = await _fixture.Client.PostAsync("/api/groups/list", malformed);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, malformedResponse.StatusCode);
    }

    [Fact]
    public async Task GetGroups_GroupsPointerDrifted_FallsBackToActualLastMessageAndFixesPointer()
    {
        // 模擬 Groups.LastMessageId 指向一則已經被刪除的訊息（保留期清除跟這支 API 兩次查詢
        // 之間的空檔）：seed 兩則訊息，讓 Groups 追蹤最新那則，然後直接刪掉那則訊息本身，
        // 不透過任何會順手修正 Groups 的路徑
        var now = DateTimeOffset.UtcNow;
        GroupMessage older = null!, newer = null!;
        await _fixture.SeedAsync(async dbContext =>
        {
            older = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "older",
                EventTimestamp = now.AddMinutes(-1), ReceivedAt = now.AddMinutes(-1)
            };
            newer = new GroupMessage
            {
                WebhookEventId = "e2", LineMessageId = "m2", GroupId = "G1", MessageType = "text", Text = "newer",
                EventTimestamp = now, ReceivedAt = now
            };
            dbContext.GroupMessages.Add(older);
            dbContext.GroupMessages.Add(newer);
            await Task.CompletedTask;
        });

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            var newestTracked = await dbContext.GroupMessages.SingleAsync(m => m.WebhookEventId == "e2");
            dbContext.GroupMessages.Remove(newestTracked); // Groups.LastMessageId 現在是懸空指標
            await dbContext.SaveChangesAsync();
        }

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var group = Assert.Single(groups!);
        Assert.Equal(older.Id, group.LastMessageId);
        Assert.Equal("older", group.LastMessagePreview);

        using var verifyScope = _fixture.Factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var fixedGroup = await verifyContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Equal(older.Id, fixedGroup.LastMessageId); // 順手修正，下一輪不用再回退
    }

    [Fact]
    public async Task GetGroups_GroupsPointerDrifted_AllMessagesGone_ExcludesGroupAndClearsPointer()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.GroupMessages.RemoveRange(dbContext.GroupMessages);
            await dbContext.SaveChangesAsync();
        }

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        Assert.Empty(groups!);

        using var verifyScope = _fixture.Factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var group = await verifyContext.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Null(group.LastMessageId);
        Assert.Null(group.LastMessageAt);
    }
    [Fact]
    public async Task GetGroups_OutputsRelativePictureUrl_WhenPictureExists()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "工作群組A",
                Picture = new GroupPicture { GroupId = "G1", Content = new byte[] { 0x00 } },
                UpdatedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var group = Assert.Single(groups!);
        Assert.Equal("api/groups/G1/avatar", group.PictureUrl);
    }

    [Fact]
    public async Task GetGroups_WhenMemberCountExists_UsesGroupsMemberCount()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "工作群組A",
                MemberCount = 42,
                UpdatedAt = now
            });
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U1",
                DisplayName = "User 1",
                UpdatedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var group = Assert.Single(groups!);
        Assert.Equal(42, group.MemberCount);
    }

    [Fact]
    public async Task GetGroups_WhenMemberCountIsNull_FallsBackToCachedGroupMembersCount()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = "G1",
                GroupName = "工作群組A",
                MemberCount = null,
                UpdatedAt = now
            });
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U1",
                DisplayName = "User 1",
                UpdatedAt = now
            });
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1",
                UserId = "U2",
                DisplayName = "User 2",
                UpdatedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var group = Assert.Single(groups!);
        Assert.Equal(2, group.MemberCount);
    }
}
