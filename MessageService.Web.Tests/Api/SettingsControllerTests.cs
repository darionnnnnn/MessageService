using System.Net;
using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class SettingsControllerTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetDisplaySettings_DefaultsToMaskMiddle()
    {
        var settings = await _fixture.Client.GetFromJsonAsync<DisplaySettingsDto>("/api/settings/display");

        Assert.Equal(nameof(NameDisplayMode.MaskMiddle), settings!.NameDisplayMode);
    }

    [Fact]
    public async Task UpdateDisplaySettings_PersistsNewMode()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/display", new DisplaySettingsDto(nameof(NameDisplayMode.Original)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<DisplaySettingsDto>("/api/settings/display");
        Assert.Equal(nameof(NameDisplayMode.Original), settings!.NameDisplayMode);
    }

    [Fact]
    public async Task UpdateDisplaySettings_InvalidMode_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/display", new DisplaySettingsDto("NotARealMode"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateKeyword_ThenListIncludesIt()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("密碼", null, true, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");

        var found = Assert.Single(keywords!);
        Assert.Equal(created!.Id, found.Id);
        Assert.Equal("密碼", found.Keyword);
        Assert.True(found.ApplyToAllGroups);
        Assert.Empty(found.GroupIds);
    }

    [Fact]
    public async Task CreateKeyword_WithGroupScope_StoresGroupIds()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("secret", "[hidden]", false, ["G1", "G2"]));
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        Assert.False(created!.ApplyToAllGroups);
        Assert.Equal(["G1", "G2"], created.GroupIds.OrderBy(g => g));
        Assert.Equal("[hidden]", created.Replacement);
    }

    [Fact]
    public async Task CreateKeyword_BlankKeyword_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("  ", null, true, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateKeyword_ReplacesGroupScope()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, false, ["G1"]));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var updateResponse = await _fixture.Client.PutAsJsonAsync(
            $"/api/settings/keywords/{created!.Id}", new UpsertMaskKeywordDto("word2", "X", false, ["G2", "G3"]));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        var updated = Assert.Single(keywords!);
        Assert.Equal("word2", updated.Keyword);
        Assert.Equal("X", updated.Replacement);
        Assert.Equal(["G2", "G3"], updated.GroupIds.OrderBy(g => g));
    }

    [Fact]
    public async Task UpdateKeyword_SwitchingToApplyToAllGroups_ClearsPreviousGroupScope()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, false, ["G1", "G2"]));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var updateResponse = await _fixture.Client.PutAsJsonAsync(
            $"/api/settings/keywords/{created!.Id}", new UpsertMaskKeywordDto("word", null, true, null));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        var updated = Assert.Single(keywords!);
        Assert.True(updated.ApplyToAllGroups);
        Assert.Empty(updated.GroupIds);
    }

    [Fact]
    public async Task CreateKeyword_ApplyToAllGroupsTrue_IgnoresSuppliedGroupIds()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, true, ["G1", "G2"]));
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        Assert.True(created!.ApplyToAllGroups);
        Assert.Empty(created.GroupIds);
    }

    [Fact]
    public async Task UpdateKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/keywords/999", new UpsertMaskKeywordDto("x", null, true, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKeyword_RemovesIt()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("temp", null, true, null));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/settings/keywords/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        Assert.Empty(keywords!);
    }

    [Fact]
    public async Task DeleteKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/keywords/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpsertAlias_ThenGetAliases_ReturnsIt()
    {
        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("值班A"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        var alias = Assert.Single(aliases!);
        Assert.Equal("U1", alias.UserId);
        Assert.Equal("值班A", alias.Alias);
    }

    [Fact]
    public async Task UpsertAlias_CalledTwice_UpdatesExistingRow()
    {
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("First"));
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("Second"));

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        var alias = Assert.Single(aliases!);
        Assert.Equal("Second", alias.Alias);
    }

    [Fact]
    public async Task DeleteAlias_RemovesIt()
    {
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("值班A"));

        var deleteResponse = await _fixture.Client.DeleteAsync("/api/settings/aliases/U1");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        Assert.Empty(aliases!);
    }

    [Fact]
    public async Task DeleteAlias_NonExistentUser_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/aliases/NoSuchUser");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_FiltersByGroupId_AndDedupesAcrossGroups()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G2", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G2", UserId = "U2", DisplayName = "大華", UpdatedAt = now });
            await Task.CompletedTask;
        });

        var g1Users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users?groupId=G1");
        Assert.Single(g1Users!);

        var allUsers = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");
        Assert.Equal(2, allUsers!.Count);
    }
}
