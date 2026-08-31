namespace MessageService.Web.Dtos;

/// <summary>
/// 訊息流活性狀態 DTO。
/// Status 在伺服器端算好（"None"／"Ok"／"Silent"）而不是交給前端拿 LastMessageAt 自己算——
/// 避免用戶端時鐘跟伺服器不同步時算出不同結果。
/// </summary>
public record MessageFlowDto(DateTimeOffset? LastMessageAt, string Status);