namespace MessageService.Services;

/// <summary>IIngestSink 落地失敗、但重試不會改變結果（例如 ingest API 判定 payload 格式不合，
/// 回 400）。跟一般例外的差別在於 forwarder 的處置：一般例外照退避排程重試，
/// 這個例外會讓 outbox 項目直接標記死信，不浪費重試次數也不刷無意義的 log。</summary>
public class PermanentIngestException(string message, Exception? innerException = null)
    : Exception(message, innerException);
