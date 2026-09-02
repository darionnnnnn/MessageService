# EDGEOPS-3 第 1 輪規劃：單向拓撲（僅 Core→Edge）快速收斂＋診斷誤導修正

> 狀態：規劃中
> 基準：dev@2656b3c（1257 綠）
> 來源：EDGEOPS-2 實測 log 分析——Edge→Core 防火牆未開通的環境下，名稱／頭貼／照片長時間出不來；另含實測確認的三個 bug（目標誤標、測試逾時誤報、outbox ack 競態）。

## 目標（使用者定案）

1. **先不管 Edge→Core 是否開通**：只有 Core→Edge 通的單向拓撲也要正常運行，名稱／頭貼／照片不要長時間等待才出現。
2. **開通後要正確切換回推送**（心跳成功 → `MarkPushSucceeded` 的秒級升級路已存在，本輪補上對稱的失敗側）。

## 實測確認的因果鏈（現況）

- 切換到拉取資源需要 `MarkPushFailed` 持續 180 秒，但它**只有 outbox 批次失敗會呼叫**——安靜期（無訊息流量）永遠不切換，名稱／頭貼查詢一直打不通的 Core、一直吃 10 分鐘冷卻（`ProfileCacheOptions.FailureRetryAfter`）。
- Core 派名稱／頭貼工作時即清空待辦（`EdgePullService.BuildProfileDispatchAsync`），Edge 冷卻中丟棄＝永久丟失，要等該群組新訊息才重新排入。
- `EdgeController.Poll` 收到 ProfileWork 會立即入列，**ContentWork 只進暫存區不入列**，照片要等 `ContentDownload:RequeueIntervalMinutes`（15 分）週期重掃。
- 三個 bug：見批次 B／E／F 的現況節。

## 兩項機制定案（使用者已確認）

1. **心跳連續失敗計入通道切換**——推翻「心跳偶發失敗不該把轉發拖入暫停」的舊定案，理由：`EdgeChannelState` 已有 180 秒寬限期（心跳 60 秒一次，連續失敗 3 次以上才暫停），偶發失敗不受影響；改後安靜期約 3 分鐘內自動切換，且與「心跳成功→恢復推送」對稱。
2. **Core 保留待辦＋節流重派**——派出後不清待辦，staleness 判定不過期（＝結果已落地或本來就新鮮）才移除；每目標最短 30 秒重派一次。搭配 Edge 端內部通道失敗冷卻縮到 30 秒（LINE API 失敗維持 10 分鐘）。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 實作方 |
|---|---|---|---|---|
| A | 心跳失敗計入通道狀態（HttpHeartbeatReporter） | 小（1 檔） | 無 | Claude |
| B | ProfileRefreshService：目標誤標修正＋抑制表雙軌化（內部失敗 30 秒） | 中（1 檔＋測試） | 無 | Claude |
| C | EdgePullService：保留待辦＋30 秒節流重派 | 中（1 檔＋測試） | 無 | Claude |
| D | Poll 的 ContentWork 立即入列 | 小（2 檔） | 無 | agy |
| E | outbox ack 刪除競態：偵測並降噪 | 中（1 檔＋測試） | 無 | Claude |
| F | 連線測試逾時誤報＋WithHost 主詞＋啟動 requeue log | 小（3 檔） | 無 | agy |

批次間無相依，可任意順序；建議 A→B→C（主線）先做，D／F 平行委派，E 收尾。
**agy 委派紀律**：agy 執行期間工作樹不得有未 commit 改動（EDGEOPS-2 踩過：並行手改被覆蓋）。

## 批次A：心跳失敗計入通道狀態

### 現況與核對結果

- `MarkPushFailed` 唯一呼叫點：`OutboxForwarderService.cs:261`。
- `HttpHeartbeatReporter`（`MessageService.Web/Services/HttpHeartbeatReporter.cs`）已相依 `EdgeChannelState`，成功側呼叫 `MarkPushSucceeded()`（28 行），失敗側註解明寫「刻意不通知」。它只在 Edge 模式註冊，掛這裡不需要動通用的 `HeartbeatService` 相依（`EdgeChannelState` 不是所有模式都有註冊）。
- `EdgeChannelState.MarkPushFailed` 內建 180 秒寬限（`_pauseAfter`＝`PullActivationSeconds`），非 Edge 模式為 no-op。

### 定案補充

- **任何**心跳發送失敗都計入（含 4xx，例如 API key 錯誤）：語意是「推送通道未確認可用」，成功即清除。不對例外分類做細分。

