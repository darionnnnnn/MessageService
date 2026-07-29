using MessageService.Models;
using MessageService.Web.Services;

namespace MessageService.Web.Tests.Services;

public class MaskingRuleSetTests
{
    private static MaskKeyword Keyword(string keyword, string? replacement = null, bool applyToAll = true, params string[] groupIds)
    {
        var rule = new MaskKeyword { Id = 1, Keyword = keyword, Replacement = replacement, ApplyToAllGroups = applyToAll };
        rule.Groups = groupIds.Select(g => new MaskKeywordGroup { MaskKeywordId = rule.Id, GroupId = g }).ToList();
        return rule;
    }

    private static MaskingRuleSet CreateRuleSet(
        NameDisplayMode mode = NameDisplayMode.MaskMiddle,
        IReadOnlyList<MaskKeyword>? keywords = null,
        IReadOnlyDictionary<string, string>? aliases = null) =>
        new(mode, keywords ?? [], aliases ?? new Dictionary<string, string>());

    [Fact]
    public void MaskText_NoMatchingKeyword_ReturnsUnchanged()
    {
        var rules = CreateRuleSet(keywords: [Keyword("密碼")]);

        Assert.Equal("今天天氣不錯", rules.MaskText("G1", "今天天氣不錯"));
    }

    [Fact]
    public void MaskText_DefaultReplacement_UsesEqualLengthAsterisksCaseInsensitive()
    {
        var rules = CreateRuleSet(keywords: [Keyword("password")]);

        Assert.Equal("my ******** is secret", rules.MaskText("G1", "my PASSWORD is secret"));
    }

    [Fact]
    public void MaskText_CustomReplacement_UsesLiteralReplacementText()
    {
        var rules = CreateRuleSet(keywords: [Keyword("密碼", replacement: "[遮蔽]")]);

        Assert.Equal("我的[遮蔽]是1234", rules.MaskText("G1", "我的密碼是1234"));
    }

    [Fact]
    public void MaskText_ApplyToAllGroups_MasksRegardlessOfGroup()
    {
        var rules = CreateRuleSet(keywords: [Keyword("secret", applyToAll: true)]);

        Assert.Equal("******", rules.MaskText("AnyGroup", "secret"));
    }

    [Fact]
    public void MaskText_ScopedToSpecificGroups_OnlyAppliesToThoseGroups()
    {
        var rules = CreateRuleSet(keywords: [Keyword("secret", applyToAll: false, groupIds: ["G1", "G2"])]);

        Assert.Equal("******", rules.MaskText("G1", "secret"));
        Assert.Equal("secret", rules.MaskText("G3", "secret"));
    }

    [Theory]
    [InlineData("Alice", "Alice")]
    [InlineData(null, "U123")]
    public void ResolveDisplayName_OriginalMode_ReturnsRawNameOrFallsBackToUserId(string? rawName, string expected)
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.Original);

        Assert.Equal(expected, rules.ResolveDisplayName("U123", rawName));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("A", "*")]
    [InlineData("小明", "小*")]
    [InlineData("王小明", "王*明")]
    [InlineData("王大小明", "王**明")]
    public void ResolveDisplayName_MaskMiddleMode_MasksByLengthBoundary(string rawName, string expected)
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.MaskMiddle);

        Assert.Equal(expected, rules.ResolveDisplayName("U123", rawName));
    }

    [Fact]
    public void ResolveDisplayName_MaskMiddleMode_NoRawName_MasksUserId()
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.MaskMiddle);

        Assert.Equal("U***4", rules.ResolveDisplayName("U1234", null));
    }

    [Fact]
    public void ResolveDisplayName_CustomAliasMode_WithAlias_ReturnsAlias()
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.CustomAlias, aliases: new Dictionary<string, string> { ["U123"] = "值班A" });

        Assert.Equal("值班A", rules.ResolveDisplayName("U123", "小明"));
    }

    [Fact]
    public void ResolveDisplayName_CustomAliasMode_WithoutAlias_FallsBackToMaskMiddle()
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.CustomAlias, aliases: new Dictionary<string, string>());

        Assert.Equal("小*", rules.ResolveDisplayName("U123", "小明"));
    }
}
