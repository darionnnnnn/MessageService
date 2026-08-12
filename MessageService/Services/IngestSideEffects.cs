namespace MessageService.Services;

/// <summary>一筆 IngestEnvelope 成功落地（無論走 DirectIngestSink 或經 HttpIngestSink 打
/// 遠端 API）之後，「這台主機」要不要對它做後續處理——入列媒體下載、入列頭貼刷新。
/// 兩個呼叫端（IngestController 收到 Line 端轉來的請求、OutboxForwarderService 排空本機
/// outbox）各自用自己注入的 IContentDownloadQueue／IProfileRefreshQueue 呼叫這裡，
/// 那兩個佇列在 DI 註冊時已經依 Line:OutboundHere 決定是真 Channel 還是 Null 實作——
/// 呼叫端因此不必知道自己在哪個模式，這裡也不必知道，只有 Program.cs 的註冊矩陣知道。</summary>
public static class IngestSideEffects
{
    public static void Apply(
        IngestEnvelope envelope, IngestResult result,
        IContentDownloadQueue downloadQueue, IProfileRefreshQueue profileRefreshQueue)
    {
        if (result.ContentId is { } contentId)
        {
            downloadQueue.Enqueue(contentId);
        }

        profileRefreshQueue.Enqueue(new ProfileRefreshTask(envelope.GroupId, envelope.UserId));
    }
}