### 改動

1. `HttpHeartbeatReporter.ReportAsync`：發送段包 try/catch，失敗時 `channelState.MarkPushFailed()` 後**原例外重拋**（`HeartbeatService` 的告警節流行為不變）。成功路徑不動。
2. 更新該檔與 `EdgeChannelState` 的相關註解（移除「失敗側刻意不通知」的舊理由，改寫為現行為與 180 秒寬限的關係）。

### 測試／驗收

- 新測試：reporter 失敗 → `MarkPushFailed` 被呼叫且例外照拋；成功 → `MarkPushSucceeded`（既有測試應已涵蓋，確認）。
- 整合行為（配合既有 `EdgeChannelState` 測試）：連續失敗跨過 `PullActivationSeconds` 後 `UsePullResources` 為 true。

## 批次B：ProfileRefreshService 目標誤標＋抑制表雙軌化

### 現況與核對結果

- `ProfileRefreshService.cs:68-88`：`targetHost` 在方法開頭固定推導為 LINE 方向（`api.line.me`／proxy），但 `GetStalenessAsync` 的 catch（81-88 行）打的是**內部通道**（Edge 模式 `ApiProfileStore` → `Ingest:BaseUrl`；AllInOne `DbProfileStore` → 本機資料庫；拉取模式 `StagingProfileStore` → 記憶體，不會失敗）。實測 log 已出現「連線逾時：api.line.me」實際打 `10.216.68.26` 的誤導訊息。
- 失敗冷卻與成功抑制共用同一張 `_suppressUntil` 表；staleness 查詢失敗套 `FailureRetryAfter`（10 分鐘），在通道切換前的過渡期把後續派工全數毒化。
- 該服務目前直接用 `DateTimeOffset.UtcNow`（既有違反 CLAUDE.md 的 TimeProvider 紀律，本輪動到抑制邏輯，一併矯正）。

### 定案

- staleness 查詢失敗＝**內部通道問題**：冷卻 30 秒（新常數 `InternalFailureRetryAfter`），錯誤訊息的目標改為內部通道描述（比照批次 D 於 EDGEOPS-2 的 `HeartbeatService` 模式：`Ingest:BaseUrl` 有值 → 該 URL；無值 → 「本機資料庫」）。
- 打 LINE API 失敗維持 `FailureRetryAfter`（10 分鐘）不變——LINE 方向的問題與內部通道無關。
- 抑制表不拆成兩張：仍是同一張 `_suppressUntil`，只是失敗來源決定冷卻長度（30 秒 vs 10 分鐘），行為單純、鍵不變。

### 改動

1. 建構子注入 `TimeProvider`（必要相依），檔內所有 `DateTimeOffset.UtcNow` 改 `timeProvider.GetUtcNow()`。
2. 建構子注入 `IOptions<IngestOptions>`，算出內部通道目標描述（唯讀欄位，比照 `HeartbeatService`）。
3. staleness 的 catch：`Classify(ex, <內部通道 host>)`＋訊息模板帶目標；`RecordFailure` 這條路徑改用 `InternalFailureRetryAfter`（30 秒）。
4. LINE API 失敗路徑（`RecordFailure` 於 `RefreshGroupAsync`／member 路徑）維持 10 分鐘。

### 測試／驗收

- staleness 失敗：log 訊息含內部通道目標（不含 `api.line.me`）；冷卻 30 秒後同鍵 task 會再處理（`FakeTimeProvider` 推進 31 秒驗證）、29 秒內不處理。
- LINE API 失敗：冷卻仍為 10 分鐘。
- 既有測試更新建構子後全綠。

## 批次C：Core 保留待辦＋節流重派

### 現況與核對結果

- `EdgePullService.cs:57` `_pendingProfileWork` 是 `HashSet<(GroupId, UserId)>`；`BuildProfileDispatchAsync`（448-460 行）取出即 `Clear()`；只有 poll 請求本身失敗才 `RestoreDispatch`（418-443 行）還原。
- staleness 判定不過期的目標被自然移除（正確——結果已落地或本就新鮮）；stale 且派出的目標清掉後就沒有任何重派來源。
- `HandleProfileResultsAsync`（481 行）落地 Edge 回報的結果——落地成功後該目標下一輪 staleness 自然變不過期。

### 定案

