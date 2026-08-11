namespace MessageService.Services;

/// <summary>把一筆 <see cref="IngestEnvelope"/> 落地的方式——直連資料庫（Full／Db 模式）或推去
/// 遠端 ingest API（Line 模式，Stage 2 才會有實作）。這是本次解耦架構唯一有兩套實作的介面：
/// webhook 收進來後的路徑永遠只有「寫 outbox」一種，差異全部收斂在這裡，
/// 不必為每個資料庫操作各自抽一層。
///
/// 必須是冪等的：同一個 <see cref="IngestEnvelope.WebhookEventId"/> 呼叫多次（outbox 重試、
/// 或上游重送）要能安全地重複執行而不產生重複資料——真正的保證來自資料庫的唯一索引，
/// 這裡的實作只需要把「已存在」也當成成功處理。</summary>
public interface IIngestSink
{
    /// <summary>成功（含判定為重複而略過）就正常回傳，讓呼叫端可以安心把 outbox 項目刪掉；
    /// 暫時性失敗（連不上資料庫／API）要向外拋例外，讓 outbox 保留該項目並按退避排程重試。</summary>
    Task SubmitAsync(IngestEnvelope envelope, CancellationToken cancellationToken);
}
