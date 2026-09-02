using System.Net;
using System.Net.Http.Json;
using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Web.Tests.Api;

public class HighlightSettingsTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task CreateKeyword_ThenListIncludesIt()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("VIP", true, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<HighlightKeywordDto>();

        var keywords = await _fixture.Client.GetFromJsonAsync<List<HighlightKeywordDto>>("/api/settings/highlight-keywords");

        var found = Assert.Single(keywords!);
        Assert.Equal(created!.Id, found.Id);
        Assert.Equal("VIP", found.Keyword);
        Assert.True(found.ApplyToAllGroups);
        Assert.Empty(found.GroupIds);
    }

    [Fact]
    public async Task CreateKeyword_WithGroupScope_StoresGroupIds()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("緊急", false, ["G1", "G2"]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<HighlightKeywordDto>();

        Assert.NotNull(created);
        Assert.False(created.ApplyToAllGroups);
        Assert.Equal(["G1", "G2"], created.GroupIds.OrderBy(g => g));

        var keywords = await _fixture.Client.GetFromJsonAsync<List<HighlightKeywordDto>>("/api/settings/highlight-keywords");
        var found = Assert.Single(keywords!);
        Assert.False(found.ApplyToAllGroups);
        Assert.Equal(["G1", "G2"], found.GroupIds.OrderBy(g => g));
    }

    [Fact]
    public async Task UpdateKeyword_ReplacesGroupScope_OnlyKeepsNewGroupInDb()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("重要", false, ["G1", "G2", "G3"]));
        var created = await createResponse.Content.ReadFromJsonAsync<HighlightKeywordDto>();

        var updateResponse = await _fixture.Client.PutAsJsonAsync(
            $"/api/settings/highlight-keywords/{created!.Id}", new UpsertHighlightKeywordDto("重要更新", false, ["G1"]));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedDto = await updateResponse.Content.ReadFromJsonAsync<HighlightKeywordDto>();
        Assert.NotNull(updatedDto);
        Assert.Equal("重要更新", updatedDto.Keyword);
        Assert.Equal(["G1"], updatedDto.GroupIds);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<HighlightKeywordDto>>("/api/settings/highlight-keywords");
        var found = Assert.Single(keywords!);
        Assert.Equal("重要更新", found.Keyword);
        Assert.Equal(["G1"], found.GroupIds);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupRows = await db.HighlightKeywordGroups
            .Where(g => g.HighlightKeywordId == created.Id)
            .ToListAsync();
        Assert.Single(groupRows);
        Assert.Equal("G1", groupRows[0].GroupId);
    }

    [Fact]
    public async Task DeleteKeyword_RemovesIt_CascadesGroupRows()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("暫存", false, ["G1", "G2"]));
        var created = await createResponse.Content.ReadFromJsonAsync<HighlightKeywordDto>();

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/settings/highlight-keywords/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<HighlightKeywordDto>>("/api/settings/highlight-keywords");
        Assert.Empty(keywords!);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var groupCount = await db.HighlightKeywordGroups
            .CountAsync(g => g.HighlightKeywordId == created.Id);
        Assert.Equal(0, groupCount);
    }

    [Fact]
    public async Task CreateKeyword_BlankKeyword_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("   ", true, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithNullGroupId_ReturnsNullGroupIdAndNullGroupName()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<HighlightUserDto>();

        Assert.NotNull(created);
        Assert.Equal("U1", created.UserId);
        Assert.Null(created.GroupId);
        Assert.Null(created.GroupName);
        Assert.Equal("U1", created.DisplayName);

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        var found = Assert.Single(users!);
        Assert.Equal("U1", found.UserId);
        Assert.Null(found.GroupId);
        Assert.Null(found.GroupName);
        Assert.Equal("U1", found.DisplayName);
    }

    [Fact]
    public async Task CreateUser_WithSpecifiedGroupId_ReturnsGroupName()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async db =>
        {
            db.Groups.Add(new Group { GroupId = "G_TEST", GroupName = "測試群組", UpdatedAt = now });
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = "G_TEST",
                UserId = "U2",
                DisplayName = "王小明",
                UpdatedAt = now
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U2", "G_TEST"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<HighlightUserDto>();

        Assert.NotNull(created);
        Assert.Equal("U2", created.UserId);
        Assert.Equal("G_TEST", created.GroupId);
        Assert.Equal("測試群組", created.GroupName);
        Assert.Equal("王小明", created.DisplayName);

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        var found = Assert.Single(users!);
        Assert.Equal("U2", found.UserId);
        Assert.Equal("G_TEST", found.GroupId);
        Assert.Equal("測試群組", found.GroupName);
        Assert.Equal("王小明", found.DisplayName);
    }

    [Fact]
    public async Task CreateUser_DuplicatePost_IsIdempotent()
    {
        var res1 = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", null));
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var dto1 = await res1.Content.ReadFromJsonAsync<HighlightUserDto>();

        var res2 = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", null));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var dto2 = await res2.Content.ReadFromJsonAsync<HighlightUserDto>();

        Assert.NotNull(dto1);
        Assert.NotNull(dto2);
        Assert.Equal(dto1.Id, dto2.Id);

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        Assert.Single(users!);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var dbCount = await db.HighlightUsers.CountAsync(u => u.UserId == "U1");
        Assert.Equal(1, dbCount);
    }

    [Fact]
    public async Task CreateUser_SameUserIdDifferentGroupId_CanCoexist()
    {
        var res1 = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", null));
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);

        var res2 = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", "G1"));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        Assert.Equal(2, users!.Count);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var dbCount = await db.HighlightUsers.CountAsync(u => u.UserId == "U1");
        Assert.Equal(2, dbCount);
    }

    [Fact]
    public async Task DeleteUser_ExistingReturnsNoContent_ThenReturnsNotFound()
    {
        var res = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U1", null));
        var created = await res.Content.ReadFromJsonAsync<HighlightUserDto>();

        var del1 = await _fixture.Client.DeleteAsync($"/api/settings/highlight-users/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del1.StatusCode);

        var del2 = await _fixture.Client.DeleteAsync($"/api/settings/highlight-users/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, del2.StatusCode);

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        Assert.Empty(users!);
    }

    [Fact]
    public async Task CreateUser_BlankUserId_ReturnsBadRequest()
    {
        var res = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("   ", null));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GetUsers_InAnonymousMode_NeverReturnsRealDisplayName()
    {
        var now = DateTimeOffset.UtcNow;
        const string realName = "真實小明";
        const string userId = "U_ANON_TEST";
        const string groupId = "G_ANON_TEST";

        await _fixture.SeedAsync(async db =>
        {
            var settings = await db.ViewerSettings.FirstOrDefaultAsync(v => v.Id == ViewerSettings.SingletonId);
            if (settings != null)
            {
                settings.NameDisplayMode = NameDisplayMode.Anonymous;
            }
            else
            {
                db.ViewerSettings.Add(new ViewerSettings { NameDisplayMode = NameDisplayMode.Anonymous });
            }

            db.Groups.Add(new Group { GroupId = groupId, GroupName = "匿名群組", UpdatedAt = now });
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = realName,
                UpdatedAt = now
            });
            db.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = groupId,
                UserId = userId,
                IconKey = "icon_bear",
                Label = "成員 #1",
                AssignedAt = now
            });
            await Task.CompletedTask;
        });

        await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto(userId, groupId));

        var users = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        var found = Assert.Single(users!);
        Assert.NotEqual(realName, found.DisplayName);
        Assert.Equal("成員 #1", found.DisplayName);
    }

    [Fact]
    public async Task GetHighlightRules_ReturnsBothKeywordsAndUsers_ConsistentWithIndividualEndpoints()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async db =>
        {
            db.Groups.Add(new Group { GroupId = "G_RULES", GroupName = "規則群組", UpdatedAt = now });
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = "G_RULES",
                UserId = "U_RULES",
                DisplayName = "測試人員",
                UpdatedAt = now
            });
            await Task.CompletedTask;
        });

        await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-keywords", new UpsertHighlightKeywordDto("急件", false, ["G_RULES"]));
        await _fixture.Client.PostAsJsonAsync(
            "/api/settings/highlight-users", new UpsertHighlightUserDto("U_RULES", "G_RULES"));

        var kwList = await _fixture.Client.GetFromJsonAsync<List<HighlightKeywordDto>>("/api/settings/highlight-keywords");
        var userList = await _fixture.Client.GetFromJsonAsync<List<HighlightUserDto>>("/api/settings/highlight-users");
        var rules = await _fixture.Client.GetFromJsonAsync<HighlightRulesDto>("/api/settings/highlight-rules");

        Assert.NotNull(rules);
        Assert.Equal(kwList!.Count, rules.Keywords.Count);
        Assert.Equal(userList!.Count, rules.Users.Count);

        var kw = Assert.Single(rules.Keywords);
        Assert.Equal("急件", kw.Keyword);
        Assert.Equal(["G_RULES"], kw.GroupIds);

        var user = Assert.Single(rules.Users);
        Assert.Equal("U_RULES", user.UserId);
        Assert.Equal("G_RULES", user.GroupId);
        Assert.Equal("規則群組", user.GroupName);
        Assert.Equal("測試人員", user.DisplayName);
    }

    [Fact]
    public async Task UpdateKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/highlight-keywords/9999", new UpsertHighlightKeywordDto("xyz", true, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/highlight-keywords/9999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
