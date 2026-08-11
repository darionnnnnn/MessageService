using MessageService.Services;

namespace MessageService.Outbox;

/// <summary>webhook 收進事件後的唯一出口——寫進本地 outbox 就算數，呼叫端不需要等落地完成。
/// 實作要保證：只要這個呼叫成功回傳，事件就不會再遺失（磁碟寫入失敗是另一個層級的問題，
/// 不在這個介面的責任範圍內）。</summary>
public interface IOutboxWriter
{
    Task EnqueueAsync(IngestEnvelope envelope, CancellationToken cancellationToken);
}
