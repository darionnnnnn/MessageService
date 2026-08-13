namespace MessageService.Models;

public enum DownloadStatus
{
    Pending,

    /// <summary>正在把下載到的內容寫進 blob 欄位——DbContentWorkSource.CompleteAsync 開頭用
    /// 一句 ExecuteUpdateAsync 把 Pending 改成這個狀態才動手寫，避免多個 worker 共讀同一個
    /// Channel 時，同一筆內容被兩個 worker 同時寫入同一顆 blob 而交錯損毀。行程重啟後沒有
    /// 機制自動回收（worker 崩潰但行程沒重啟的這段期間會卡住），會被下次啟動的
    /// RequeuePendingAsync 一併撿回改回 Pending 重跑，見 DbContentWorkSource.GetPendingIdsAsync。</summary>
    Downloading,

    Completed,
    Failed
}
