namespace MessageService.Services;

public interface IContentDownloadQueue
{
    void Enqueue(long messageContentId);
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);
}
