using System.Net;
using System.Net.Http.Json;
using MessageService.Services;
using MessageService.Tests.TestSupport;

namespace MessageService.Tests.Services;

// 同 ApiContentWorkSourceTests 的定位：釘住這個類別自己發出的請求形狀——
// 特別是 staleness 的 query string 組裝（cutoff 的 "O" 格式含 '+' 與 ':'，沒有正確
// escape 的話 '+' 會被伺服器解成空白、時間值直接壞掉）。
public class ApiProfileStoreTests
{
    private static (ApiProfileStore store, FakeHttpMessageHandler handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        return (new ApiProfileStore(new FakeHttpClientFactory(handler)), handler);
    }

    [Fact]
    public async Task GetStalenessAsync_BuildsQueryWithEscapedCutoff_AndParsesResponse()
    {
        var (store, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProfileStaleness(true, false))
        });
        var cutoff = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

        var staleness = await store.GetStalenessAsync("G1", "U1", cutoff, CancellationToken.None);

        Assert.True(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/api/ingest/profiles/staleness", uri.AbsolutePath);
        // '+00:00' 的 '+' 必須是 %2B——沒 escape 的話 ASP.NET Core 綁定時會把它還原成空白
        Assert.Contains("cutoff=2026-08-12T10%3A00%3A00.0000000%2B00%3A00", uri.Query);
        Assert.Contains("groupId=G1", uri.Query);
        Assert.Contains("userId=U1", uri.Query);
    }

    [Fact]
    public async Task GetStalenessAsync_NullUserId_OmitsUserIdFromQuery()
    {
        var (store, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProfileStaleness(false, false))
        });

        await store.GetStalenessAsync("G1", userId: null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.DoesNotContain("userId", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task UpsertGroupAsync_PostsSummaryToGroupPath()
    {
        GroupSummary? sent = null;
        var (store, handler) = Create(request =>
        {
            sent = request.Content!.ReadFromJsonAsync<GroupSummary>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var summary = new GroupSummary("G1", "群組名", "https://example/g.png");

        await store.UpsertGroupAsync("G1", summary, CancellationToken.None);

        Assert.Equal("https://db-host.example/api/ingest/profiles/group", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(summary, sent);
    }

    [Fact]
    public async Task UpsertMemberAsync_WrapsGroupIdAndProfileInRequestBody()
    {
        MemberUpsertRequest? sent = null;
        var (store, handler) = Create(request =>
        {
            sent = request.Content!.ReadFromJsonAsync<MemberUpsertRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var profile = new MemberProfile("U1", "顯示名", null);

        await store.UpsertMemberAsync("G1", "U1", profile, CancellationToken.None);

        Assert.Equal("https://db-host.example/api/ingest/profiles/member", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(new MemberUpsertRequest("G1", profile), sent);
    }

    [Fact]
    public async Task UpsertFailure_Throws_SoProfileRefreshLogsAndMovesOn()
    {
        // 頭貼快取是非關鍵資料：例外交給 ProfileRefreshService 既有的「記 log、不重試」，
        // 下一則該群組的訊息會重新入列（見 IProfileStore 介面說明）
        var (store, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => store.UpsertGroupAsync("G1", new GroupSummary("G1", null, null), CancellationToken.None));
    }
}
