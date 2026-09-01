namespace MessageService.Services;

public record GroupSummary(
    string GroupId,
    string? GroupName,
    string? PictureUrl,
    byte[]? PictureBytes = null,
    string? PictureContentType = null,
    /// <summary>這次頭貼下載是暫時性失敗（連不上、5xx、限流）：值得在短冷卻後重試。</summary>
    bool PictureDownloadFailed = false,
    /// <summary>這個頭貼網址永久拿不到（檔案超過上限、404／410）：重試多少次都一樣，
    /// 落地時會把它記進 PictureFetchedUrl，讓 staleness 不再把這筆判為缺圖。</summary>
    bool PicturePermanentlyUnavailable = false);
