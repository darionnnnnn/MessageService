namespace MessageService.Web.Dtos;

/// <summary>Truncated＝這個視窗內符合條件的訊息比 MessagesController.MessageWindowLimit 還多，
/// 這批已經被截斷（不是「沒有更早的訊息」，兩者是不同概念——後者見 HasMore）。</summary>
public record MessagesPageDto(IReadOnlyList<MessageDto> Messages, bool HasMore, long? LatestId, bool Truncated);
