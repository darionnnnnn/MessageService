using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MessageService.Web.Tests.Services;

public class MaskingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MessageDbContext _dbContext;
    private readonly IMemoryCache _cache;

    public MaskingServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        _dbContext = new MessageDbContext(options);
        _dbContext.Database.EnsureCreated();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task LoadRulesAsync_UsesSeededDefaultViewerSettings()
    {
        // Migration 的 HasData 種子（Original）在 EnsureCreated 下也會套用
        var service = new MaskingService(_dbContext, _cache);

        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("小明", rules.ResolveDisplayName("U1", "小明"));
    }

    [Fact]
    public async Task LoadRulesAsync_LoadsKeywordsAndAppliesThem()
    {
        _dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
        await _dbContext.SaveChangesAsync();

        var service = new MaskingService(_dbContext, _cache);
        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("我的**是1234", rules.MaskText("G1", "我的密碼是1234"));
    }

    [Fact]
    public async Task LoadRulesAsync_LoadsGroupScopedKeyword()
    {
        var rule = new MaskKeyword { Keyword = "secret", ApplyToAllGroups = false };
        rule.Groups.Add(new MaskKeywordGroup { GroupId = "G1" });
        _dbContext.MaskKeywords.Add(rule);
        await _dbContext.SaveChangesAsync();

        var service = new MaskingService(_dbContext, _cache);
        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("******", rules.MaskText("G1", "secret"));
        Assert.Equal("secret", rules.MaskText("G2", "secret"));
    }

    [Fact]
    public async Task LoadRulesAsync_LoadsUserAliases_WhenModeIsCustomAlias()
    {
        var settings = await _dbContext.ViewerSettings.FirstAsync();
        settings.NameDisplayMode = NameDisplayMode.CustomAlias;
        _dbContext.UserAliases.Add(new UserAlias { UserId = "U1", Alias = "值班A" });
        await _dbContext.SaveChangesAsync();

        var service = new MaskingService(_dbContext, _cache);
        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("值班A", rules.ResolveDisplayName("U1", "小明"));
    }

    [Fact]
    public async Task LoadRulesAsync_ReturnsCachedRules_WhenCalledRepeatedly()
    {
        var service = new MaskingService(_dbContext, _cache);

        // 第一次呼叫，載入種子預設值（Original）
        var firstRules = await service.LoadRulesAsync(CancellationToken.None);
        Assert.Equal("小明", firstRules.ResolveDisplayName("U1", "小明"));

        // 直接透過另一個 DbContext 改動資料庫
        var otherOptions = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        using (var otherDbContext = new MessageDbContext(otherOptions))
        {
            var settings = await otherDbContext.ViewerSettings.FirstAsync();
            settings.NameDisplayMode = NameDisplayMode.MaskMiddle;
            await otherDbContext.SaveChangesAsync();
        }

        // 第二次呼叫，應命中快取，回傳舊規則
        var secondRules = await service.LoadRulesAsync(CancellationToken.None);
        Assert.Equal("小明", secondRules.ResolveDisplayName("U1", "小明"));
    }

    [Fact]
    public async Task InvalidateCache_ClearsCache_AllowsNextLoadToReadUpdatedDatabase()
    {
        var service = new MaskingService(_dbContext, _cache);

        // 第一次呼叫，快取原始規則
        var firstRules = await service.LoadRulesAsync(CancellationToken.None);
        Assert.Equal("小明", firstRules.ResolveDisplayName("U1", "小明"));

        // 透過另一個 DbContext 改動資料庫
        var otherOptions = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        using (var otherDbContext = new MessageDbContext(otherOptions))
        {
            var settings = await otherDbContext.ViewerSettings.FirstAsync();
            settings.NameDisplayMode = NameDisplayMode.MaskMiddle;
            await otherDbContext.SaveChangesAsync();
        }

        // 呼叫 InvalidateCache 使快取失效
        service.InvalidateCache();

        // 第三次呼叫，應重新讀取資料庫，回傳新規則
        var thirdRules = await service.LoadRulesAsync(CancellationToken.None);
        Assert.Equal("小*", thirdRules.ResolveDisplayName("U1", "小明"));
    }
}
