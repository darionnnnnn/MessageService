namespace MessageService.Web.Dtos;

/// <summary>搜尋回應。results 之外還要帶 limitedByEncryption，是因為加密啟用時候選集
/// 在關鍵字比對前就被截斷成最新 300 則，使用者搜不到舊訊息時需要知道原因，
/// 否則「找不到」跟「超出可搜尋範圍」在畫面上完全無法區分。</summary>
public record MessageSearchResponseDto(
    IReadOnlyList<MessageSearchResultDto> Results,
    bool LimitedByEncryption);
