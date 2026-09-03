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
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan delta) => Now = Now.Add(delta);
    }

    private readonly FakeTimeProvider _time = new();

    /// <summary>staleness 查詢的內部通道設定。空 BaseUrl＝本機資料庫（AllInOne／Core），
    /// 有值＝Edge 打 Core 的 ingest API。</summary>
    private static Microsoft.Extensions.Options.IOptions<IngestOptions> IngestOpts(string? baseUrl = null) =>
        OptionsFactory.Create(new IngestOptions { BaseUrl = baseUrl });

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
            IngestOpts(),
            _time,
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

        // 冷卻未到期不重打
        _time.Advance(TimeSpan.FromMilliseconds(19));
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);

        // 過了 FailureRetryAfter 才重打
        _time.Advance(TimeSpan.FromMilliseconds(2));
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
        _time.Advance(TimeSpan.FromMilliseconds(21));
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
            IngestOpts(),
            _time,
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

        _time.Advance(TimeSpan.FromMilliseconds(10));
        await service.ProcessAsync(new ProfileRefreshTask("G_Trigger", null), CancellationToken.None);
    }

    [Fact]
    public async Task ProcessAsync_GroupPictureDownloadFails_UpsertsNameAndEntersFailureCooldown()
    {
        _profileClient.OnGetGroupSummary = groupId => new GroupSummary(groupId, "GroupName", "https://example.com/pic.jpg", PictureDownloadFailed: true);
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7),
            FailureRetryAfter = TimeSpan.FromMilliseconds(50)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        // 1. 名稱仍然有 upsert 入庫
        var group = await GetGroupAsync("G1");
        Assert.NotNull(group);
        Assert.Equal("GroupName", group!.GroupName);
        Assert.Equal("https://example.com/pic.jpg", group.PictureUrl);
        Assert.Null(group.PictureFetchedUrl);

        // 2. 處於失敗冷卻內（50ms），未過期時跳過
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);

        // 3. 失敗冷卻過期後重試（如果是成功抑制 5 分鐘則此時仍會被跳過）
        _time.Advance(TimeSpan.FromMilliseconds(51));
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Equal(2, _profileClient.GroupSummaryCalls.Count);
    }

    [Fact]
    public async Task ProcessAsync_MemberPictureDownloadFails_UpsertsNameAndEntersFailureCooldown()
    {
        using (var scope = _provider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "Cached", UpdatedAt = DateTimeOffset.UtcNow });
            await dbContext.SaveChangesAsync();
        }

        _profileClient.OnGetGroupMemberProfile = (_, userId) => new MemberProfile(userId, "UserName", "https://example.com/pic.jpg", PictureDownloadFailed: true);
        var service = CreateService(new ProfileCacheOptions
        {
            RefreshAfter = TimeSpan.FromDays(7),
            FailureRetryAfter = TimeSpan.FromMilliseconds(50)
        });

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        // 1. 名稱仍然有 upsert 入庫
        var member = await GetMemberAsync("G1", "U1");
        Assert.NotNull(member);
        Assert.Equal("UserName", member!.DisplayName);
        Assert.Equal("https://example.com/pic.jpg", member.PictureUrl);
        Assert.Null(member.PictureFetchedUrl);

        // 2. 處於失敗冷卻內（50ms），未過期時跳過
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Single(_profileClient.MemberProfileCalls);

        // 3. 失敗冷卻過期後重試（如果是成功抑制 5 分鐘則此時仍會被跳過）
        _time.Advance(TimeSpan.FromMilliseconds(51));
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(2, _profileClient.MemberProfileCalls.Count);
    }

    private sealed class CapturingLogger : ILogger<ProfileRefreshService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>staleness 查詢打的是內部通道（Edge→Core 的 ingest API），不是 LINE。
    /// 標成 api.line.me 會讓維運去開錯誤的防火牆洞——實測踩過這個坑。</summary>
    [Fact]
    public async Task ProcessAsync_StalenessFails_LogsInternalChannelTarget_NotLineDomain()
    {
        var store = new ThrowingProfileStore((_, _, _) =>
            throw new HttpRequestException("timeout", new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.TimedOut)));
        var logger = new CapturingLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) }),
            IngestOpts("http://core.example/MSWeb/"),
            _time,
            logger);

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("core.example", warning);
        Assert.Contains("api/ingest/profiles/staleness", warning);
        Assert.DoesNotContain("line.me", warning);
    }

    [Fact]
    public async Task ProcessAsync_StalenessFails_WithoutIngestBaseUrl_LogsLocalDatabaseTarget()
    {
        var store = new ThrowingProfileStore((_, _, _) => throw new InvalidOperationException("db down"));
        var logger = new CapturingLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) }),
            IngestOpts(),
            _time,
            logger);

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        Assert.Contains("本機資料庫", Assert.Single(logger.Warnings));
    }

    /// <summary>內部通道失敗只冷卻 30 秒，不吃 FailureRetryAfter（預設 10 分鐘）。
    /// 通道切換的過渡期只有 180 秒，用 10 分鐘冷卻會把切換後的第一批派工整批抑制掉，
    /// 名稱／頭貼要等十幾分鐘才出得來——這正是實測回報的症狀。</summary>
    [Fact]
    public async Task ProcessAsync_StalenessFails_UsesShortInternalCooldown_NotFailureRetryAfter()
    {
        var store = new ThrowingProfileStore((_, _, _) =>
            throw new HttpRequestException("timeout", new System.Net.Sockets.SocketException(
                (int)System.Net.Sockets.SocketError.TimedOut)));

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions
            {
                RefreshAfter = TimeSpan.FromDays(7),
                FailureRetryAfter = TimeSpan.FromMinutes(10)
            }),
            IngestOpts("http://core.example/"),
            _time,
            NullLogger<ProfileRefreshService>.Instance);

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(1, store.GetStalenessCallCount);

        // 29 秒：仍在冷卻內
        _time.Advance(TimeSpan.FromSeconds(29));
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(1, store.GetStalenessCallCount);

        // 31 秒：冷卻結束，重新查詢（若沿用 FailureRetryAfter 這裡還要再等 9 分半）
        _time.Advance(TimeSpan.FromSeconds(2));
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(2, store.GetStalenessCallCount);
    }

    /// <summary>Edge 打 Core 的 staleness 查詢遇到 HttpClient 逾時時是 TaskCanceledException，
    /// 不是停機——要走內部失敗的短冷卻，不能穿出去（穿出 ExecuteAsync 會讓服務靜默結束）。</summary>
    [Fact]
    public async Task ProcessAsync_StalenessHttpClientTimeout_IsInternalFailure_NotPropagated()
    {
        var store = new ThrowingProfileStore((_, _, _) =>
            throw new TaskCanceledException("timeout", new TimeoutException()));
        var logger = new CapturingLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7) }),
            IngestOpts("http://core.example/"),
            _time,
            logger);

        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        Assert.Contains("core.example", Assert.Single(logger.Warnings));

        _time.Advance(TimeSpan.FromSeconds(31));
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(2, store.GetStalenessCallCount);
    }

    private class ThrowingProfileStore(Func<string, string?, DateTimeOffset, Task<ProfileStaleness>> onGetStaleness) : IProfileStore
    {
        public int GetStalenessCallCount { get; private set; }

        public Task<ProfileStaleness> GetStalenessAsync(string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            GetStalenessCallCount++;
            return onGetStaleness(groupId, userId, cutoff);
        }

        public Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProfileRefreshTask>> GetStaleProfilesAsync(int max, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProfileRefreshTask>>([]);
    }

    [Fact]
    public async Task ProcessAsync_GetStalenessAsyncThrows_WithUserId_SuppressesGroupAndMemberAndLogsWarning()
    {
        var store = new ThrowingProfileStore((_, _, _) =>
            throw new HttpRequestException("Server error", null, System.Net.HttpStatusCode.InternalServerError));
        var logger = new CapturingLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMinutes(10) }),
            IngestOpts(),
            _time,
            logger);

        // 呼叫不會往外拋例外
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);

        // 記了含分類字串的 Warning
        Assert.Single(logger.Warnings);
        Assert.Contains("查詢名稱／頭貼快取狀態失敗", logger.Warnings[0]);
        Assert.Contains("HTTP 500", logger.Warnings[0]);

        // Group 與 Member 均進入冷卻，第二次呼叫直接短路，不重打 GetStalenessAsync
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Equal(1, store.GetStalenessCallCount);
    }

    [Fact]
    public async Task ProcessAsync_GetStalenessAsyncThrows_WithoutUserId_SuppressesGroupAndLogsWarning()
    {
        var store = new ThrowingProfileStore((_, _, _) =>
            throw new HttpRequestException("DNS failed", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        var logger = new CapturingLogger();

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions { RefreshAfter = TimeSpan.FromDays(7), FailureRetryAfter = TimeSpan.FromMinutes(10) }),
            IngestOpts(),
            _time,
            logger);

        // 呼叫不會往外拋例外
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);

        // 記了含分類字串的 Warning
        Assert.Single(logger.Warnings);
        Assert.Contains("查詢名稱／頭貼快取狀態失敗", logger.Warnings[0]);
        Assert.Contains("DNS 解析失敗", logger.Warnings[0]);

        // Group 進入冷卻，第二次呼叫直接短路
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Equal(1, store.GetStalenessCallCount);
    }

    private class ThrowingUpsertProfileStore(Action? onUpsertGroup = null, Action? onUpsertMember = null) : IProfileStore
    {
        public Task<ProfileStaleness> GetStalenessAsync(string groupId, string? userId, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(new ProfileStaleness(true, userId != null));

        public Task UpsertGroupAsync(string groupId, GroupSummary summary, CancellationToken cancellationToken)
        {
            onUpsertGroup?.Invoke();
            return Task.CompletedTask;
        }

        public Task UpsertMemberAsync(string groupId, string userId, MemberProfile profile, CancellationToken cancellationToken)
        {
            onUpsertMember?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProfileRefreshTask>> GetStaleProfilesAsync(int max, DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProfileRefreshTask>>([]);
    }

    [Fact]
    public async Task ProcessAsync_GroupUpsertThrows_RecordsCooldownAndSkipsSubsequentLineCalls()
    {
        // 10. Edge 推送失敗記冷卻：upsert 拋例外後，同一 (groupId,userId) 在冷卻期內再收到任務時不會再打 LINE
        var store = new ThrowingUpsertProfileStore(
            onUpsertGroup: () => throw new HttpRequestException("Core ingest API down"));

        _profileClient.OnGetGroupSummary = _ => new GroupSummary("G1", "GroupName", null);

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions
            {
                RefreshAfter = TimeSpan.FromDays(7),
                FailureRetryAfter = TimeSpan.FromMinutes(10)
            }),
            IngestOpts("http://core.example/"),
            _time,
            NullLogger<ProfileRefreshService>.Instance);

        // 第一次處理：打 LINE API，但 upsert 拋出例外，應記錄冷卻
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);

        // 第二次在冷卻期內再收到同一任務：group 處於冷卻期內被跳過，不打 LINE
        await service.ProcessAsync(new ProfileRefreshTask("G1", null), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);
    }

    [Fact]
    public async Task ProcessAsync_MemberUpsertThrows_RecordsCooldownAndSkipsSubsequentLineCalls()
    {
        // 10（成員版）. Edge 推送失敗記冷卻：member upsert 拋例外後，同一 (groupId,userId) 在冷卻期內再收到任務時不會再打 LINE
        var store = new ThrowingUpsertProfileStore(
            onUpsertGroup: () => { /* group upsert 成功 */ },
            onUpsertMember: () => throw new HttpRequestException("Core ingest API down"));

        _profileClient.OnGetGroupSummary = _ => new GroupSummary("G1", "GroupName", null);
        _profileClient.OnGetGroupMemberProfile = (_, userId) => new MemberProfile(userId, "UserName", null);

        var services = new ServiceCollection();
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton<ILineProfileClient>(_profileClient);
        var provider = services.BuildServiceProvider();

        var service = new ProfileRefreshService(
            new FakeProfileRefreshQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            OptionsFactory.Create(new ProfileCacheOptions
            {
                RefreshAfter = TimeSpan.FromDays(7),
                FailureRetryAfter = TimeSpan.FromMinutes(10)
            }),
            IngestOpts("http://core.example/"),
            _time,
            NullLogger<ProfileRefreshService>.Instance);

        // 第一次處理：member upsert 拋出例外，記錄冷卻
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);
        Assert.Single(_profileClient.MemberProfileCalls);

        // 第二次在冷卻期內（group 已成功進入 SuppressWindow，member 進入 FailureRetryAfter）：
        // 兩者皆被抑制，不打 LINE
        await service.ProcessAsync(new ProfileRefreshTask("G1", "U1"), CancellationToken.None);
        Assert.Single(_profileClient.GroupSummaryCalls);
        Assert.Single(_profileClient.MemberProfileCalls);
    }
}
