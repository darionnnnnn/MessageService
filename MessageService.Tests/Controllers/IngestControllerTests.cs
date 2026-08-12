using MessageService.Controllers;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageService.Tests.Controllers;

// 端到端行為（含真實 DirectIngestSink＋資料庫、認證中介層）已在 DeploymentModeTests 用真實
// host 驗證；這裡只單獨測 controller 對 IIngestSink 例外的映射邏輯，用 FakeIngestSink
// 模擬 DirectIngestSinkTests 已經釘住的「暫時性失敗會往外拋」這個契約在 HTTP 層怎麼呈現。
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

    [Fact]
    public async Task SubmitEvent_SinkSucceeds_ReturnsOk()
    {
        var sink = new FakeIngestSink();
        var controller = new IngestController(sink, NullLogger<IngestController>.Instance);

        var result = await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Single(sink.Submitted);
    }

    [Fact]
    public async Task SubmitEvent_SinkThrows_Returns500()
    {
        // 對應 DirectIngestSink 判定「暫時性失敗」時往外拋的情境——這裡驗證它會變成
        // Line 端的 HttpIngestSink 認得出來的「可重試」狀態碼（見 HttpIngestSinkTests
        // 對非 400 狀態碼一律當可重試的斷言）
        var sink = new FakeIngestSink { ThrowOnNextSubmit = new InvalidOperationException("db unreachable") };
        var controller = new IngestController(sink, NullLogger<IngestController>.Instance);

        var result = await controller.SubmitEvent(SampleEnvelope(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.StatusCode);
    }

    [Fact]
    public async Task SubmitEvent_SinkThrowsOperationCanceled_PropagatesRatherThanReturning500()
    {
        // 停機取消不是「這筆處理失敗」，是請求本身被中止——讓例外照 ASP.NET Core 正常管線
        // 處理（通常轉譯為連線中止），不應該包裝成看起來像業務邏輯失敗的 500
        var sink = new FakeIngestSink { ThrowOnNextSubmit = new OperationCanceledException() };
        var controller = new IngestController(sink, NullLogger<IngestController>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.SubmitEvent(SampleEnvelope(), CancellationToken.None));
    }
}
