using MessageService.Web.Services;

namespace MessageService.Web.Tests.Services;

public class AvatarIconCatalogTests
{
    // 嚴肅場合下容易被讀成負面/貶義聯想的字，代號庫不該出現
    private static readonly string[] DisallowedSubstrings = ["豬", "狗", "鼠", "蛇", "雞", "龜", "驢", "狐", "猴"];

    [Fact]
    public void Icons_HasTwentyFourEntries()
    {
        Assert.Equal(24, AvatarIconCatalog.Icons.Count);
    }

    [Fact]
    public void Icons_AllIconKeysAreUnique()
    {
        var keys = AvatarIconCatalog.Icons.Select(i => i.IconKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Icons_AllLabelsAreUnique()
    {
        var labels = AvatarIconCatalog.Icons.Select(i => i.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Icons_NoneUseDisallowedNegativeAssociationWords()
    {
        foreach (var icon in AvatarIconCatalog.Icons)
        {
            foreach (var disallowed in DisallowedSubstrings)
            {
                Assert.DoesNotContain(disallowed, icon.Label);
            }
        }
    }

    [Fact]
    public void Icons_NoneCollideWithGroupFallbackKey()
    {
        Assert.DoesNotContain(AvatarIconCatalog.Icons, i => i.IconKey == AvatarIconCatalog.GroupFallbackIconKey);
    }

    [Fact]
    public void ForHash_SameSeed_AlwaysReturnsSameIcon()
    {
        var first = AvatarIconCatalog.ForHash("U1234567890");
        var second = AvatarIconCatalog.ForHash("U1234567890");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ForHash_DifferentSeeds_CanReturnDifferentIcons()
    {
        var seeds = Enumerable.Range(0, 50).Select(i => $"U{i}");
        var distinctIcons = seeds.Select(AvatarIconCatalog.ForHash).Select(i => i.IconKey).Distinct().Count();

        Assert.True(distinctIcons > 1);
    }
}
