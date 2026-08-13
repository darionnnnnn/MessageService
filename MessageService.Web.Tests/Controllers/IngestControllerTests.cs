using MessageService.Controllers;
using MessageService.Options;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Controllers;

// 端到端行為（含真實 DirectIngestSink＋資料庫、認證中介層）已在 DeploymentModeTests 用真實
// host 驗證；這裡單獨測 controller 對 IIngestSink 例外的映射邏輯、IngestSideEffects 有沒有被
// 正確呼叫、以及 Stage 3 新增端點（content-work／profiles）對 IContentWorkSource／IProfileStore
// 的轉發是否正確——用 Fake 依賴隔開，不需要真的資料庫或 HTTP。
public class IngestControllerTests
{
    private static IngestEnvelope SampleEnvelope() => new(
        WebhookEventId: "evt-1",
        LineMessageId: "m1",
        GroupId: "G1",
        UserId: "U1",
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.UtcNow,
        ReceivedAt: DateTimeOffset.UtcNow,
        HasContent: false,
        ContentFileName: null);

    private static IngestController CreateController(
        IIngestSink? sink = null,
        FakeContentWorkSource? contentWorkSource = null,
        FakeProfileStore? profileStore = null,
        FakeContentDownloadQueue? downloadQueue = null,
        FakeProfileRefreshQueue? profileRefreshQueue = null,
        FakeHeartbeatStore? heartbeatStore = null) =>
        new(
            sink ?? new FakeIngestSink(),
            contentWorkSource ?? new FakeContentWorkSource(),
            profileStore ?? new FakeProfileStore(),
            downloadQueue ?? new FakeContentDownloadQueue(),
            profileRefreshQueue ?? new FakeProfileRefreshQueue(),
            heartbeatStore ?? new FakeHeartbeatStore(),
            OptionsFactory.Create(new IngestOptions()),
            NullLogger<IngestController>.Instance);

    // === POST events ===

    [Fact]
    public async Task SubmitEvent_SinkSucceeds_ReturnsOk()
    {
        var sink = new FakeIngestSink();
        var controller = CreateController(sink: sink);

        var result = await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(sink.Submitted);
    }

    [Fact]
    public async Task SubmitEvent_SinkThrows_Returns500()
    {
        var sink = new FakeIngestSink { ThrowOnNextSubmit = new InvalidOperationException("db unreachable") };
        var controller = CreateController(sink: sink);

        var result = await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
    }

