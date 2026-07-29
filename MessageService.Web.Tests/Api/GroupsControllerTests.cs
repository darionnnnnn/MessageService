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
}
