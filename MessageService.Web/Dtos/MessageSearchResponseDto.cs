namespace MessageService.Web.Dtos;

/// <summary>搜尋回應。加密啟用時，密文無法以 SQL LIKE 比對，搜尋範圍受限於天數視窗與候選筆數上限。
/// 將限制拆分為結構化的天數視窗（恆常限制）與是否達到候選上限（忙碌時才觸發），避免以單一布林旗標常態誤報，
/// 讓使用者在內容真正被截斷時仍能獲得明確訊號。加密未啟用時 Limit 為 null。</summary>
public record MessageSearchResponseDto(
    IReadOnlyList<MessageSearchResultDto> Results,
    SearchLimitDto? Limit);

public record SearchLimitDto(int WindowDays, bool CandidateCapped);
