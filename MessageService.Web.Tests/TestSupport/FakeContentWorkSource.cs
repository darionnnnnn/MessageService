using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeContentWorkSource : IContentWorkSource
{
    public List<long> PendingIds { get; set; } = [];
    public Dictionary<long, ContentWorkItem> Items { get; } = new();
    public List<(long ContentId, byte[] Content, string? ContentType, string OwnerId)> Completed { get; } = [];
    public List<(long ContentId, string OwnerId)> Failed { get; } = [];

    public int GetPendingIdsCallCount { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<long>>>? OnGetPendingIds { get; set; }

    public List<bool> ReclaimDownloadingCalls { get; } = [];
    public List<bool> IsStartupCalls { get; } = [];
    public List<string> OwnerIdCalls { get; } = [];

    public Task<IReadOnlyList<long>> GetPendingIdsAsync(bool reclaimDownloading, bool isStartup, string ownerId, CancellationToken cancellationToken)
    {
        GetPendingIdsCallCount++;
        ReclaimDownloadingCalls.Add(reclaimDownloading);
        IsStartupCalls.Add(isStartup);
        OwnerIdCalls.Add(ownerId);
        if (OnGetPendingIds is not null)
        {
            return OnGetPendingIds(cancellationToken);
        }
        return Task.FromResult<IReadOnlyList<long>>(PendingIds);
    }

    public Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(contentId));

    public Task CompleteAsync(long contentId, Stream content, long contentLength, string? contentType, string ownerId, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        content.CopyTo(memoryStream);
        Completed.Add((contentId, memoryStream.ToArray(), contentType, ownerId));
        return Task.CompletedTask;
    }

    public Task FailAsync(long contentId, string ownerId, CancellationToken cancellationToken)
    {
        Failed.Add((contentId, ownerId));
        return Task.CompletedTask;
    }
}
