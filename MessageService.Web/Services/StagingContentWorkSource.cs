namespace MessageService.Services;

/// <summary>拉取模式下 Edge 端的媒體工作來源：待辦不是自己去 Core 查來的，而是 Core 每次
/// poll 時派下來的（見 <see cref="EdgeContentStaging"/>）；下載完成後也不是直接上傳，
/// 而是放進記憶體暫存等 Core 來取。
///
/// 介面契約與 <see cref="DbContentWorkSource"/> 一致的地方：
/// <c>GetPendingIdsAsync</c> 是可重複掃描的（不是取出即消耗）——ContentDownloadService 會
/// 週期重掃，若這裡改成 drain 語意，第二輪會空手而回、下載失敗的項目再也回不來。
/// 租約回收在 Core 端做（那裡才有資料庫），這裡不需要也不能自己回收。</summary>
public class StagingContentWorkSource(EdgeContentStaging staging) : IContentWorkSource
{
    public Task<IReadOnlyList<long>> GetPendingIdsAsync(
        bool reclaimDownloading, TimeSpan? startupAge, string ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(staging.GetPendingIds());

    public Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken) =>
        Task.FromResult(staging.GetDispatched(contentId));

    public async Task CompleteAsync(
        long contentId, Stream content, long contentLength, string? contentType, string ownerId,
        CancellationToken cancellationToken)
    {
        // 這裡必須整份進記憶體：Core 要靠獨立的 GET 取回，來源串流那時早就關了。
        // 單筆上限由 Ingest:MaxContentBytes 在 Core 端的派工階段把關，總量上限由暫存區負責
        using var buffer = new MemoryStream(capacity: contentLength > 0 && contentLength < int.MaxValue
            ? (int)contentLength
            : 0);
        await content.CopyToAsync(buffer, cancellationToken);

        if (!staging.TryStage(contentId, buffer.ToArray(), contentType))
        {
            // 暫存滿了：內容沒收下，但派工留著（見 EdgeContentStaging.TryStage）。
            // 丟例外而不是靜默返回，ContentDownloadService 才會知道這次沒完成
            throw new InvalidOperationException(
                $"媒體暫存區已達上限（Ingest:PullStagingMaxBytes），內容 {contentId} 這次無法暫存。" +
                "派工仍保留著，等暫存騰出空間後由既有的下載重試接手。");
        }
    }

    public Task FailAsync(long contentId, string ownerId, CancellationToken cancellationToken)
    {
        staging.MarkFailed(contentId);
        return Task.CompletedTask;
    }
}
