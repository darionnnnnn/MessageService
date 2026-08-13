using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeIngestSink : IIngestSink
{
    public List<IngestEnvelope> Submitted { get; } = [];

    /// <summary>設定了就讓下一次（且只有下一次）SubmitAsync 呼叫拋出，模擬暫時性失敗；
    /// 用來驗證 OutboxForwarderService 的重試/退避行為。</summary>
    public Exception? ThrowOnNextSubmit { get; set; }

    /// <summary>依 WebhookEventId 指定要拋出的例外——比 ThrowOnNextSubmit 更精準，用來測試
    /// 批次中「特定一筆失敗、其餘照常」的情境（見 IIngestSink.SubmitBatchAsync 說明），不用
    /// 依賴呼叫順序猜第幾次呼叫會踩到目標項目。</summary>
    public Dictionary<string, Exception> ThrowForWebhookEventId { get; } = [];

    /// <summary>下一次（且只有下一次）成功的 SubmitAsync 要回傳的 ContentId——預設 null
    /// （純文字訊息）；測試媒體訊息／IngestSideEffects 入列行為時可以指定。</summary>
    public long? NextContentId { get; set; }

    /// <summary>設定了就讓 SubmitBatchAsync 直接回傳這份結果，不逐筆呼叫 SubmitAsync——
    /// 用來模擬「批次回應沒提到某些項目」這類對端行為異常（正常實作不會發生），
    /// 驗證 forwarder 對異常回應的防禦處置。</summary>
    public IReadOnlyList<IngestBatchItemResult>? BatchResultsOverride { get; set; }

    public async Task<IReadOnlyList<IngestBatchItemResult>> SubmitBatchAsync(
        IReadOnlyList<IngestEnvelope> envelopes, CancellationToken cancellationToken)
    {
        if (BatchResultsOverride is { } overrideResults)
        {
            return overrideResults;
        }

        // C# 不允許從實作類別呼叫被自己覆寫掉的介面預設實作，這裡照抄 IIngestSink 預設實作的
        // 語意（逐筆、PermanentIngestException 只影響該筆、其他例外整批往外拋），讓沒設定
        // override 的既有測試行為完全不變
        var results = new List<IngestBatchItemResult>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            try
            {
                var result = await SubmitAsync(envelope, cancellationToken);
                results.Add(new IngestBatchItemResult(envelope.WebhookEventId, result.ContentId, false, null));
            }
            catch (PermanentIngestException ex)
            {
                results.Add(new IngestBatchItemResult(envelope.WebhookEventId, null, true, ex.Message));
            }
        }
        return results;
    }

    public Task<IngestResult> SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken)
    {
        if (ThrowForWebhookEventId.Remove(envelope.WebhookEventId, out var scopedEx))
        {
            throw scopedEx;
        }

        if (ThrowOnNextSubmit is { } ex)
        {
            ThrowOnNextSubmit = null;
            throw ex;
        }

        Submitted.Add(envelope);
        var result = new IngestResult(NextContentId);
        NextContentId = null;
        return Task.FromResult(result);
    }
}
