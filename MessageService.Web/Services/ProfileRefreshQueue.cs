using System.Threading.Channels;

namespace MessageService.Services;

public class ProfileRefreshQueue : IProfileRefreshQueue
{
    private readonly Channel<ProfileRefreshTask> _channel = Channel.CreateUnbounded<ProfileRefreshTask>();

    public void Enqueue(ProfileRefreshTask task) => _channel.Writer.TryWrite(task);

    public IAsyncEnumerable<ProfileRefreshTask> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
