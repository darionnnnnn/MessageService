namespace MessageService.Services;

/// <summary>Edge 端的媒體工作來源，依目前通道方向二選一：推送通得過就照原本打 Core 的
/// ingest API（<see cref="ApiContentWorkSource"/>），推送暫停時改用 Core 派工進來的記憶體
/// 暫存（<see cref="StagingContentWorkSource"/>）。
///
/// 沒有這一層的話，Auto 模式下推送不通時只有訊息與心跳會反轉，媒體會繼續往打不通的 Core
/// 送而靜默失效。<see cref="ApiContentWorkSource"/> 延後解析：Pull 模式下它需要的
/// Ingest:BaseUrl 允許留空，提早解析會炸。</summary>
public class ChannelAwareContentWorkSource(
    EdgeChannelState channelState,
    StagingContentWorkSource staging,
    IServiceProvider serviceProvider) : IContentWorkSource
{
    private IContentWorkSource Active => channelState.UsePullResources
        ? staging
        : serviceProvider.GetRequiredService<ApiContentWorkSource>();

    public Task<IReadOnlyList<long>> GetPendingIdsAsync(
        bool reclaimDownloading, TimeSpan? startupAge, string ownerId, CancellationToken cancellationToken) =>
        Active.GetPendingIdsAsync(reclaimDownloading, startupAge, ownerId, cancellationToken);

    public Task<ContentWorkItem?> GetAsync(long contentId, CancellationToken cancellationToken) =>
        Active.GetAsync(contentId, cancellationToken);

    public Task CompleteAsync(
        long contentId, Stream content, long contentLength, string? contentType, string ownerId,
        CancellationToken cancellationToken) =>
        Active.CompleteAsync(contentId, content, contentLength, contentType, ownerId, cancellationToken);

    public Task FailAsync(long contentId, string ownerId, CancellationToken cancellationToken) =>
        Active.FailAsync(contentId, ownerId, cancellationToken);
}
