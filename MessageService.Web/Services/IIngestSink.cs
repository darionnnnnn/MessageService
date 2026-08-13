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
}