- `_pendingProfileWork` 改為 `Dictionary<(string GroupId, string? UserId), DateTimeOffset LastDispatchedAt>`。
- 每輪 poll 前逐目標：距上次派出不足 **30 秒** → 跳過（不查 staleness、不派）；已達 30 秒 → 查 staleness，**不過期 → 移除**（結果已落地），過期 → 派出並更新 `LastDispatchedAt`。
- staleness 查詢跟著 30 秒節流——保留待辦後 pending 存活期變長，不能每秒每目標查一次資料庫。
- 時戳在**派出當下**更新；poll 請求失敗不回滾時戳——該目標 30 秒後自然重派，poll 失敗代表 Core→Edge 也不通，30 秒延遲無關緊要。`RestoreDispatch` 的 profile 部分因此整段刪除（content 部分不動）。
- 收斂上限：LINE 失敗（Edge 端 10 分冷卻）情境下，同目標最多每 30 秒被派一次、Edge 端 O(1) 丟棄，直到 Edge 冷卻結束處理成功、結果落地、staleness 轉不過期後自動停止。無新增設定項。

### 改動

1. `_pendingProfileWork` 型別與上述迴圈邏輯；新增常數 `ProfileRedispatchInterval = TimeSpan.FromSeconds(30)`。
2. 時間來源用該服務既有的 `TimeProvider`（實作時確認；若目前用 `DateTimeOffset.UtcNow`，比照批次 B 矯正該檔）。
3. 累積端（595-601 行，訊息落地時 `Add`）改為「不存在才加入，`LastDispatchedAt` 初始為最小值（立即可派）」；已存在的不重設時戳。

### 測試／驗收

- 派出後目標仍在待辦；30 秒內不重派、30 秒後 staleness 仍 stale 會重派（`FakeTimeProvider`）。
- staleness 轉不過期 → 目標移除，之後不再查、不再派。
- poll 失敗 → 待辦不遺失（原 `RestoreDispatch` 的 profile 測試改寫）。
- 30 秒內同目標不重複查 staleness（以替身計數）。

## 批次D：Poll 的 ContentWork 立即入列

### 現況與核對結果

- `EdgeController.Poll`（52-66 行）：ProfileWork 逐筆 `profileRefreshQueue.Enqueue`；ContentWork 只 `staging.AcceptDispatch`，無入列——依賴 `ContentDownloadService` 每 15 分鐘（`RequeueIntervalMinutes`）重掃才撿到，照片延遲最長 15 分鐘。
- `EdgeContentStaging.AcceptDispatch`（51-75 行）回傳的 `accepted` 含「先前已派過」的重複項（58-62 行）；只有新收下的（70-71 行）才需要入列，重複入列雖有 claim 租約保護但屬無謂工作。
- `IContentDownloadQueue` 已註冊、`EdgePullService.cs:571` 有同型別的入列先例。

### 改動

1. `EdgeContentStaging.AcceptDispatch` 回傳型別改為 `(IReadOnlyList<long> Accepted, IReadOnlyList<long> NewlyAccepted)`（或等價 record）：`Accepted` 維持原語意（回給 Core），`NewlyAccepted` 只含這次新寫進 `_dispatched` 的。
2. `EdgeController` 建構子加必要相依 `IContentDownloadQueue`，`Poll` 對 `NewlyAccepted` 逐筆 `Enqueue(contentId)`。
3. 呼叫點與測試替身全數跟上新回傳型別（不得加相容多載）。

### 測試／驗收

- 新測試：Poll 收到 ContentWork → 新項目被 `Enqueue`（替身計數），重複派的不重複入列，暫存滿被拒收的不入列。
- `AcceptDispatch` 單元測試更新：兩個清單的語意各自驗證。
- 既有 15 分鐘重掃路徑不動（仍是兜底）。

## 批次E：outbox ack 刪除競態降噪

### 現況與核對結果

- Edge 推送失敗寫退避（`OutboxForwarderService.ProcessBatchAsync` 尾端 `SaveChangesAsync`，274 行）與 Core 輪詢後的 `outbox/ack`（`EdgeController.cs:96` `ExecuteDeleteAsync`，不同 DbContext）競態：同一筆同時「被更新」與「被刪除」→ `DbUpdateConcurrencyException` → 整批 rollback、ERROR 級完整堆疊（實測已出現）。
- 兩個副作用：同批其他項目的退避更新一起 rollback（下輪立即重跑，違反該檔自己註解警告的「無退避熱迴圈」）；ERROR 噪音擠壓 200 筆環形緩衝。
- 語意上這不是錯誤：項目被 ack 刪除＝Core 已收到，是單向拓撲的**正常路徑**。

### 改動

