using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeHeartbeatReporter : IHeartbeatReporter
{
    public List<HeartbeatReport> Reported { get; } = [];

    public Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken)
    {
        Reported.Add(report);
        return Task.CompletedTask;
    }
}
