namespace MessageService.Web.Services;

/// <summary>
/// 非完整訊息內容的摘要顯示規則（側欄最後訊息預覽、訊息搜尋結果都用這份，避免各自維護一套）。
/// 文字訊息套遮蔽規則後截斷；其餘型別一律顯示型別標籤（媒體訊息沒有可搜/可顯示的文字內容）。
/// </summary>
public static class MessagePreviewFormatter
{
    private const int MaxLength = 30;

    public static string Format(string messageType, string? text, IMaskingRuleSet maskingRules, string groupId) =>
        messageType switch
        {
            "text" => Truncate(maskingRules.MaskText(groupId, text ?? string.Empty)),
            "sticker" => "[貼圖]",
            "image" => "[圖片]",
            "video" => "[影片]",
            "audio" => "[語音訊息]",
            "file" => "[檔案]",
            _ => "[訊息]"
        };

    private static string Truncate(string text) =>
        text.Length <= MaxLength ? text : text[..MaxLength] + "…";
}
