using System.Net;
using System.Net.Http.Json;
using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Web.Tests.Api;

public class GroupDeletionTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DeleteMessages_RemovesMessagesButKeepsGroup()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G1";
        const string userId = "U1";

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = groupId,
                GroupName = "測試群組",
                Picture = new GroupPicture { GroupId = groupId, Content = [0x01, 0x02] },
                UpdatedAt = now
            });

            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = "成員甲",
                Picture = new GroupMemberPicture { GroupId = groupId, UserId = userId, Content = [0x03] },
                UpdatedAt = now
            });

            dbContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = groupId,
                UserId = userId,
                IconKey = "cat",
                Label = "小貓",
                AssignedAt = now
            });

            var keyword = new MaskKeyword
            {
                Keyword = "機密",
                ApplyToAllGroups = false
            };
            dbContext.MaskKeywords.Add(keyword);
            dbContext.MaskKeywordGroups.Add(new MaskKeywordGroup
            {
                MaskKeyword = keyword,
                GroupId = groupId
            });

            dbContext.UserAliases.Add(new UserAlias
            {
                UserId = userId,
                Alias = "小明"
            });

            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "e1",
                    LineMessageId = "m1",
                    GroupId = groupId,
                    UserId = userId,
                    MessageType = "text",
                    Text = "第一則訊息",
                    EventTimestamp = now.AddMinutes(-10),
                    ReceivedAt = now.AddMinutes(-10)
                },
                new GroupMessage
                {
                    WebhookEventId = "e2",
                    LineMessageId = "m2",
                    GroupId = groupId,
                    UserId = userId,
                    MessageType = "image",
                    EventTimestamp = now,
                    ReceivedAt = now,
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Blob = new MessageContentBlob { Content = [0xAA, 0xBB] },
                        ContentType = "image/jpeg"
                    }
                });

            await Task.CompletedTask;
        });

        var response = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GroupDeletionResultDto>();
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.MessageCount);
        Assert.Equal(0, dto.MemberCount);
        Assert.Equal(0, dto.AnonymousIdentityCount);
        Assert.Equal(0, dto.MaskKeywordScopeCount);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        Assert.Empty(await db.GroupMessages.Where(m => m.GroupId == groupId).ToListAsync());
        Assert.Empty(await db.MessageContents.ToListAsync());
        Assert.Empty(await db.MessageContentBlobs.ToListAsync());

        var group = await db.Groups.AsNoTracking().SingleAsync(g => g.GroupId == groupId);
        Assert.Null(group.LastMessageId);
        Assert.Null(group.LastMessageAt);
        Assert.Equal("測試群組", group.GroupName);

        Assert.Equal(1, await db.GroupPictures.CountAsync(p => p.GroupId == groupId));
        Assert.Equal(1, await db.GroupMembers.CountAsync(m => m.GroupId == groupId));
        Assert.Equal(1, await db.GroupMemberPictures.CountAsync(p => p.GroupId == groupId));
        Assert.Equal(1, await db.AnonymousIdentities.CountAsync(a => a.GroupId == groupId));
        Assert.Equal(1, await db.MaskKeywordGroups.CountAsync(g => g.GroupId == groupId));
        Assert.Equal(1, await db.UserAliases.CountAsync(u => u.UserId == userId));
        // 只刪訊息的路徑不碰高亮規則，回傳的計數必為 0
        Assert.Equal(0, dto.HighlightScopeCount);
    }

    [Fact]
    public async Task DeleteMessages_DoesNotAffectOtherGroups()
    {
        var now = DateTimeOffset.UtcNow;
        GroupMessage g2LastMessage = null!;

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "g1-e1", LineMessageId = "g1-m1", GroupId = "G1", MessageType = "text", Text = "g1-1",
                    EventTimestamp = now.AddMinutes(-5), ReceivedAt = now.AddMinutes(-5)
                },
                new GroupMessage
                {
                    WebhookEventId = "g1-e2", LineMessageId = "g1-m2", GroupId = "G1", MessageType = "text", Text = "g1-2",
                    EventTimestamp = now, ReceivedAt = now
                });

            var g2Msg1 = new GroupMessage
            {
                WebhookEventId = "g2-e1", LineMessageId = "g2-m1", GroupId = "G2", MessageType = "text", Text = "g2-1",
                EventTimestamp = now.AddMinutes(-5), ReceivedAt = now.AddMinutes(-5)
            };
            g2LastMessage = new GroupMessage
            {
                WebhookEventId = "g2-e2", LineMessageId = "g2-m2", GroupId = "G2", MessageType = "text", Text = "g2-2",
                EventTimestamp = now, ReceivedAt = now
            };
            dbContext.GroupMessages.AddRange(g2Msg1, g2LastMessage);

            await Task.CompletedTask;
        });

        var response = await _fixture.Client.DeleteAsync("/api/groups/G1/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        Assert.Equal(0, await db.GroupMessages.CountAsync(m => m.GroupId == "G1"));
        var g1 = await db.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G1");
        Assert.Null(g1.LastMessageId);
        Assert.Null(g1.LastMessageAt);

        Assert.Equal(2, await db.GroupMessages.CountAsync(m => m.GroupId == "G2"));
        var g2 = await db.Groups.AsNoTracking().SingleAsync(g => g.GroupId == "G2");
        Assert.Equal(g2LastMessage.Id, g2.LastMessageId);
        Assert.NotNull(g2.LastMessageAt);
    }

    [Fact]
    public async Task DeleteMessages_DeletesMoreThanOneBatch()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G_BATCH";

        await _fixture.SeedAsync(async dbContext =>
        {
            for (var i = 0; i < 1200; i++)
            {
                dbContext.GroupMessages.Add(new GroupMessage
                {
                    WebhookEventId = $"batch-e-{i}",
                    LineMessageId = $"batch-m-{i}",
                    GroupId = groupId,
                    MessageType = "text",
                    Text = $"msg-{i}",
                    EventTimestamp = now.AddSeconds(i),
                    ReceivedAt = now.AddSeconds(i)
                });
            }
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}/messages");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GroupDeletionResultDto>();
        Assert.NotNull(dto);
        Assert.Equal(1200, dto!.MessageCount);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        Assert.Equal(0, await db.GroupMessages.CountAsync(m => m.GroupId == groupId));
    }

    [Fact]
    public async Task DeleteGroup_RemovesEverythingScopedToGroup()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G1";
        const string userId = "U1";

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = groupId,
                GroupName = "測試群組",
                Picture = new GroupPicture { GroupId = groupId, Content = [0x01, 0x02] },
                UpdatedAt = now
            });

            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = "成員甲",
                Picture = new GroupMemberPicture { GroupId = groupId, UserId = userId, Content = [0x03] },
                UpdatedAt = now
            });

            dbContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = groupId,
                UserId = userId,
                IconKey = "cat",
                Label = "小貓",
                AssignedAt = now
            });

            var keyword = new MaskKeyword
            {
                Keyword = "機密",
                ApplyToAllGroups = false
            };
            dbContext.MaskKeywords.Add(keyword);
            dbContext.MaskKeywordGroups.Add(new MaskKeywordGroup
            {
                MaskKeyword = keyword,
                GroupId = groupId
            });

            dbContext.UserAliases.Add(new UserAlias
            {
                UserId = userId,
                Alias = "小明"
            });

            dbContext.GroupMessages.AddRange(
                new GroupMessage
                {
                    WebhookEventId = "e1",
                    LineMessageId = "m1",
                    GroupId = groupId,
                    UserId = userId,
                    MessageType = "text",
                    Text = "第一則訊息",
                    EventTimestamp = now.AddMinutes(-10),
                    ReceivedAt = now.AddMinutes(-10)
                },
                new GroupMessage
                {
                    WebhookEventId = "e2",
                    LineMessageId = "m2",
                    GroupId = groupId,
                    UserId = userId,
                    MessageType = "image",
                    EventTimestamp = now,
                    ReceivedAt = now,
                    Content = new MessageContent
                    {
                        DownloadStatus = DownloadStatus.Completed,
                        Blob = new MessageContentBlob { Content = [0xAA, 0xBB] },
                        ContentType = "image/jpeg"
                    }
                });

            // 高亮規則：一條逐群組的關鍵字範圍列、一筆綁該群組的人員規則、
            // 一筆「全部群組」的人員規則（後者不可以被刪掉）
            var highlightKeyword = new HighlightKeyword
            {
                Keyword = "警示",
                ApplyToAllGroups = false
            };
            dbContext.HighlightKeywords.Add(highlightKeyword);
            dbContext.HighlightKeywordGroups.Add(new HighlightKeywordGroup
            {
                HighlightKeyword = highlightKeyword,
                GroupId = groupId
            });
            dbContext.HighlightUsers.Add(new HighlightUser { UserId = userId, GroupId = groupId });
            dbContext.HighlightUsers.Add(new HighlightUser { UserId = userId, GroupId = null });

            // G2 作為隔離驗證對照
            dbContext.Groups.Add(new Group
            {
                GroupId = "G2",
                GroupName = "對照群組",
                UpdatedAt = now
            });
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G2",
                UserId = "U2",
                DisplayName = "成員乙",
                UpdatedAt = now
            });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "g2-e1",
                LineMessageId = "g2-m1",
                GroupId = "G2",
                UserId = "U2",
                MessageType = "text",
                Text = "G2 訊息",
                EventTimestamp = now,
                ReceivedAt = now
            });

            await Task.CompletedTask;
        });

        var response = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GroupDeletionResultDto>();
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.MessageCount);
        Assert.Equal(1, dto.MemberCount);
        Assert.Equal(1, dto.AnonymousIdentityCount);
        Assert.Equal(1, dto.MaskKeywordScopeCount);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        Assert.Equal(0, await db.Groups.CountAsync(g => g.GroupId == groupId));
        Assert.Equal(0, await db.GroupPictures.CountAsync(p => p.GroupId == groupId));
        Assert.Equal(0, await db.GroupMembers.CountAsync(m => m.GroupId == groupId));
        Assert.Equal(0, await db.GroupMemberPictures.CountAsync(p => p.GroupId == groupId));
        Assert.Equal(0, await db.AnonymousIdentities.CountAsync(a => a.GroupId == groupId));
        Assert.Equal(0, await db.MaskKeywordGroups.CountAsync(g => g.GroupId == groupId));
        Assert.Equal(0, await db.GroupMessages.CountAsync(m => m.GroupId == groupId));
        Assert.Empty(await db.MessageContents.ToListAsync());
        Assert.Empty(await db.MessageContentBlobs.ToListAsync());

        // 高亮規則：綁這個群組的兩筆都要清掉，「全部群組」那筆與關鍵字本體要留著
        Assert.Equal(0, await db.HighlightKeywordGroups.CountAsync(g => g.GroupId == groupId));
        Assert.Equal(0, await db.HighlightUsers.CountAsync(u => u.GroupId == groupId));
        Assert.Equal(1, await db.HighlightUsers.CountAsync(u => u.GroupId == null));
        Assert.True(await db.HighlightKeywords.AnyAsync(k => k.Keyword == "警示"));
        Assert.Equal(2, dto.HighlightScopeCount);

        // MaskKeywords 本體仍在
        Assert.True(await db.MaskKeywords.AnyAsync(k => k.Keyword == "機密"));
        // UserAliases 仍在
        Assert.True(await db.UserAliases.AnyAsync(u => u.UserId == userId));

        // G2 完全不受影響
        Assert.Equal(1, await db.Groups.CountAsync(g => g.GroupId == "G2"));
        Assert.Equal(1, await db.GroupMembers.CountAsync(m => m.GroupId == "G2"));
        Assert.Equal(1, await db.GroupMessages.CountAsync(m => m.GroupId == "G2"));
    }

    [Fact]
    public async Task DeleteGroup_ReturnsNotFound_WhenGroupMissing()
    {
        var response = await _fixture.Client.DeleteAsync("/api/groups/NonExistentGroup");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMessages_ReturnsNotFound_WhenGroupMissing()
    {
        var response = await _fixture.Client.DeleteAsync("/api/groups/NonExistentGroup/messages");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGroup_Twice_SecondReturnsNotFound()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G1";

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = groupId, MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var firstResponse = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        var secondResponse = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteMessages_ThenGroupListExcludesGroup()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G1";

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = groupId, MessageType = "text", Text = "hi",
                EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}/messages");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");
        Assert.NotNull(groups);
        Assert.DoesNotContain(groups!, g => g.GroupId == groupId);
    }

    [Fact]
    public async Task DeleteGroup_ThenNewMessageRecreatesGroup()
    {
        var now = DateTimeOffset.UtcNow;
        const string groupId = "G1";

        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = groupId, MessageType = "text", Text = "old",
                EventTimestamp = now.AddMinutes(-5), ReceivedAt = now.AddMinutes(-5)
            });
            await Task.CompletedTask;
        });

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/groups/{groupId}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        using (var verifyScope = _fixture.Factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
            Assert.False(await db.Groups.AnyAsync(g => g.GroupId == groupId));
        }

        GroupMessage newMessage = null!;
        await _fixture.SeedAsync(async dbContext =>
        {
            newMessage = new GroupMessage
            {
                WebhookEventId = "e-new",
                LineMessageId = "m-new",
                GroupId = groupId,
                MessageType = "text",
                Text = "reborn message",
                EventTimestamp = now,
                ReceivedAt = now
            };
            dbContext.GroupMessages.Add(newMessage);
            await Task.CompletedTask;
        });

        using (var verifyScope = _fixture.Factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<MessageDbContext>();
            var group = await db.Groups.AsNoTracking().SingleAsync(g => g.GroupId == groupId);
            Assert.Equal(newMessage.Id, group.LastMessageId);
        }
    }
}
