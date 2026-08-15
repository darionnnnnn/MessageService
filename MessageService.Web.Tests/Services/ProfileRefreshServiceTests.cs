using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddSingleton(MessageService.Data.Crypto.FieldCipher.Disabled);
        services.AddScoped<IProfileStore, DbProfileStore>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
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

    // === 失敗冷卻：LINE profile API 失敗（暫時性故障／bot 已被踢出群組）後，同一個群組／成員
    // 在冷卻時間內不該每一則訊息都再打一次 API，見 ProfileCacheOptions.FailureRetryAfter ===

    [Fact]
    public async Task ProcessAsync_GroupApiFails_SecondCallWithinCooldown_SkipsApiCall()
    {
        _profileClient.OnGetGroupSummary = _ => null;
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMinutes(10)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Single(_profileClient.GroupSummaryCalls);
    }

    [Fact]
    public async Task ProcessAsync_GroupApiFails_AfterCooldownExpires_RetriesApiCall()
    {
        _profileClient.OnGetGroupSummary = _ => null;
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMilliseconds(20)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        await Task.Delay(100);
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Equal(2, _profileClient.GroupSummaryCalls.Count);
    }

    [Fact]
    public async Task ProcessAsync_GroupApiSucceedsAfterPriorFailure_ClearsCooldown()
    {
        var succeed = false;
        _profileClient.OnGetGroupSummary = groupId => succeed ? new GroupSummary(groupId, "Recovered", null) : null;
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMilliseconds(20)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        await Task.Delay(100);
        succeed = true;
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        var group = await GetGroupAsync("G1");
        Assert.Equal("Recovered", group!.GroupName);
    }

    [Fact]
    public async Task ProcessAsync_MemberApiFails_SecondCallWithinCooldown_SkipsApiCall()
    {
        // Group 快取先設成新鮮，這裡只想單獨看 member 冷卻的行為，不想連 group 也一起打 API
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "Cached", UpdatedAt = DateTimeOffset.UtcNow });
            await dbContext.SaveChangesAsync();
        }
        _profileClient.OnGetGroupMemberProfile = (_, _) => null;
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMinutes(10)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        Assert.Single(_profileClient.MemberProfileCalls);
        Assert.Empty(_profileClient.GroupSummaryCalls);
    }

    [Fact]
    public async Task ProcessAsync_MemberApiFails_DifferentGroupSameUser_IsNotAffectedByOtherGroupsCooldown()
    {
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "Cached", UpdatedAt = DateTimeOffset.UtcNow });
            dbContext.Groups.Add(new Group { GroupId = "G2", GroupName = "Cached", UpdatedAt = DateTimeOffset.UtcNow });
            await dbContext.SaveChangesAsync();
        }
        _profileClient.OnGetGroupMemberProfile = (_, _) => null;
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMinutes(10)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G2", "U1"), CancellationToken.None);

        // 冷卻鍵含 GroupId，不同群組的同一個 UserId 不該互相影響
        Assert.Equal(2, _profileClient.MemberProfileCalls.Count);
    }

    private (ProfileRefreshService Service, FakeProfileStore Store) CreateServiceWithFakeStore(
        ProfileCacheOptions? options = null,
        ProfileStaleness? stalenessToReturn = null)
    {
        var store = new FakeProfileStore();
        if (stalenessToReturn is not null)
        {
            store.StalenessToReturn = stalenessToReturn;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(options ?? new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) }),
            NullLogger<ProfileRefreshService>.Instance);

        return (service, store);
    }

    [Fact]
    public async Task ProcessAsync_SameTaskCalledTwice_ShortCircuitsAndQueriesStalenessOnlyOnce()
    {
        var (service, store) = CreateServiceWithFakeStore(stalenessToReturn: new ProfileStaleness(false, false));

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        Assert.Single(store.GetStalenessCalls);
    }

    [Fact]
    public async Task ProcessAsync_DifferentUserInSameGroup_QueriesStalenessAgain()
    {
        var (service, store) = CreateServiceWithFakeStore(stalenessToReturn: new ProfileStaleness(false, false));

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U2"), CancellationToken.None);

        Assert.Equal(2, store.GetStalenessCalls.Count);
    }

    [Fact]
    public async Task ProcessAsync_ProfileClientThrows_RecordsCooldownAndSkipsSubsequentCalls()
    {
        _profileClient.OnGetGroupSummary = _ => throw new HttpRequestException("LINE API error");
        var (service, _) = CreateServiceWithFakeStore(
            options: new ProfileCacheOptions
            {
                RefreshAfter = TimeSpan.FromDays(7),
                FailureRetryAfter = TimeSpan.FromMinutes(10)
            },
            stalenessToReturn: new ProfileStaleness(true, false));

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        Assert.Single(_profileClient.GroupSummaryCalls);
    }

    [Fact]
    public async Task ProcessAsync_GroupSuppressedButMemberNot_StillQueriesStaleness()
    {
        var (service, store) = CreateServiceWithFakeStore(stalenessToReturn: new ProfileStaleness(false, false));

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Single(store.GetStalenessCalls);

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(2, store.GetStalenessCalls.Count);
    }

    [Fact]
    public async Task ProcessAsync_ExceedsCleanupThreshold_PerformsExpiredEntriesCleanup()
    {
        var (service, _) = CreateServiceWithFakeStore(
            options: new ProfileCacheOptions
            {
                RefreshAfter = TimeSpan.FromDays(7),
                FailureRetryAfter = TimeSpan.FromMilliseconds(1)
            },
            stalenessToReturn: new ProfileStaleness(false, false));

        for (var i = 0; i < 1005; i++)
        {
            await service.ProcessAsync(new ProfileRefreshTask($"G_{i}", null), CancellationToken.None);
        }

        await Task.Delay(10);
        await service.ProcessAsync(new ProfileRefreshTask("G_Trigger", null), CancellationToken.None);
    }
}
