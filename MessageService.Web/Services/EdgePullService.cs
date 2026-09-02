using System.Net.Http.Json;
using System.Text.Json;
using MessageService.Controllers;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

/// <summary>Core 端的反向通道：防火牆只開通 core→edge 時，由這台主動去 Edge 拉訊息與心跳。
///
/// 啟停由「最後一次收到<b>推送</b>心跳的時間」決定（見 <see cref="PushHeartbeatTracker"/>）——
/// 超過 Ingest:PullActivationSeconds 沒收到就開始輪詢，推送恢復就停止。這裡刻意不看
/// HostHeartbeats 表：輪詢自己拉回來的心跳也會寫進那張表，用表當判斷來源會讓輪詢把自己停掉。
///
/// 只在設定了 Ingest:EdgeBaseUrl 時註冊（見服務註冊矩陣）——沒設就完全不存在，
/// 行為與沒有這個功能時一致。</summary>
public class EdgePullService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    PushHeartbeatTracker pushHeartbeatTracker,
    TimeProvider timeProvider,
    IOptions<IngestOptions> ingestOptions,
    IOptions<ContentDownloadOptions> contentOptions,
    ILogger<EdgePullService> logger) : BackgroundService
{
    /// <summary>具名 HttpClient：短逾時，只用於 poll／ack 這種小 JSON 往返。
    /// blob 取回（作業C）另有長逾時的 client，兩者不可共用。</summary>
    public const string HttpClientName = "edge-pull";

    /// <summary>具名 HttpClient：長逾時，只用於取回 blob（可達數百 MB）。
    /// 與 poll／ack 的短逾時 client 分開，慢的大檔不能拖垮每秒一次的輪詢節奏。</summary>
    public const string ContentHttpClientName = "edge-pull-content";

    /// <summary>已知可取回、還沒處理完的內容 Id。取回在獨立迴圈進行，poll 只負責把 Id 放進來——
    /// 一份數百 MB 的附檔要傳好幾分鐘，不能卡住每秒一次的心跳與訊息通道。
    /// 同一個 Id 在處理完之前不會重複入列，所以大檔傳到一半不會被再要求一次。</summary>
    private readonly HashSet<long> _inFlightContent = [];

    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _contentQueue = new();

    /// <summary>剛落地、還沒派給 Edge 下載的媒體 Id。比照推送模式「落地即入列」的節奏，
    /// 不必等下一次全表掃描。</summary>
    private readonly HashSet<long> _freshContentIds = [];

    /// <summary>上次做全表掃描（含回收逾期認領、把可重試的 Failed 撿回）的時點。
    /// 這個掃描有副作用：它會把 Failed 重設成 Pending。每秒做一次的話，一筆永遠下載不到的
    /// 內容會在幾秒內燒完 ContentDownload:MaxFailedRetries——推送模式下這個消耗節奏是
    /// RequeueIntervalMinutes（分鐘級），這裡必須比照。</summary>
    private DateTimeOffset? _lastFullScanAt;

    /// <summary>壞 payload 的告警節流時點：反序列化失敗的項目不 ack、會每輪重新出現，
    /// 每輪都記 Error 會以每秒一則的速度刷爆 log。</summary>
    private DateTimeOffset? _lastBadPayloadWarningAt;

    /// <summary>要派給 Edge 刷新的名稱／頭貼對象，由落地的訊息累積而來。
    /// 用 (群組, 成員) 值組當鍵，值是**上次派出的時刻**（DateTimeOffset.MinValue＝還沒派過）。
    ///
    /// **派出後不移除**：Edge 可能在冷卻窗口內把派工靜默丟棄（例如通道還沒切到拉取模式時
    /// staleness 查詢失敗），派出即清空的話那筆就永遠丟了，要等該群組下一則新訊息才會重來——
    /// 實測到的「名稱／頭貼一直出不來」就是這個。改成留著、每 ProfileRedispatchInterval
    /// 重派一次，直到 Core 這邊查到它已經不過期（＝Edge 的結果已經落地）才移除。</summary>
    private readonly Dictionary<(string GroupId, string? UserId), DateTimeOffset> _pendingProfileWork = [];

    /// <summary>同一個刷新對象最短的重派間隔。要夠短才能在 Edge 結束短冷卻後立刻補上
    /// （見 ProfileRefreshService.InternalFailureRetryAfter，同為 30 秒），又不能每秒重派——
    /// 每次派工前都要查一次 staleness，那是 Core 端的資料庫查詢。</summary>
    private static readonly TimeSpan ProfileRedispatchInterval = TimeSpan.FromSeconds(30);

    private readonly IngestOptions _options = ingestOptions.Value;

    /// <summary>連續失敗次數，決定退避倍率；成功歸零。</summary>
    private int _consecutiveFailures;

    /// <summary>目前是否處於「輪詢中」狀態，只用來讓狀態轉換各記一次 log。</summary>
    private bool _pulling;

    /// <summary>持續失敗時的摘要節流時點，避免 1 秒一次的失敗把 log 刷爆。</summary>
    private DateTimeOffset? _lastFailureSummaryAt;

    /// <summary>暫存已滿的告警節流時點——背壓本身不是錯誤，但持續發生代表媒體一直下不完，
    /// 完全不出聲的話「圖片一直抓不到」會查不出原因。</summary>
    private DateTimeOffset? _lastBackpressureWarningAt;

    /// <summary>取回失敗的告警節流時點：失敗的內容下一輪 poll 就會重新入列，
    /// 持續失敗時每秒都會再試一次，不節流的話一樣刷爆 log。</summary>
    private DateTimeOffset? _lastFetchFailureWarningAt;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunPollLoopAsync(stoppingToken), RunContentLoopAsync(stoppingToken));

    private async Task RunPollLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // PollOnceAsync 內部已處理預期中的失敗；走到這裡代表落地或資料庫端出了非預期的錯，
                // 一樣要退避，不能讓迴圈以 1 秒節奏空轉重試
                RecordFailure(ex);
            }

            try
            {
                await Task.Delay(CurrentDelay(), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>blob 取回迴圈，與 poll 迴圈完全分開跑：附檔可達數百 MB、用的是十分鐘逾時的
    /// client，塞在 poll 迴圈裡會讓心跳與訊息在傳輸期間整個停住。</summary>
    private async Task RunContentLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessContentQueueOnceAsync(stoppingToken))
            {
                try
                {
                    await Task.Delay(CurrentDelay(), timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>取回佇列裡的下一筆內容。回傳是否真的處理了一筆（佇列空時回 false）。
    /// 公開方法讓測試不必跑背景迴圈就能驗證單次取回的行為。</summary>
    public async Task<bool> ProcessContentQueueOnceAsync(CancellationToken cancellationToken)
    {
        if (!_contentQueue.TryDequeue(out var contentId))
        {
            return false;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var workSource = scope.ServiceProvider.GetRequiredService<IContentWorkSource>();
            var ownerId = scope.ServiceProvider.GetRequiredService<ProcessOwnerId>().Value;
            await FetchAndLandContentAsync(workSource, contentId, ownerId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取回失敗（斷線、截斷、長度不符）不 ack：Edge 保留著暫存，下一輪 poll
            // 會再把它列進 ReadyContentIds，那時重新入列重取。持續失敗每秒都會再試，告警要節流
            var now = timeProvider.GetUtcNow();
            if (_lastFetchFailureWarningAt is not { } last || now - last >= TimeSpan.FromMinutes(10))
            {
                _lastFetchFailureWarningAt = now;
                logger.LogWarning(ex,
                    "從 Edge 取回內容 {ContentId} 失敗，會持續重試；這則告警每 10 分鐘最多記一次。", contentId);
            }
        }
        finally
        {
            lock (_inFlightContent)
            {
                _inFlightContent.Remove(contentId);
            }
        }

        return true;
    }

    /// <summary>判斷現在該不該輪詢：從未收過推送心跳（例如 edge→core 一開始就不通）也算該輪詢，
    /// 不必等門檻過去。</summary>
    public bool ShouldPull()
    {
        var lastPush = pushHeartbeatTracker.LastReceivedAt;
        if (lastPush is null)
        {
            return true;
        }

        return timeProvider.GetUtcNow() - lastPush.Value >= TimeSpan.FromSeconds(_options.PullActivationSeconds);
    }

    /// <summary>目前該等多久再跑下一輪：正常是 PullIntervalSeconds，連續失敗時指數退避到
    /// PullFailureMaxBackoffSeconds 為止。</summary>
    public TimeSpan CurrentDelay()
    {
        var baseSeconds = Math.Max(1, _options.PullIntervalSeconds);
        if (_consecutiveFailures == 0)
        {
            return TimeSpan.FromSeconds(baseSeconds);
        }

        var maxSeconds = Math.Max(baseSeconds, _options.PullFailureMaxBackoffSeconds);
        // 指數成長，但先夾住指數本身，避免連續失敗久了之後 Math.Pow 溢位
        var exponent = Math.Min(_consecutiveFailures - 1, 30);
        var seconds = baseSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }

    /// <summary>跑一次輪詢。回傳是否真的發出了 poll（推送通道還活著時回 false）。
    /// 公開方法讓測試不必跑計時迴圈就能驗證單次行為（比照 OutboxForwarderService.ProcessBatchAsync）。</summary>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!ShouldPull())
        {
            if (_pulling)
            {
                _pulling = false;
                logger.LogInformation(
                    "已收到 Edge 的推送心跳，停止輪詢 {EdgeBaseUrl}，改回由 Edge 主動推送。", _options.EdgeBaseUrl);
            }
            return false;
        }

        if (!_pulling)
        {
            _pulling = true;
            logger.LogInformation(
                "超過 {Seconds} 秒沒收到 Edge 的推送心跳，開始輪詢 {EdgeBaseUrl} 取回訊息與心跳。",
                _options.PullActivationSeconds, _options.EdgeBaseUrl);
        }

        using var scope = scopeFactory.CreateScope();

        EdgePollResponse? response;
        IReadOnlyList<ContentWorkItem> dispatch = [];
        IReadOnlyList<EdgeProfileWorkItem> profileDispatch = [];
        var client = httpClientFactory.CreateClient(HttpClientName);
        try
        {
            dispatch = await BuildDispatchAsync(scope.ServiceProvider, cancellationToken);
            profileDispatch = await BuildProfileDispatchAsync(scope.ServiceProvider, cancellationToken);
            using var pollResponse = await client.PostAsJsonAsync(
                "api/edge/poll", new EdgePollRequest(dispatch, profileDispatch), cancellationToken);
            pollResponse.EnsureSuccessStatusCode();
            response = await pollResponse.Content.ReadFromJsonAsync<EdgePollResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 這兩批派工在組請求時就從累積集合裡取走了，沒送出去就得放回去——
            // profile 不放回要等下一則新訊息才會再被觸發；媒體不放回要等下一次全表掃描
            // （最長 RequeueIntervalMinutes）才被撿回，「落地即派」的意圖就斷了
            RestoreDispatch(dispatch);
            RecordFailure(ex);
            return true;
        }

        if (response is null)
        {
            RestoreDispatch(dispatch);
            RecordFailure(new InvalidOperationException("Edge 的 poll 回應無法反序列化。"));
            return true;
        }

        WarnIfBackpressured(dispatch, response);
        await WriteHeartbeatAsync(scope.ServiceProvider, response, cancellationToken);
        var acknowledged = await LandMessagesAsync(scope.ServiceProvider, response, cancellationToken);
        await HandleContentAsync(scope.ServiceProvider, response, cancellationToken);
        await HandleProfileResultsAsync(scope.ServiceProvider, response, cancellationToken);

        if (acknowledged.Count > 0)
        {
            try
            {
                using var ackResponse = await client.PostAsJsonAsync(
                    "api/edge/outbox/ack", new EdgeOutboxAckRequest(acknowledged), cancellationToken);
                ackResponse.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ack 沒送到不會掉資料：這批下次 poll 會再回來，落地端靠 WebhookEventId
                // 唯一索引去重。這裡照樣算一次失敗，讓退避生效
                RecordFailure(ex);
                return true;
            }
        }

        RecordSuccess();
        return true;
    }

    /// <summary>Edge 的暫存滿了、這輪沒把派工全收下時出個聲。沒收下的會留在 Pending
    /// 下一輪自動重派（派工本身不認領，認領在 CompleteAsync），所以這不是錯誤，
    /// 但持續發生就是「媒體一直下不完」的線索，不能靜默。</summary>
    private void WarnIfBackpressured(IReadOnlyList<ContentWorkItem> dispatch, EdgePollResponse response)
    {
        var rejected = dispatch.Count - response.AcceptedContentWork.Count;
        if (rejected <= 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (_lastBackpressureWarningAt is { } last && now - last < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastBackpressureWarningAt = now;
        logger.LogWarning(
            "Edge 的媒體暫存區已滿，這輪有 {Rejected} 筆派工沒被收下（會留著下一輪重派）。" +
            "持續發生代表媒體下載跟不上，可考慮調高 Ingest:PullStagingMaxBytes。", rejected);
    }

    /// <summary>組出這一輪要派給 Edge 的媒體工作。沿用 Core 端既有的 DbContentWorkSource：
    /// 認領、租約回收、重試次數全部維持原本那一套，這裡只是把「誰去下載」換成 Edge。</summary>
    private async Task<IReadOnlyList<ContentWorkItem>> BuildDispatchAsync(
        IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var workSource = scopedProvider.GetRequiredService<IContentWorkSource>();
        var ownerId = scopedProvider.GetRequiredService<ProcessOwnerId>().Value;

        var candidateIds = new HashSet<long>();
        lock (_freshContentIds)
        {
            // 剛落地的立刻派，比照推送模式「落地即入列」——不必等下一次全表掃描
            candidateIds.UnionWith(_freshContentIds);
            _freshContentIds.Clear();
        }

        // 全表掃描有副作用（回收逾期認領、把可重試的 Failed 重設成 Pending），每秒做一次
        // 會在幾秒內燒完 ContentDownload:MaxFailedRetries——推送模式下這個消耗節奏是
        // RequeueIntervalMinutes（分鐘級），這裡必須比照
        var now = timeProvider.GetUtcNow();
        var scanInterval = TimeSpan.FromMinutes(Math.Max(1, contentOptions.Value.RequeueIntervalMinutes));
        if (_lastFullScanAt is not { } lastScan || now - lastScan >= scanInterval)
        {
            _lastFullScanAt = now;
            candidateIds.UnionWith(await workSource.GetPendingIdsAsync(
                reclaimDownloading: true, startupAge: null, ownerId, cancellationToken));
        }

        var dispatch = new List<ContentWorkItem>();
        foreach (var id in candidateIds)
        {
            // 已經在取回中的不重派——那筆的內容 Edge 已經下載好了，正在傳回來的路上
            lock (_inFlightContent)
            {
                if (_inFlightContent.Contains(id))
                {
                    continue;
                }
            }

            if (await workSource.GetAsync(id, cancellationToken) is { } item)
            {
                dispatch.Add(item);
            }
        }

        return dispatch;
    }

    /// <summary>處理 Edge 回報的媒體結果：完成的排進取回佇列（由 RunContentLoopAsync 慢慢拿，
    /// 不卡住每秒一次的 poll）、失敗的走既有重試狀態機。</summary>
    private async Task HandleContentAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (response.FailedContentIds.Count > 0)
        {
            var workSource = scopedProvider.GetRequiredService<IContentWorkSource>();
            var ownerId = scopedProvider.GetRequiredService<ProcessOwnerId>().Value;
            foreach (var id in response.FailedContentIds)
            {
                // 不疊第二套死信：交回既有的 MaxRetries／Failed 狀態機處理
                await workSource.FailAsync(id, ownerId, cancellationToken);
            }
        }

        foreach (var id in response.ReadyContentIds)
        {
            lock (_inFlightContent)
            {
                // 已經在取回中（或已排隊）的不重複入列——一份大檔傳到一半時，接下來每一輪
                // poll 都還會把它列在 ReadyContentIds 裡
                if (!_inFlightContent.Add(id))
                {
                    continue;
                }
            }

            _contentQueue.Enqueue(id);
        }
    }

    private async Task FetchAndLandContentAsync(
        IContentWorkSource workSource, long contentId, string ownerId, CancellationToken cancellationToken)
    {
        var contentClient = httpClientFactory.CreateClient(ContentHttpClientName);

        using var httpResponse = await contentClient.GetAsync(
            $"api/edge/content/{contentId}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var declaredLength = httpResponse.Content.Headers.ContentLength
            ?? throw new InvalidOperationException($"Edge 取回內容 {contentId} 沒有帶 Content-Length，無法驗證完整性。");

        var bytes = await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.LongLength != declaredLength)
        {
            // 傳輸被截斷：這次不落地也不 ack，下一輪整份重取
            throw new InvalidOperationException(
                $"Edge 取回內容 {contentId} 不完整（宣告 {declaredLength} 位元組、實收 {bytes.LongLength} 位元組）。");
        }

        using var buffer = new MemoryStream(bytes, writable: false);
        await workSource.CompleteAsync(
            contentId, buffer, bytes.LongLength,
            httpResponse.Content.Headers.ContentType?.ToString(), ownerId, cancellationToken);

        // 完整落地之後才 ack，Edge 這時才釋放記憶體暫存
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var ackResponse = await client.PostAsync(
            $"api/edge/content/{contentId}/ack", content: null, cancellationToken);
        ackResponse.EnsureSuccessStatusCode();
    }

    /// <summary>媒體派工在組請求時就從 _freshContentIds 取走了，沒送出去要放回去，否則得等
    /// 下一次全表掃描（最長 RequeueIntervalMinutes）才被撿回，「落地即派」的意圖就斷了。
    ///
    /// 名稱／頭貼派工不需要對應處理：那份待辦派出後本來就留著（見 _pendingProfileWork），
    /// 這次沒送到的話下一個重派間隔會自然再送一次。</summary>
    private void RestoreDispatch(IReadOnlyList<ContentWorkItem> dispatch)
    {
        if (dispatch.Count == 0)
        {
            return;
        }

        lock (_freshContentIds)
        {
            foreach (var item in dispatch)
            {
                _freshContentIds.Add(item.ContentId);
            }
        }
    }

    /// <summary>把累積的刷新對象轉成派工：TTL 判斷在這台做（Core 才有資料庫），
    /// 只有真的過期的才派出去，不讓 Edge 白打 LINE API。</summary>
    private async Task<IReadOnlyList<EdgeProfileWorkItem>> BuildProfileDispatchAsync(
        IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        (string GroupId, string? UserId)[] targets;
        lock (_pendingProfileWork)
        {
            if (_pendingProfileWork.Count == 0)
            {
                return [];
            }

            // 只挑「從沒派過」或「距上次派出已滿一個重派間隔」的——其餘連 staleness 都不查，
            // 待辦保留後存活期變長，每秒每筆查一次資料庫會壓垮每秒一次的輪詢節奏
            targets = [.. _pendingProfileWork
                .Where(pair => now - pair.Value >= ProfileRedispatchInterval)
                .Select(pair => pair.Key)];
        }

        if (targets.Length == 0)
        {
            return [];
        }

        var profileStore = scopedProvider.GetRequiredService<IProfileStore>();
        var cacheOptions = scopedProvider.GetRequiredService<IOptions<ProfileCacheOptions>>().Value;
        var cutoff = now - cacheOptions.RefreshAfter;

        var dispatch = new List<EdgeProfileWorkItem>();
        var settled = new List<(string GroupId, string? UserId)>();
        foreach (var (groupId, userId) in targets)
        {
            var staleness = await profileStore.GetStalenessAsync(groupId, userId, cutoff, cancellationToken);
            if (staleness.GroupStale || staleness.MemberStale)
            {
                dispatch.Add(new EdgeProfileWorkItem(groupId, userId, staleness));
            }
            else
            {
                // 不過期＝Edge 回報的結果已經落地（或本來就新鮮），這筆到此為止
                settled.Add((groupId, userId));
            }
        }

        lock (_pendingProfileWork)
        {
            foreach (var key in settled)
            {
                _pendingProfileWork.Remove(key);
            }

            // 派出當下就記時戳。poll 請求本身失敗不回滾——那代表 Core→Edge 也不通，
            // 這筆 30 秒後自然重派，多等一輪無關緊要
            foreach (var item in dispatch)
            {
                _pendingProfileWork[(item.GroupId, item.UserId)] = now;
            }
        }

        return dispatch;
    }

    /// <summary>把 Edge 打回來的名稱／頭貼結果落地。失敗不重試也不回報：頭貼是非關鍵資料，
    /// 那筆會因為 TTL 仍然過期而在之後的輪次被重新派工（與推送模式的處置一致）。</summary>
    private async Task HandleProfileResultsAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (response.ProfileResults.Count == 0)
        {
            return;
        }

        var profileStore = scopedProvider.GetRequiredService<IProfileStore>();
        foreach (var result in response.ProfileResults)
        {
            try
            {
                if (result.Group is { } group)
                {
                    await profileStore.UpsertGroupAsync(result.GroupId, group, cancellationToken);
                }
                if (result.Member is { } member && result.UserId is { } userId)
                {
                    await profileStore.UpsertMemberAsync(result.GroupId, userId, member, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "落地 Edge 回報的名稱／頭貼失敗（群組 {GroupId}、成員 {UserId}），等 TTL 再次過期時重新刷新。",
                    result.GroupId, result.UserId);
            }
        }
    }

    private async Task WriteHeartbeatAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (!HeartbeatIdentity.IsValid(response.Role, response.MachineName))
        {
            logger.LogWarning(
                "Edge 回報的心跳身分不合法（Role={Role}、MachineName 長度={Length}），略過這次心跳寫入。",
                response.Role, response.MachineName?.Length ?? 0);
            return;
        }

        var heartbeatStore = scopedProvider.GetRequiredService<IHeartbeatStore>();

        // 指紋固定 null：Edge 不碰加密金鑰，不能拿本機（Core）的指紋去填 Edge 那列——
        // 與 IngestController 代寫推送心跳的處置一致
        await heartbeatStore.UpsertAsync(
            response.Role, response.MachineName,
            new HeartbeatReport(response.OutboxPending, response.OutboxOldestAgeSeconds),
            encryptionKeyFingerprint: null, channel: HeartbeatChannel.Pull, cancellationToken);
    }

    private async Task<List<string>> LandMessagesAsync(
        IServiceProvider scopedProvider, EdgePollResponse response, CancellationToken cancellationToken)
    {
        if (response.Messages.Count == 0)
        {
            return [];
        }

        var envelopes = new List<IngestEnvelope>(response.Messages.Count);
        foreach (var item in response.Messages)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<IngestEnvelope>(item.PayloadJson)
                    ?? throw new InvalidOperationException("payload 反序列化為 null。");
                envelopes.Add(envelope);
            }
            catch (Exception ex)
            {
                // 壞掉的 payload 不 ack：留在 Edge 的 outbox 讓人工判斷，這裡重試也不會變好。
                // 不 ack 代表它每一輪都會再出現，所以告警要節流，否則以每秒一則的速度刷爆 log
                var now = timeProvider.GetUtcNow();
                if (_lastBadPayloadWarningAt is not { } last || now - last >= TimeSpan.FromMinutes(10))
                {
                    _lastBadPayloadWarningAt = now;
                    logger.LogError(ex,
                        "Edge 送來的 outbox 項目 {WebhookEventId} 無法反序列化，略過且不 ack；" +
                        "它會在每一輪重新出現，這則告警每 10 分鐘最多記一次。", item.WebhookEventId);
                }
            }
        }

        if (envelopes.Count == 0)
        {
            return [];
        }

        var sink = scopedProvider.GetRequiredService<IIngestSink>();
        var downloadQueue = scopedProvider.GetRequiredService<IContentDownloadQueue>();
        var profileRefreshQueue = scopedProvider.GetRequiredService<IProfileRefreshQueue>();

        var results = await sink.SubmitBatchAsync(envelopes, cancellationToken);

        var envelopesByWebhookEventId = envelopes
            .GroupBy(e => e.WebhookEventId)
            .ToDictionary(g => g.Key, g => g.First());

        var acknowledged = new List<string>(results.Count);
        foreach (var item in results)
        {
            // 永久拒絕的也要 ack：Core 這邊已經判定重試不會變好，留在 Edge 只會無限重送
            if (!item.PermanentlyRejected && envelopesByWebhookEventId.TryGetValue(item.WebhookEventId, out var envelope))
            {
                IngestSideEffects.Apply(
                    envelope, new IngestResult(item.ContentId), downloadQueue, profileRefreshQueue);
            }

            acknowledged.Add(item.WebhookEventId);
        }

        // 記下這批訊息牽涉到的群組與成員，下一輪 poll 時由 Core 判斷過期與否再派給 Edge 刷新——
        // 拉取模式下 Edge 沒有資料庫，TTL 判斷只能在這台做（見 EdgeProfileStaging）
        lock (_pendingProfileWork)
        {
            foreach (var envelope in envelopes)
            {
                // MinValue＝還沒派過，下一輪 poll 立刻派。已經在待辦裡的不重設時戳，
                // 否則同一個群組連發訊息會把重派間隔一直往後推
                _pendingProfileWork.TryAdd((envelope.GroupId, envelope.UserId), DateTimeOffset.MinValue);
            }
        }

        // 帶媒體的訊息剛落地就記下來，下一輪直接派給 Edge 下載，不必等節流過的全表掃描
        lock (_freshContentIds)
        {
            foreach (var item in results)
            {
                if (item.ContentId is { } contentId)
                {
                    _freshContentIds.Add(contentId);
                }
            }
        }

        return acknowledged;
    }

    private void RecordSuccess()
    {
        if (_consecutiveFailures > 0)
        {
            logger.LogInformation("輪詢 {EdgeBaseUrl} 已恢復正常。", _options.EdgeBaseUrl);
        }

        _consecutiveFailures = 0;
        _lastFailureSummaryAt = null;
    }

    private void RecordFailure(Exception ex)
    {
        _consecutiveFailures++;

        // 1 秒一次的失敗如果每次都記，log 一天會多出 8 萬行——只在「從正常轉為失敗」記一次，
        // 持續失敗期間每 10 分鐘補一則摘要
        var now = timeProvider.GetUtcNow();
        if (_consecutiveFailures == 1)
        {
            _lastFailureSummaryAt = now;
            logger.LogWarning(ex,
                "輪詢 {EdgeBaseUrl} 失敗，改為退避重試（上限 {MaxSeconds} 秒）。",
                _options.EdgeBaseUrl, _options.PullFailureMaxBackoffSeconds);
            return;
        }

        if (_lastFailureSummaryAt is { } last && now - last >= TimeSpan.FromMinutes(10))
        {
            _lastFailureSummaryAt = now;
            logger.LogWarning(
                "輪詢 {EdgeBaseUrl} 仍然失敗，已連續 {Count} 次。最後一次的原因：{Reason}",
                _options.EdgeBaseUrl, _consecutiveFailures, ex.Message);
        }
    }
}
