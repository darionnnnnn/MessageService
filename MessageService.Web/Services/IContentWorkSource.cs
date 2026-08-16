namespace MessageService.Services;

/// <summary>ContentDownloadService 取得待辦、回報結果的資料來源——直接查本機資料庫
/// （Full／Db 模式，DbContentWorkSource）或打遠端 ingest API（Line 模式且
/// Line:OutboundHere=true，ApiContentWorkSource）。跟 IIngestSink 是同一種切法：
/// 下載服務本身的重試／轉檔等待邏輯完全不變，只有「這幾個資料操作去哪裡做」抽換。</summary>
public interface IContentWorkSource
{
    /// <summary>撈出待處理的內容 Id：Pending，加上仍可重試的 Failed（一併重設為 Pending——
    /// 常見成因是設定錯誤而非內容本身有問題，修好設定重啟後應該自動補跑）。
    /// <paramref name="reclaimDownloading"/> 為 true 時連 Downloading 也一併改回 Pending 撿回——
    /// 這代表呼叫端保證「此刻沒有自己的 worker 在跑」，只有啟動接續成立；週期重掃時 worker
    /// 可能正在下載大檔，必須傳 false，否則會把正在下載的列打回 Pending，讓另一個 worker
    /// 再度認領、兩邊同時寫同一顆 blob（CompleteAsync 的認領互斥就是為了擋這個）。</summary>
    Task<IReadOnlyList<long>> GetPendingIdsAsync(bool reclaimDownloading, CancellationToken cancellationToken);

    /// <summary>取單筆詳情。回傳 null 代表這筆已經不是 Pending 了（已被處理過或不存在）——
    /// 跟現行 ContentDownloadService.ProcessAsync 的狀態檢查是同一個判斷，只是搬進這裡。</summary>
    Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken);

    /// <summary>contentLength 必須是 content 這個 Stream 讀到底的實際位元組數（不是預估值）——
    /// SQLite 實作要用它先配置固定大小的 blob 再串流填入，見 DbContentWorkSource。呼叫端
    /// （ContentDownloadService）若來源沒提供已知長度，要自行落地量出來，見該類別的說明。</summary>
    Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, CancellationToken cancellationToken);

    Task FailAsync(long contentId, CancellationToken cancellationToken);
}