1. `ProcessBatchAsync` 尾端的 `SaveChangesAsync` 包 catch `DbUpdateConcurrencyException`：把 `ex.Entries` 中的衝突項逐筆 `entry.State = EntityState.Detached`（被刪＝已送達，放手），再 `SaveChangesAsync` 重存一次（只重試一次，第二次仍拋就往外讓既有 ERROR catch 接住）。
2. 競態發生時記 **Information**：「{Count} 筆 outbox 項目已被 Core 輪詢取走，略過本地更新」。

### 測試／驗收

- 新測試：模擬「儲存前項目被另一個 context 刪除」→ 不拋例外、其餘項目的退避更新成功保存、記 Information。
- 既有 outbox 測試全綠。

## 批次F：診斷文字三項修正

### 現況與核對結果

1. `LineConnectivityTester.TestTargetAsync` 的 10 秒逾時（`cts.CancelAfter`）觸發時，`TaskCanceledException` 無內層 `TimeoutException`，被 `OutboundFailureClassifier` 分類成「請求已取消（呼叫端中斷，不是連線問題）」——與事實相反（實測貼圖列已出現，該次實為 CDN 慢回應）。
2. `OutboundFailureClassifier.WithHost`（113-114 行）host 為 null 時以空字串代入，產生「連線逾時：沒有回應」這種缺主詞句（實測心跳 log 已出現）。
3. `ContentDownloadService.cs:44-46` 啟動 requeue 失敗記 **ERROR** 且無目標資訊——實際是「啟動時內部通道暫時不通」，且 15 分鐘週期重掃會自動補救。

### 改動

1. `TestTargetAsync` 在既有的 `catch (Exception ex)` **之前**加一個
   `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` →
   Description 直接給「連線逾時：{target} 在時限內沒有回應（防火牆很可能未開通）」，不進 `Classify`。
   判斷依據：外部 token 沒被取消卻收到取消例外，必然是內部逾時（10 秒 `CancelAfter` 或具名
   client 自身的 Timeout，兩者語意相同）。**不要**在 catch 裡讀 `cts`——它以 `using var` 宣告在
   try 區塊內，catch 的作用域看不到（規劃復核抓到的錯誤，原寫法無法編譯）。呼叫端真的中斷
   （`cancellationToken` 已取消）時例外照舊進一般 catch 維持原分類。訊息不寫死「10 秒」。
2. `WithHost`：host 為 null 時以「對方」代入（「連線逾時：對方沒有回應…」），有 host 行為不變。
3. `ContentDownloadService` 啟動 requeue 失敗：ERROR → **Warning**，訊息加「{RequeueIntervalMinutes} 分鐘後的週期重掃會自動再試」；不加目標描述（`IContentWorkSource` 抽象下目標可為資料庫或 API，加相依不值得——`Classify(ex)` 的分類文字已能區分連線類失敗）。

### 測試／驗收

- 逾時測試：handler 直接丟 `TaskCanceledException`（模擬逾時形狀，不必真等 10 秒、不必加可注入逾時）→ Description 含「連線逾時」與 target、不含「呼叫端中斷」；外部 token 已取消的案例 → 維持原分類。
- `WithHost(null)` 三個模板的輸出含「對方」。
- requeue 失敗記 Warning 且訊息含週期重掃提示。

## 已知限制（既有設計，本輪不改，列此留痕）

- Core 的名稱／頭貼待辦以「訊息落地」觸發累積——**沒有新訊息的舊群組不會被排入刷新**，其名稱／頭貼要等該群組下一則訊息（或推送恢復後的正常流程）補上。
- 通道切換前的過渡窗口（約 180 秒）內派下的照片工作若下載嘗試失敗（打到不通的 Core），由 Edge 既有的 15 分鐘週期重掃兜底撿回；批次A 讓 Core 開始輪詢與 Edge 切換的時點大致對齊（同以心跳斷訊為訊號），此窗口實務上只剩秒級。

## 明確不做（本輪定案）

- 不改 poll 協定（不加 DeferredProfileWork／ack 欄位）——重派由 Core 端 staleness 判定驅動即可收斂。
- 不動 `EdgeChannelState` 的 180 秒寬限與 60 分探測週期參數。
- 升級回推送後 `EdgeProfileStaging._results` 可能殘留少量未取走的結果（Core 停輪詢後無人取；TTL 到期會重新刷新、重啟即清）——影響小，不做清理機制。
- `OutboundFailureClassifier` 對 401/403/404/429 不帶 host 的既有設計不動。
- `EdgeSettingsHotReloadTests` 既有偶發失敗（獨立任務卡處理，不進本輪）。
