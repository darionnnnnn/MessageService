namespace MessageService.Services;

/// <summary>串流版下載結果：Content 不再是整份 byte[]，避免大檔案（影片可達數百 MB）整份進
/// 記憶體。owner 是底層 HttpResponseMessage（真正呼叫 LINE API 時才有；測試用的假結果傳
/// null 即可）——Content 讀取仰賴它保持連線不被回收，Dispose 時要一起釋放。</summary>
public sealed class LineContentResult(Stream content, string? contentType, long? contentLength, IDisposable? owner = null)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string? ContentType { get; } = contentType;

    /// <summary>來源提供了 Content-Length 時才有值；沒有的話呼叫端要自行落地量長度
    /// （見 ContentDownloadService，SQLite 的分塊寫入需要預知總長度）。</summary>
    public long? ContentLength { get; } = contentLength;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner?.Dispose();
    }
}
