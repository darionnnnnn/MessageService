namespace MessageService.Services;

/// <summary>組出具名 HttpClient 的 BaseAddress。
///
/// **一定要保留結尾斜線**：HttpClient 把相對位址（本專案一律用 "api/xxx" 這種不帶開頭斜線的
/// 寫法）依 RFC 3986 對 BaseAddress 做解析，而該規則會把基底最後一個「非目錄」路徑段換掉。
/// 站台部署在 IIS 子應用程式底下時（例如 http://host/MSLine），少了結尾斜線就會變成
/// http://host/api/xxx——子應用路徑整段消失、對方回 404，而錯誤訊息完全看不出原因。
/// 設定值有沒有寫斜線不該由使用者記得，在這裡統一補上。</summary>
public static class HttpBaseAddress
{
    public static Uri Create(string baseUrl)
    {
        var trimmed = baseUrl.Trim();

        // 帶 query／fragment 的基底位址本來就不該出現，補斜線只會更錯——原樣交給 Uri 判斷
        if (!trimmed.EndsWith('/') && !trimmed.Contains('?') && !trimmed.Contains('#'))
        {
            trimmed += "/";
        }

        return new Uri(trimmed);
    }
}
