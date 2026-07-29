namespace MessageService.Services;

public interface IProfileRefreshQueue
{
    void Enqueue(ProfileRefreshTask task);
    IAsyncEnumerable<ProfileRefreshTask> ReadAllAsync(CancellationToken cancellationToken);
}