    [Fact]
    public async Task SubmitEvent_SinkThrowsOperationCanceled_PropagatesRatherThanReturning500()
    {
        var sink = new FakeIngestSink { ThrowOnNextSubmit = new OperationCanceledException() };
        var controller = CreateController(sink: sink);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.SubmitEvent(SampleEnvelope(), CancellationToken.None));
    }

    [Fact]
    public async Task SubmitEvent_ResultHasContentId_ResponseBodyIncludesIt()
    {
        var sink = new FakeIngestSink { NextContentId = 7 };
        var controller = CreateController(sink: sink);

        var result = await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<IngestEventResponse>(ok.Value);
        Assert.Equal(7, body.ContentId);
    }

    [Fact]
    public async Task SubmitEvent_ResultHasContentId_EnqueuesOnThisHostsOwnQueue()
    {
        // 這台主機（Db 端）自己要不要接手，取決於它自己的 IContentDownloadQueue 是不是 Null——
        // 這裡用真的（Fake）佇列驗證 IngestSideEffects 真的被呼叫到
        var sink = new FakeIngestSink { NextContentId = 7 };
        var downloadQueue = new FakeContentDownloadQueue();
        var controller = CreateController(sink: sink, downloadQueue: downloadQueue);

        await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        Assert.Equal(7, Assert.Single(downloadQueue.Enqueued));
    }

    // === POST events-batch（問題9） ===

    private static IngestEnvelope Envelope(string webhookEventId) => SampleEnvelope() with { WebhookEventId = webhookEventId };

    [Fact]
    public async Task SubmitEventsBatch_EmptyList_ReturnsOkWithEmptyResults()
    {
        var controller = CreateController();

        var result = await controller.SubmitEventsBatch([], CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsType<List<IngestBatchItemResult>>(ok.Value));
    }

    [Fact]
    public async Task SubmitEventsBatch_AllSucceed_ReturnsResultsForEach()
    {
        var sink = new FakeIngestSink();
        var controller = CreateController(sink: sink);
        var envelopes = new List<IngestEnvelope> { Envelope("evt-1"), Envelope("evt-2") };

        var result = await controller.SubmitEventsBatch(envelopes, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var results = Assert.IsType<List<IngestBatchItemResult>>(ok.Value);
        Assert.Equal(["evt-1", "evt-2"], results.Select(r => r.WebhookEventId));
        Assert.All(results, r => Assert.False(r.PermanentlyRejected));
        Assert.Equal(["evt-1", "evt-2"], sink.Submitted.Select(e => e.WebhookEventId));
    }

    [Fact]
    public async Task SubmitEventsBatch_SinkThrowsTransiently_Returns500()
    {
        var sink = new FakeIngestSink { ThrowOnNextSubmit = new InvalidOperationException("db unreachable") };
        var controller = CreateController(sink: sink);

        var result = await controller.SubmitEventsBatch([Envelope("evt-1")], CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
    }

    [Fact]
    public async Task SubmitEventsBatch_OneEntryPermanentlyRejected_OthersStillSucceed()
    {
        var sink = new FakeIngestSink();
        sink.ThrowForWebhookEventId["evt-bad"] = new PermanentIngestException("malformed");
        var controller = CreateController(sink: sink);
        var envelopes = new List<IngestEnvelope> { Envelope("evt-good-1"), Envelope("evt-bad"), Envelope("evt-good-2") };

        var result = await controller.SubmitEventsBatch(envelopes, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var results = Assert.IsType<List<IngestBatchItemResult>>(ok.Value);
        Assert.Equal(["evt-good-1", "evt-good-2"], sink.Submitted.Select(e => e.WebhookEventId));
        var rejected = Assert.Single(results, r => r.PermanentlyRejected);
        Assert.Equal("evt-bad", rejected.WebhookEventId);
        Assert.Contains("malformed", rejected.Error);
    }

    [Fact]
    public async Task SubmitEventsBatch_SuccessfulItems_EnqueueOnThisHostsOwnQueue()
    {
        var sink = new FakeIngestSink { NextContentId = 7 };
        var downloadQueue = new FakeContentDownloadQueue();
        var controller = CreateController(sink: sink, downloadQueue: downloadQueue);

        await controller.SubmitEventsBatch([Envelope("evt-1")], CancellationToken.None);

        Assert.Equal(7, Assert.Single(downloadQueue.Enqueued));
    }

    // P0：LINE redelivery 用同一個 WebhookEventId 重送整包，同一批裡出現重複鍵是預期會發生的
    // （見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次A）。改用 GroupBy 之前這裡會直接讓
    // ToDictionary 丟 ArgumentException，端點回 500，讓 Edge 端 outbox 永久卡死
    [Fact]
    public async Task SubmitEventsBatch_DuplicateWebhookEventId_DoesNotThrowAndReturnsOk()
    {
        var sink = new FakeIngestSink();
        var controller = CreateController(sink: sink);
        var envelopes = new List<IngestEnvelope> { Envelope("evt-1"), Envelope("evt-1"), Envelope("evt-2") };

        var result = await controller.SubmitEventsBatch(envelopes, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var results = Assert.IsType<List<IngestBatchItemResult>>(ok.Value);
        Assert.Equal(["evt-1", "evt-1", "evt-2"], results.Select(r => r.WebhookEventId));
    }

    // === content-work ===

    [Fact]
    public async Task GetContentWork_ReturnsPendingIdsFromSource()
    {
        var source = new FakeContentWorkSource { PendingIds = [1, 2, 3] };
        var controller = CreateController(contentWorkSource: source);

        var result = await controller.GetContentWork(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new long[] { 1, 2, 3 }, Assert.IsAssignableFrom<IReadOnlyList<long>>(ok.Value));
    }

    [Fact]
    public async Task GetContentWorkItem_Exists_ReturnsOk()
    {
        var item = new ContentWorkItem(5, "line-msg-5", "image");
        var source = new FakeContentWorkSource();
        source.Items[5] = item;
        var controller = CreateController(contentWorkSource: source);

        var result = await controller.GetContentWorkItem(5, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(item, ok.Value);
    }

    [Fact]
    public async Task GetContentWorkItem_MissingOrNotPending_ReturnsNotFound()
    {
        var controller = CreateController(contentWorkSource: new FakeContentWorkSource());

        var result = await controller.GetContentWorkItem(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task MarkContentFailed_DelegatesToSource()
    {
        var source = new FakeContentWorkSource();
        var controller = CreateController(contentWorkSource: source);

        var result = await controller.MarkContentFailed(5, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(5, Assert.Single(source.Failed));
    }

    // === profiles ===

    [Fact]
    public async Task GetProfileStaleness_DelegatesToStore()
    {
        var store = new FakeProfileStore { StalenessToReturn = new ProfileStaleness(true, false) };
        var controller = CreateController(profileStore: store);

        var result = await controller.GetProfileStaleness("G1", "U1", DateTimeOffset.UtcNow, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var staleness = Assert.IsType<ProfileStaleness>(ok.Value);
        Assert.True(staleness.GroupStale);
        Assert.False(staleness.MemberStale);
    }

    [Fact]
    public async Task UpsertGroupProfile_DelegatesToStore()
    {
        var store = new FakeProfileStore();
        var controller = CreateController(profileStore: store);
        var summary = new GroupSummary("G1", "群組名", "https://example/pic.png");

        var result = await controller.UpsertGroupProfile(summary, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var (groupId, saved) = Assert.Single(store.UpsertedGroups);
        Assert.Equal("G1", groupId);
        Assert.Equal(summary, saved);
    }

    [Fact]
    public async Task UpsertMemberProfile_DelegatesToStore()
    {
        var store = new FakeProfileStore();
        var controller = CreateController(profileStore: store);
        var profile = new MemberProfile("U1", "顯示名", "https://example/pic.png");

        var result = await controller.UpsertMemberProfile(new MemberUpsertRequest("G1", profile), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var (groupId, userId, saved) = Assert.Single(store.UpsertedMembers);
        Assert.Equal("G1", groupId);
        Assert.Equal("U1", userId);
        Assert.Equal(profile, saved);
    }

    // === heartbeat（Edge 代寫自己的存活狀態，見 HeartbeatRequest 說明）===

    [Fact]
    public async Task ReportHeartbeat_DelegatesToStore_WithNullFingerprint()
    {
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest("Edge", "edge-host-1", OutboxPending: 3, OutboxOldestAgeSeconds: 42.5);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var (role, machineName, report, fingerprint) = Assert.Single(store.Upserted);
        Assert.Equal("Edge", role);
        Assert.Equal("edge-host-1", machineName);
        Assert.Equal(3, report.OutboxPending);
        Assert.Equal(42.5, report.OutboxOldestAgeSeconds);
        // Edge 不碰加密金鑰——不能拿 Core 自己的指紋去填 Edge 那列，否則「金鑰不一致」比對失效
        Assert.Null(fingerprint);
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("Line")]
    [InlineData("Db")]
    public async Task ReportHeartbeat_LegacyRoleNames_AreAccepted(string legacyRole)
    {
        // Role 直接寫進主鍵欄位，驗證只要求「能解析成 DeploymentMode」——舊名稱是合法的別名
        // （Full/Line/Db），跟 Deployment:Mode 設定鍵本身的相容性一致，不該被擋
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest(legacyRole, "host-1", null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ReportHeartbeat_UnknownRole_ReturnsBadRequest()
    {
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest("NotARealRole", "host-1", null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(store.Upserted);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task ReportHeartbeat_NumericRole_ReturnsBadRequest(string numericRole)
    {
        // Enum.TryParse<DeploymentMode> 是廣為人知的陷阱：只要字串能轉成底層 int，即使沒有
        // 對應具名成員也會回傳 true（"0" 甚至恰好等於 AllInOne 的底層值，但寫進 DB 的是原始
        // 字串 "0" 而不是 "AllInOne"，一樣是垃圾值）——驗證必須比對宣告的名稱本身，不能用
        // Enum.TryParse
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest(numericRole, "host-1", null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(store.Upserted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReportHeartbeat_EmptyMachineName_ReturnsBadRequest(string machineName)
    {
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest("Edge", machineName, null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(store.Upserted);
    }

    [Fact]
    public async Task ReportHeartbeat_MachineNameTooLong_ReturnsBadRequest()
    {
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest("Edge", new string('x', 129), null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(store.Upserted);
    }

    [Fact]
    public async Task ReportHeartbeat_MachineNameAtMaxLength_Succeeds()
    {
        var store = new FakeHeartbeatStore();
        var controller = CreateController(heartbeatStore: store);
        var request = new HeartbeatRequest("Edge", new string('x', 128), null, null);

        var result = await controller.ReportHeartbeat(request, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }
}
