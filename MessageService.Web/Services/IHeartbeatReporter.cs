namespace MessageService.Services;

/// <summary>這台主機怎麼把自己的心跳送出去——有資料庫就直接寫（DbHeartbeatReporter），
/// 沒有資料庫（Edge）就打 Core 端的 heartbeat 端點（HttpHeartbeatReporter），
/// 跟 IContentWorkSource／IIngestSink 是同一種「依 HasDatabaseAccess 二選一」的分工方式。</summary>
public interface IHeartbeatReporter
{
    Task ReportAsync(HeartbeatReport report, CancellationToken cancellationToken);
}
