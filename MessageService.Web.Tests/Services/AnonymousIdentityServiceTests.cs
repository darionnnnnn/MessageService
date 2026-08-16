using MessageService.Data;
using MessageService.Models;
using MessageService.Tests.TestSupport;
using MessageService.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Services;

public class AnonymousIdentityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MessageDbContext _dbContext;
    private readonly AnonymousIdentityService _service;

    public AnonymousIdentityServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        _dbContext = new MessageDbContext(options);
        _dbContext.Database.EnsureCreated();
        _service = new AnonymousIdentityService(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private MessageDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        return new MessageDbContext(options);
    }

    private (AnonymousIdentityService service, MessageDbContext dbContext, SaveFailureInterceptor interceptor) CreateServiceWithInterceptor()
    {
        var interceptor = new SaveFailureInterceptor();
        var options = new DbContextOptionsBuilder<MessageDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        var dbContext = new MessageDbContext(options);
        var service = new AnonymousIdentityService(dbContext);
        return (service, dbContext, interceptor);
    }

    [Fact]
    public async Task GetOrAssignAsync_EmptyUserIds_ReturnsEmptyAndWritesNothing()
    {
        var result = await _service.GetOrAssignAsync("G1", [], CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_dbContext.AnonymousIdentities);
    }

    [Fact]
    public async Task GetOrAssignAsync_FirstEncounter_AssignsAndPersists()
    {
        var result = await _service.GetOrAssignAsync("G1", ["U1"], CancellationToken.None);

        var identity = Assert.Single(result);
        Assert.Equal("U1", identity.Key);
        Assert.False(string.IsNullOrEmpty(identity.Value.IconKey));
        Assert.False(string.IsNullOrEmpty(identity.Value.Label));

        var persisted = await _dbContext.AnonymousIdentities.SingleAsync();
        Assert.Equal("G1", persisted.GroupId);
        Assert.Equal("U1", persisted.UserId);
        Assert.Equal(identity.Value.IconKey, persisted.IconKey);
        Assert.Equal(identity.Value.Label, persisted.Label);
    }

    [Fact]
    public async Task GetOrAssignAsync_CalledTwiceForSameUser_ReturnsIdenticalIdentity()
    {
        var first = await _service.GetOrAssignAsync("G1", ["U1"], CancellationToken.None);

        // 換一個新的 DbContext 模擬下一次獨立請求，確保穩定性不是靠同一個 DbContext 的追蹤快取
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        await using var secondDbContext = new MessageDbContext(options);
        var secondService = new AnonymousIdentityService(secondDbContext);
        var second = await secondService.GetOrAssignAsync("G1", ["U1"], CancellationToken.None);

        Assert.Equal(first["U1"], second["U1"]);
        Assert.Single(_dbContext.AnonymousIdentities.AsEnumerable());
    }

    [Fact]
    public async Task GetOrAssignAsync_SameUserDifferentGroups_CanGetDifferentIdentities()
    {
        var g1 = await _service.GetOrAssignAsync("G1", ["U1"], CancellationToken.None);
        var g2 = await _service.GetOrAssignAsync("G2", ["U1"], CancellationToken.None);

        // 不強制要求不同（雜湊可能剛好選到同一個圖示），但兩筆各自獨立持久化
        Assert.Equal(2, await _dbContext.AnonymousIdentities.CountAsync());
    }

    [Fact]
    public async Task GetOrAssignAsync_MixOfKnownAndNewUsers_OnlyAssignsTheNewOnes()
    {
        var first = await _service.GetOrAssignAsync("G1", ["U1"], CancellationToken.None);

        var second = await _service.GetOrAssignAsync("G1", ["U1", "U2"], CancellationToken.None);

        Assert.Equal(first["U1"], second["U1"]);
        Assert.True(second.ContainsKey("U2"));
        Assert.Equal(2, await _dbContext.AnonymousIdentities.CountAsync());
    }

    [Fact]
    public async Task GetOrAssignAsync_ManyMembersInOneGroup_CollidingIconGetsSuffixedLabel()
    {
        // 24 款圖示、25 個人必定至少撞名一次；驗證撞到同一個圖示時 Label 會加序號區分
        var userIds = Enumerable.Range(0, 25).Select(i => $"U{i}").ToList();

        var result = await _service.GetOrAssignAsync("G1", userIds, CancellationToken.None);

        Assert.Equal(25, result.Count);
        var byIcon = result.Values.GroupBy(v => v.IconKey).ToList();
        Assert.Contains(byIcon, g => g.Count() > 1);

        foreach (var group in byIcon.Where(g => g.Count() > 1))
        {
            var labels = group.Select(v => v.Label).OrderBy(l => l).ToList();
            // 同圖示底下的 label 必須互不相同（序號區分），不能出現重複的「小熊」「小熊」
            Assert.Equal(labels.Count, labels.Distinct().Count());
        }
    }

    [Fact]
    public async Task GetOrAssignAsync_AllIconKeysComeFromCatalog()
    {
        var userIds = Enumerable.Range(0, 10).Select(i => $"U{i}").ToList();

        var result = await _service.GetOrAssignAsync("G1", userIds, CancellationToken.None);

        var validKeys = AvatarIconCatalog.Icons.Select(i => i.IconKey).ToHashSet();
        Assert.All(result.Values, v => Assert.Contains(v.IconKey, validKeys));
    }

    [Fact]
    public async Task GetOrAssignAsync_LabelCollided_SuffixIncrementsAndAssignsDistinctLabels()
    {
        // 找出在 G1 群組下會指派到相同圖示的兩位不同使用者
        var firstUserId = "U1";
        var expectedIcon = AvatarIconCatalog.ForHash($"G1:{firstUserId}");
        var secondUserId = Enumerable.Range(2, 100)
            .Select(i => $"U{i}")
            .First(id => AvatarIconCatalog.ForHash($"G1:{id}").IconKey == expectedIcon.IconKey);

        // 模擬在第一位使用者存檔前，該圖示的第一個代號已被另一位成員佔用（觸發代號唯一索引衝突）
        var (service, context, interceptor) = CreateServiceWithInterceptor();
        await using var _ = context;

        interceptor.BeforeSaveOnce = async () =>
        {
            await using var otherContext = CreateDbContext();
            otherContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = "G1",
                UserId = "U_PreExisting",
                IconKey = expectedIcon.IconKey,
                Label = expectedIcon.Label,
                AssignedAt = DateTimeOffset.UtcNow
            });
            await otherContext.SaveChangesAsync();
        };

        var result = await service.GetOrAssignAsync("G1", [firstUserId, secondUserId], CancellationToken.None);

        Assert.Equal(2, result.Count);
        var first = result[firstUserId];
        var second = result[secondUserId];

        // 兩位使用者最後拿到的 Label 互不相同，且都是以該圖示的名稱開頭（例如「小熊 2」與「小熊 3」）
        Assert.Equal(expectedIcon.IconKey, first.IconKey);
        Assert.Equal(expectedIcon.IconKey, second.IconKey);
        Assert.StartsWith(expectedIcon.Label, first.Label);
        Assert.StartsWith(expectedIcon.Label, second.Label);
        Assert.NotEqual(first.Label, second.Label);

        // 資料庫中包含佔用者共 3 筆，Label 全部互不重複
        var allIdentities = await _dbContext.AnonymousIdentities.Where(a => a.GroupId == "G1").ToListAsync();
        Assert.Equal(3, allIdentities.Count);
        Assert.Equal(3, allIdentities.Select(a => a.Label).Distinct().Count());
    }

    [Fact]
    public async Task GetOrAssignAsync_UserPreemptedByConcurrentRequest_ReturnsPersistedIdentityFromDatabase()
    {
        // 模擬同一個使用者在本地存檔前，已被別的併發請求搶先指派（包含非預設的 IconKey 與 Label）
        var (service, context, interceptor) = CreateServiceWithInterceptor();
        await using var _ = context;

        const string userId = "U1";
        const string preemptedIconKey = "penguin";
        const string preemptedLabel = "企鵝 99";

        interceptor.BeforeSaveOnce = async () =>
        {
            await using var otherContext = CreateDbContext();
            otherContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = "G1",
                UserId = userId,
                IconKey = preemptedIconKey,
                Label = preemptedLabel,
                AssignedAt = DateTimeOffset.UtcNow
            });
            await otherContext.SaveChangesAsync();
        };

        var result = await service.GetOrAssignAsync("G1", [userId], CancellationToken.None);

        var identity = Assert.Single(result);
        Assert.Equal(userId, identity.Key);
        // 回傳的是資料庫裡既有那一筆的值，而非本地新算的值
        Assert.Equal(preemptedIconKey, identity.Value.IconKey);
        Assert.Equal(preemptedLabel, identity.Value.Label);
    }

    [Fact]
    public async Task GetOrAssignAsync_TransientSaveFailure_RethrowsOriginalDbUpdateException()
    {
        // 存檔遇到與衝突無關的暫時性故障（如連線中斷、逾時）時，原例外會往外傳
        var (service, context, interceptor) = CreateServiceWithInterceptor();
        await using var _ = context;
        interceptor.ThrowOnce = true;

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => service.GetOrAssignAsync("G1", ["U1"], CancellationToken.None));

        Assert.Contains("simulated transient save failure", ex.Message);
        Assert.Empty(await _dbContext.AnonymousIdentities.ToListAsync());
    }

    [Fact]
    public async Task GetOrAssignAsync_LabelCollisionExceedsMaxRetries_ThrowsInvalidOperationException()
    {
        // 預先以不同的 IconKey 塞入 51 個連續撞名的 Label，使初始計數為 0 但每次存檔皆撞名
        const string groupId = "G_RetryLimit";
        const string userId = "U1";
        var icon = AvatarIconCatalog.ForHash($"{groupId}:{userId}");

        for (var i = 0; i <= 50; i++)
        {
            var label = i == 0 ? icon.Label : $"{icon.Label} {i + 1}";
            _dbContext.AnonymousIdentities.Add(new AnonymousIdentity
            {
                GroupId = groupId,
                UserId = $"Occupier_{i}",
                IconKey = "other_key",
                Label = label,
                AssignedAt = DateTimeOffset.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GetOrAssignAsync(groupId, [userId], CancellationToken.None));

        Assert.Contains(groupId, ex.Message);
        Assert.Contains("50", ex.Message);
    }
}
