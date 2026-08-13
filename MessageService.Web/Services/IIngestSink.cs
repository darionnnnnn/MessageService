namespace MessageService.Services;

/// <summary>SubmitAsync 的結果。ContentId 只在該訊息帶媒體內容時有值——這是 Stage 3 為了讓
/// 拆機模式的媒體下載能運作而加的：MessageContent.Id 是資料庫產生的自增值，Line 模式的
/// ContentDownloadService 沒有資料庫可查，只能靠這裡把 Id 帶回來才知道要下載哪一筆。</summary>
public record IngestResult(long? ContentId);

/// <summary>把一筆 <see cref="IngestEnvelope"/> 落地的方式——直連資料庫（Full／Db 模式）或推去
/// 遠端 ingest API（Line 模式）。這是本次解耦架構唯一有兩套實作的介面：
/// webhook 收進來後的路徑永遠只有「寫 outbox」一種，差異全部收斂在這裡，
/// 不必為每個資料庫操作各自抽一層。
///
/// 必須是冪等的：同一個 <see cref="IngestEnvelope.WebhookEventId"/> 呼叫多次（outbox 重試、
/// 或上游重送）要能安全地重複執行而不產生重複資料——真正的保證來自資料庫的唯一索引，
/// 這裡的實作只需要把「已存在」也當成成功處理。**判定為重複時一樣要回傳既有那筆的
/// ContentId**：outbox 重試代表前一次的回應可能遺失了，若這次回 null，該筆媒體要等到
/// 下次服務重啟的啟動重撈才會被撿回，形同暫時卡住。</summary>
public interface IIngestSink
{
    /// <summary>成功（含判定為重複而略過）就正常回傳，讓呼叫端可以安心把 outbox 項目刪掉；
    /// 暫時性失敗（連不上資料庫／API）要向外拋例外，讓 outbox 保留該項目並按退避排程重試。</summary>
    Task<IngestResult> SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>批次落地多筆事件，取代逐筆各自一次 RTT（見問題9：拆機模式下 outbox 排空是
    /// 逐筆 HTTP round-trip，量大時吞吐只有 20~30 筆/秒）。預設實作逐筆呼叫 SubmitAsync——
    /// DirectIngestSink（AllInOne／Core 直連資料庫）本來就沒有每筆一次 RTT 的問題，不需要
    /// 真的批次，用這個預設值就好；HttpIngestSink（Edge）覆寫這個方法，一次 HTTP 請求送整批。
    ///
    /// 順序在單次呼叫內保證（不並行處理任兩筆）：viewer 依 Id 排序顯示，並行落地會讓
    /// GroupMessages 的自增 Id 跟 EventTimestamp 對不上、打亂同群組訊息順序。
    ///
    /// 中途遇到 PermanentIngestException（例如某筆 payload 格式不合）只影響那一筆，其餘
    /// 照常處理；遇到其他例外（暫時性失敗，例如連線中斷）視為整批這次沒處理完，直接往外拋，
    /// 呼叫端據此讓整批的 outbox 項目照退避排程重試——已經成功的項目重送是安全的（冪等）。</summary>
    async Task<IReadOnlyList<IngestBatchItemResult>> SubmitBatchAsync(
        IReadOnlyList<IngestEnvelope> envelopes, CancellationToken cancellationToken)
    {
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
}
