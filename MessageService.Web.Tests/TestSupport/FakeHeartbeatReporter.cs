using MessageService.Services;

namespace MessageService.Tests.TestSupport;

public class FakeHeartbeatReporter : IHeartbeatReporter
{
    public List<HeartbeatReport> Reported { get; } = [];

    /// <summary>設為 true 讓每次 ReportAsync 都拋例外，模擬送不到（單向防火牆下的穩態）。</summary>
    public bool Failing { get; set; }

    /// <summary>指定要拋的例外型別——用來模擬 HttpClient 逾時那種「不是停機的 TaskCanceledException」。</summary>
    public Exception? FailureToThrow { get; set; }

    public Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken)
    {
        if (FailureToThrow is { } ex)
        {
            throw ex;
        }

        if (Failing)
        {
            throw new HttpRequestException("模擬心跳送不到");
        }

        Reported.Add(report);
        return Task.CompletedTask;
    }
}
