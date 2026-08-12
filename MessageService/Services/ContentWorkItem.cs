namespace MessageService.Services;

/// <summary>ContentDownloadService 下載一筆媒體內容所需要知道的一切——刻意不是完整的
/// MessageContent／GroupMessage 實體，只挑下載邏輯真正用到的欄位，讓 IContentWorkSource
/// 的兩套實作（直接查 DB vs 打 API）形狀一致、也讓 API 版的回應 body 保持精簡。</summary>
public record ContentWorkItem(long ContentId, string LineMessageId, string MessageType);
