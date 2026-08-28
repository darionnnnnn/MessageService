using MessageService.Services;

namespace MessageService.Tests.Services;

/// <summary>拉取模式下的名稱／頭貼四類（群組名稱、成員名稱、群組圖片、成員頭貼）——
/// 逐類驗證與推送模式（ApiProfileStore→DbProfileStore）落地的欄位一致，一類都不能少。</summary>
public class StagingProfileStoreTests
{
    private static (StagingProfileStore Store, EdgeProfileStaging Staging) Create()
    {
        var staging = new EdgeProfileStaging();
        return (new StagingProfileStore(staging), staging);
    }

    [Fact]
    public async Task GetStalenessAsync_NotDispatched_ReportsNotStale()
    {
        var (store, _) = Create();

        var staleness = await store.GetStalenessAsync("G1", "U1", DateTimeOffset.UtcNow, CancellationToken.None);

        // 沒被 Core 派工的對象不該讓 Edge 多打 LINE API
        Assert.False(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task GetStalenessAsync_Dispatched_ReturnsWhatCoreComputed()
    {
        var (store, staging) = Create();
        var dispatched = new ProfileStaleness(
            GroupStale: true, MemberStale: true,
            GroupPictureFetchedUrl: "https://g/pic", MemberPictureFetchedUrl: "https://m/pic",
            HasGroupPicture: true, HasMemberPicture: false);
        staging.Dispatch([new EdgeProfileWorkItem("G1", "U1", dispatched)]);

        var staleness = await store.GetStalenessAsync("G1", "U1", DateTimeOffset.UtcNow, CancellationToken.None);

        // 六個欄位都要原樣帶過去——少帶任何一個都會讓 Edge 重抓已經抓過的圖
        Assert.Equal(dispatched, staleness);
    }

    [Fact]
    public async Task UpsertGroupAsync_GroupNameAndPicture_AreReportedBack()
    {
        var (store, staging) = Create();
        var summary = new GroupSummary("G1", "研發群組", "https://g/pic", [1, 2, 3], "image/png");

        await store.UpsertGroupAsync("G1", summary, CancellationToken.None);

        var result = Assert.Single(staging.DrainResults());
        Assert.Equal("G1", result.GroupId);
        Assert.Null(result.UserId);
        Assert.Null(result.Member);
        // 群組名稱與群組圖片兩類逐欄位等價
        Assert.Equal("研發群組", result.Group!.GroupName);
        Assert.Equal("https://g/pic", result.Group.PictureUrl);
        Assert.Equal([1, 2, 3], result.Group.PictureBytes);
        Assert.Equal("image/png", result.Group.PictureContentType);
    }

    [Fact]
    public async Task UpsertMemberAsync_DisplayNameAndAvatar_AreReportedBack()
    {
        var (store, staging) = Create();
        var profile = new MemberProfile("U1", "小明", "https://m/pic", [9, 8], "image/jpeg");

        await store.UpsertMemberAsync("G1", "U1", profile, CancellationToken.None);

        var result = Assert.Single(staging.DrainResults());
        Assert.Equal("G1", result.GroupId);
        Assert.Equal("U1", result.UserId);
        Assert.Null(result.Group);
        // 成員名稱與成員頭貼兩類逐欄位等價
        Assert.Equal("小明", result.Member!.DisplayName);
        Assert.Equal("https://m/pic", result.Member.PictureUrl);
        Assert.Equal([9, 8], result.Member.PictureBytes);
        Assert.Equal("image/jpeg", result.Member.PictureContentType);
    }

    [Fact]
    public async Task Upsert_ClearsDispatchedStaleness()
    {
        var (store, staging) = Create();
        staging.Dispatch([new EdgeProfileWorkItem("G1", null, new ProfileStaleness(true, false))]);

        await store.UpsertGroupAsync("G1", new GroupSummary("G1", "名稱", null), CancellationToken.None);

        // 已經刷新過的不該再被視為過期而重抓
        var staleness = await store.GetStalenessAsync("G1", null, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(staleness.GroupStale);
    }

    [Fact]
    public void DrainResults_TakenOnce_IsNotRepeated()
    {
        var (_, staging) = Create();
        staging.EnqueueGroup("G1", new GroupSummary("G1", "名稱", null));

        Assert.Single(staging.DrainResults());
        Assert.Empty(staging.DrainResults());
    }

    [Fact]
    public void DrainResults_RespectsPerRoundByteBudget()
    {
        var (_, staging) = Create();
        var big = new byte[EdgeProfileStaging.ResultBudgetBytes];
        staging.EnqueueGroup("G1", new GroupSummary("G1", "一", null, big, "image/png"));
        staging.EnqueueGroup("G2", new GroupSummary("G2", "二", null, big, "image/png"));

        // poll 走短逾時的小 JSON 通道，一輪不能塞好幾 MB
        Assert.Single(staging.DrainResults());
        Assert.Single(staging.DrainResults());
    }

    [Fact]
    public void DrainResults_SingleOversizedItem_IsStillReturned()
    {
        var (_, staging) = Create();
        var oversized = new byte[EdgeProfileStaging.ResultBudgetBytes * 2];
        staging.EnqueueGroup("G1", new GroupSummary("G1", "大圖", null, oversized, "image/png"));

        // 否則單張大頭貼會永遠卡在佇列最前面，後面的誰也送不出去
        Assert.Single(staging.DrainResults());
    }
}
