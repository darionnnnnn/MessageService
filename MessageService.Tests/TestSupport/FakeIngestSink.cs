using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeIngestSink : IIngestSink
{
    public List<IngestEnvelope> Submitted { get; } = [];

    /// <summary>設定了就讓下一次（且只有下一次）SubmitAsync 呼叫拋出，模擬暫時性失敗；
    /// 用來驗證 OutboxForwarderService 的重試/退避行為。</summary>
    public Exception? ThrowOnNextSubmit { get; set; }

    public Task SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ThrowOnNextSubmit is { } ex)
        {
            ThrowOnNextSubmit = null;
            throw ex;
        }

        Submitted.Add(envelope);
        return Task.CompletedTask;
    }
}
