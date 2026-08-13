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
        IReadOnlyDictionary<string, string>? aliases = null,
        PiiMaskingSettings? pii = null) =>
        new(mode, keywords ?? [], aliases ?? new Dictionary<string, string>(), pii);

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

    [Fact]
    public void ResolveDisplayName_AnonymousMode_ReturnsSuppliedLabel_IgnoringRawName()
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.Anonymous);

        Assert.Equal("小熊", rules.ResolveDisplayName("U123", "小明", "小熊"));
    }

    [Fact]
    public void ResolveDisplayName_AnonymousMode_NoLabelSupplied_FallsBackToUnknownPlaceholder()
    {
        var rules = CreateRuleSet(mode: NameDisplayMode.Anonymous);

        Assert.Equal("(未知)", rules.ResolveDisplayName("U123", "小明"));
    }

    [Theory]
    [InlineData(NameDisplayMode.Original, true)]
    [InlineData(NameDisplayMode.MaskMiddle, false)]
    [InlineData(NameDisplayMode.CustomAlias, false)]
    [InlineData(NameDisplayMode.Anonymous, false)]
    public void RevealsOriginalProfile_OnlyTrueForOriginalMode(NameDisplayMode mode, bool expected)
    {
        var rules = CreateRuleSet(mode: mode);

        Assert.Equal(expected, rules.RevealsOriginalProfile);
    }

    [Theory]
    [InlineData(NameDisplayMode.Anonymous, true)]
    [InlineData(NameDisplayMode.Original, false)]
    [InlineData(NameDisplayMode.MaskMiddle, false)]
    [InlineData(NameDisplayMode.CustomAlias, false)]
    public void RequiresAnonymousIdentity_OnlyTrueForAnonymousMode(NameDisplayMode mode, bool expected)
    {
        var rules = CreateRuleSet(mode: mode);

        Assert.Equal(expected, rules.RequiresAnonymousIdentity);
    }

    // === 台灣個資格式偵測：身分證／手機／市話／健保卡，見 MaskingRuleSet 建構子的 PiiMaskingSettings ===
    // 預期字串一律用 MaskMiddleExpected 算出來（保留首尾、中間補星號），不手算星號數量避免算錯

    private static string MaskMiddleExpected(string value) =>
        value.Length <= 2 ? value : value[0] + new string('*', value.Length - 2) + value[^1];

    [Theory]
    [InlineData("我的身分證是A123456789啦", "A123456789")]
    [InlineData("女性身分證A223456789", "A223456789")]
    // 小寫：真實聊天訊息不會有人特地按 Shift 打大寫，只認 [A-Z] 等於在最常見的輸入上完全失效
    [InlineData("我的身分證是a123456789啦", "a123456789")]
    // 2021 年起的新式外來人口統一證號：第二碼是 8（男）／9（女）
    [InlineData("統一證號A812345678", "A812345678")]
    [InlineData("統一證號A912345678", "A912345678")]
    // 舊式居留證統一證號：第二碼是 A~D 的英文字母
    [InlineData("居留證AB12345678", "AB12345678")]
    public void MaskText_NationalId_MasksMiddleKeepingFirstAndLastChar(string input, string matched)
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);
        var expected = input.Replace(matched, MaskMiddleExpected(matched));

        Assert.Equal(expected, rules.MaskText("G1", input));
    }

    /// <summary>「兩個字母 + 8 碼數字」的單號很常見，第二碼限定在真的會出現的性別／證件
    /// 類別碼（1/2/8/9/A-D）才不會把訂單編號整串誤遮。</summary>
    [Theory]
    [InlineData("訂單編號PO12345678")]
    [InlineData("料號XY87654321")]
    public void MaskText_NationalId_DoesNotMaskLookalikeReferenceNumbers(string input)
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);

        Assert.Equal(input, rules.MaskText("G1", input));
    }

    /// <summary>前後邊界要擋掉英文字母而不只是數字，否則英數混合的單號會被咬掉一段，
    /// 變成「ORD0********8」這種既沒保護到什麼、又破壞正常內容的結果。</summary>
    [Theory]
    [InlineData("ORD0912345678")]
    [InlineData("X123456789012")]
    public void MaskText_AlphanumericTokens_AreNotPartiallyMasked(string input)
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);

        Assert.Equal(input, rules.MaskText("G1", input));
    }

    [Fact]
    public void MaskText_NationalId_Disabled_LeavesTextUnchanged()
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled with { MaskNationalId = false });

        Assert.Equal("我的身分證是A123456789啦", rules.MaskText("G1", "我的身分證是A123456789啦"));
    }

    [Theory]
    [InlineData("打給我0912345678", "0912345678")]
    [InlineData("電話0912-345-678喔", "0912-345-678")]
    [InlineData("0912 345 678", "0912 345 678")]
    public void MaskText_MobilePhone_MasksCommonFormats(string input, string matched)
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);
        var expected = input.Replace(matched, MaskMiddleExpected(matched));

        Assert.Equal(expected, rules.MaskText("G1", input));
    }

    [Fact]
    public void MaskText_MobilePhone_Disabled_LeavesTextUnchanged()
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled with { MaskMobilePhone = false });

        Assert.Equal("打給我0912345678", rules.MaskText("G1", "打給我0912345678"));
    }

    [Theory]
    [InlineData("公司電話02-12345678", "02-12345678")]
    [InlineData("市話049-1234567", "049-1234567")]
    // 台北市話最普遍的寫法，只認單一連字號會整個漏掉
    [InlineData("公司02-2345-6789分機12", "02-2345-6789")]
    public void MaskText_Landline_RequiresHyphen_MasksMatchingFormats(string input, string matched)
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);
        var expected = input.Replace(matched, MaskMiddleExpected(matched));

        Assert.Equal(expected, rules.MaskText("G1", input));
    }

    [Fact]
    public void MaskText_Landline_WithoutHyphen_IsNotTreatedAsLandline()
    {
        // 沒有連字號的純數字序列不套市話規則（避免誤判一般數字）；10 碼也不到健保卡規則的 12 碼，
        // 這裡刻意驗證兩者都不誤觸
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);

        Assert.Equal("0212345678", rules.MaskText("G1", "0212345678"));
    }

    [Fact]
    public void MaskText_Landline_Disabled_LeavesTextUnchanged()
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled with { MaskLandline = false });

        Assert.Equal("公司電話02-12345678", rules.MaskText("G1", "公司電話02-12345678"));
    }

    [Fact]
    public void MaskText_NhiCard_TwelveDigits_MasksMiddleKeepingFirstAndLastChar()
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);

        Assert.Equal("健保卡號" + MaskMiddleExpected("123456789012"), rules.MaskText("G1", "健保卡號123456789012"));
    }

    [Fact]
    public void MaskText_NhiCard_Disabled_LeavesTextUnchanged()
    {
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled with { MaskNhiCard = false });

        Assert.Equal("健保卡號123456789012", rules.MaskText("G1", "健保卡號123456789012"));
    }

    [Fact]
    public void MaskText_AllPiiDisabled_LeavesAllFormatsUnchanged()
    {
        var rules = CreateRuleSet(pii: new PiiMaskingSettings(false, false, false, false));

        const string text = "身分證A123456789 手機0912345678 市話02-12345678 健保卡123456789012";
        Assert.Equal(text, rules.MaskText("G1", text));
    }

    [Fact]
    public void MaskText_DefaultPiiSettings_WhenNotSpecified_IsAllEnabled()
    {
        // MaskingRuleSet 建構子沒帶 PiiMaskingSettings 時預設全開，跟 ViewerSettings 欄位的
        // 資料庫預設值（見 MessageDbContext 的 model／migration）一致
        var rules = CreateRuleSet(pii: null);

        Assert.Equal(MaskMiddleExpected("A123456789"), rules.MaskText("G1", "A123456789"));
    }

    [Fact]
    public void MaskText_ShortNumberSequence_DoesNotFalselyMatchAnyPiiFormat()
    {
        // 一般短數字（例如「3點見」「第2次」）不該被任何格式誤判
        var rules = CreateRuleSet(pii: PiiMaskingSettings.AllEnabled);

        Assert.Equal("我們3點見，這是第2次了", rules.MaskText("G1", "我們3點見，這是第2次了"));
    }

    [Fact]
    public void MaskText_KeywordAndPiiMasking_BothApply()
    {
        var rules = CreateRuleSet(keywords: [Keyword("密碼")], pii: PiiMaskingSettings.AllEnabled);

        var expected = "我的**是" + MaskMiddleExpected("A123456789") + "，別跟別人說";
        Assert.Equal(expected, rules.MaskText("G1", "我的密碼是A123456789，別跟別人說"));
    }
}
