using MessageService.Data;
using MessageService.Models;
using MessageService.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Services;

public class MaskingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MessageDbContext _dbContext;

    public MaskingServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        _dbContext = new MessageDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task LoadRulesAsync_UsesSeededDefaultViewerSettings()
    {
        // Migration 的 HasData 種子（MaskMiddle）在 EnsureCreated 下也會套用
        var service = new MaskingService(_dbContext);

        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("小*", rules.ResolveDisplayName("U1", "小明"));
    }

    [Fact]
    public async Task LoadRulesAsync_LoadsKeywordsAndAppliesThem()
    {
        _dbContext.MaskKeywords.Add(new MaskKeyword { Keyword = "密碼", ApplyToAllGroups = true });
        await _dbContext.SaveChangesAsync();

        var service = new MaskingService(_dbContext);
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

        var service = new MaskingService(_dbContext);
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

        var service = new MaskingService(_dbContext);
        var rules = await service.LoadRulesAsync(CancellationToken.None);

        Assert.Equal("值班A", rules.ResolveDisplayName("U1", "小明"));
    }
}
