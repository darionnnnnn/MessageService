namespace MessageService.Services;

/// <summary>ContentDownloadService 取得待辦、回報結果的資料來源——直接查本機資料庫
/// （Full／Db 模式，DbContentWorkSource）或打遠端 ingest API（Line 模式且
/// Line:OutboundHere=true，ApiContentWorkSource）。跟 IIngestSink 是同一種切法：
/// 下載服務本身的重試／轉檔等待邏輯完全不變，只有「這幾個資料操作去哪裡做」抽換。</summary>
public interface IContentWorkSource
{
    /// <summary>啟動接續用：撈出 Pending 與 Failed 的內容 Id（Failed 一併重設為 Pending——
    /// 常見成因是設定錯誤而非內容本身有問題，修好設定重啟後應該自動補跑）。</summary>
    Task<IReadOnlyList<long>> GetPendingIdsAsync(CancellationToken cancellationToken);

    /// <summary>取單筆詳情。回傳 null 代表這筆已經不是 Pending 了（已被處理過或不存在）——
    /// 跟現行 ContentDownloadService.ProcessAsync 的狀態檢查是同一個判斷，只是搬進這裡。</summary>
    Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken);

    Task CompleteAsync(long contentId, byte[] content, string? contentType, CancellationToken cancellationToken);

    Task FailAsync(long contentId, CancellationToken cancellationToken);
}
