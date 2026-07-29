using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class ProfileRefreshServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly FakeLineProfileClient _profileClient = new();

    public ProfileRefreshServiceTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();

        var services = new ServiceCollection();
        services.AddDbContext<MessageDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ILineProfileClient>(_profileClient);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private ProfileRefreshService CreateService(ProfileCacheOptions? options = null) =>
        new(
            new FakeProfileRefreshQueue(),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) }),
            NullLogger<ProfileRefreshService>.Instance);

    private async Task<Group?> GetGroupAsync(string groupId)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.Groups.FindAsync(groupId);
    }

    private async Task<GroupMember?> GetMemberAsync(string groupId, string userId)
    {
        using var scope = _provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        return await dbContext.GroupMembers.FindAsync(groupId, userId);
    }

    [Fact]
    public async Task ProcessAsync_NewGroupAndMember_FetchesAndInserts()
    {
        _profileClient.OnGetGroupSummary = _ => new GroupSummary("G1", "工作群組", "https://example/pic.jpg");
        _profileClient.OnGetGroupMemberProfile = (_, userId) => new MemberProfile(userId, "小明", null);

        var service = CreateService();
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        var group = await GetGroupAsync("G1");
        Assert.NotNull(group);
        Assert.Equal("工作群組", group!.GroupName);

        var member = await GetMemberAsync("G1", "U1");
        Assert.NotNull(member);
        Assert.Equal("小明", member!.DisplayName);
    }

    [Fact]
    public async Task ProcessAsync_FreshCache_SkipsApiCall()
    {
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "Cached", UpdatedAt = DateTimeOffset.UtcNow });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "Cached User", UpdatedAt = DateTimeOffset.UtcNow });
            await dbContext.SaveChangesAsync();
        }

        var service = CreateService();
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        Assert.Empty(_profileClient.GroupSummaryCalls);
        Assert.Empty(_profileClient.MemberProfileCalls);
    }

    [Fact]
    public async Task ProcessAsync_StaleCache_RefetchesAndUpdates()
    {
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "Old Name", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-8) });
            await dbContext.SaveChangesAsync();
        }

        _profileClient.OnGetGroupSummary = _ => new GroupSummary("G1", "New Name", null);

        var service = CreateService(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) });
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Single(_profileClient.GroupSummaryCalls);
        var group = await GetGroupAsync("G1");
        Assert.Equal("New Name", group!.GroupName);
    }

    [Fact]
    public async Task ProcessAsync_ApiReturnsNull_DoesNotThrowAndLeavesNoRow()
    {
        _profileClient.OnGetGroupSummary = _ => null;

        var service = CreateService();
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Null(await GetGroupAsync("G1"));
    }

    [Fact]
    public async Task ProcessAsync_NullUserId_OnlyRefreshesGroup()
    {
        var service = CreateService();
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Single(_profileClient.GroupSummaryCalls);
        Assert.Empty(_profileClient.MemberProfileCalls);
    }
}
