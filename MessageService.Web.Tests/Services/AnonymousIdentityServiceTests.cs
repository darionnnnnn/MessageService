using MessageService.Data;
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
}
