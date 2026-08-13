using MessageService.Services;
using MessageService.Tests.TestSupport;

namespace MessageService.Tests.Services;

// 這組測試接手了原本在 DirectIngestSinkTests 的「入列」相關斷言——Stage 3 把入列責任從
// DirectIngestSink 搬到呼叫端（見該類別註解），這裡改測搬去的地方本身。
public class IngestSideEffectsTests
{
    private static IngestEnvelope Envelope(string groupId = "G1", string? userId = "U1") => new(
        WebhookEventId: "evt-1",
        LineMessageId: "m1",
        GroupId: groupId,
        UserId: userId,
        MessageType: "text",
        Text: "hello",
        StickerId: null,
        PackageId: null,
        EventTimestamp: DateTimeOffset.UtcNow,
        ReceivedAt: DateTimeOffset.UtcNow,
        HasContent: false,
        ContentFileName: null);

    [Fact]
    public void Apply_ResultHasContentId_EnqueuesDownload()
    {
        var queue = new FakeContentDownloadQueue();
        var profileQueue = new FakeProfileRefreshQueue();

        IngestSideEffects.Apply(Envelope(), new IngestResult(42), queue, profileQueue);

        Assert.Equal(42, Assert.Single(queue.Enqueued));
    }

    [Fact]
    public void Apply_ResultHasNoContentId_DoesNotEnqueueDownload()
    {
        var queue = new FakeContentDownloadQueue();
        var profileQueue = new FakeProfileRefreshQueue();

        IngestSideEffects.Apply(Envelope(), new IngestResult(null), queue, profileQueue);

        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public void Apply_AlwaysEnqueuesProfileRefresh_RegardlessOfContentId()
    {
        var queue = new FakeContentDownloadQueue();
        var profileQueue = new FakeProfileRefreshQueue();

        IngestSideEffects.Apply(Envelope(groupId: "G9", userId: "U9"), new IngestResult(null), queue, profileQueue);

        var task = Assert.Single(profileQueue.Enqueued);
        Assert.Equal("G9", task.GroupId);
        Assert.Equal("U9", task.UserId);
    }

    [Fact]
    public void Apply_UserIdNull_EnqueuesProfileRefreshWithNullUserId()
    {
        var queue = new FakeContentDownloadQueue();
        var profileQueue = new FakeProfileRefreshQueue();

        IngestSideEffects.Apply(Envelope(userId: null), new IngestResult(null), queue, profileQueue);

        var task = Assert.Single(profileQueue.Enqueued);
        Assert.Null(task.UserId);
    }

    [Fact]
    public void Apply_NullQueues_AreNoOpsWhenUsingNullImplementations()
    {
        // 「這台主機不接手」的情境用 Null 實作而不是傳 null 參考——這裡確認 Null 實作
        // 搭配 IngestSideEffects 呼叫不會出任何錯，符合 Program.cs 依 OutboundHere 換實作的設計
        IngestSideEffects.Apply(Envelope(), new IngestResult(42), new NullContentDownloadQueue(), new NullProfileRefreshQueue());
        // 沒有拋例外就是通過；Null 實作本身的行為由 NullContentDownloadQueueTests 驗證
    }
}
