# EDGEOPS-3 第 1 輪規劃：單向拓撲（僅 Core→Edge）快速收斂＋診斷誤導修正

> 狀態：實作完成，待體檢
> 基準：dev@2656b3c（1257 綠）→ feature/edgeops-3@c736b86（1278 綠）
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

## 實作結果

| 批次 | commit | 實作方 | 測試數 |
|---|---|---|---|
| A+B | `b1c07aa` | Claude | 1257 → 1264 |
| C | `0e56cb6` | Claude | 1264 → 1268 |
| E | `4d2804c` | Claude | （含於上）|
| D+F | `c736b86` | agy（Claude 驗收＋修正） | 1268 → 1278 |

### 與規劃的落差

1. **批次F 第 4 點的規格寫錯，驗收時抓到並自行修正**（最重要的一項）。
   規劃寫「把 `WithHost` 在 host 為 null 時代入的值改成『對方 』」，但 `WithHost` 有兩類呼叫端：
   - **連線層**（DNS／路由不可達／連線被拒／逾時）：模板沒有主詞，確實需要補——這是原意。
   - **狀態碼類**：模板本身已帶主詞（`HTTP {code}：{0}對端伺服器錯誤`），補了會變成
     「對方 對端伺服器錯誤」語意重複；更嚴重的是 `var other` 那條（4xx／5xx 以外的狀態碼）
     傳進 `WithHost` 的是**預先格式化好的「：host」字串而不是 host**，null 時會產出
     `HTTP 302對方 ` 這種壞字串。agy 照規格做，且沒有測試涵蓋該分支，所以沒被擋下。
   修正：`WithHost` 還原為原本的空字串行為，另加 `WithCounterpart` 只給四條連線層分支使用；
   agy 對狀態碼類測試的斷言一併還原，並新增
   `Classify_UnusualStatusCode_WithoutHost_HasNoStraySubject` 鎖住這條回歸。
2. **批次B 順帶矯正測試的時間紀律**（規劃只寫了正式碼要注入 `TimeProvider`）：
   `ProfileRefreshServiceTests` 原本用 `Task.Delay(100)` 配 20ms 冷卻測到期，正是 CLAUDE.md
   說的「時間長度永遠測不到」形狀。改用假時鐘推進後，順便補上「29ms 不重試、+2ms 後才重試」
   這類真正驗到長度的斷言，五個測試從依賴真實睡眠變成確定性。
3. **批次E 多修一處**：`ProcessBatchAsync` 內還有第二個 `SaveChangesAsync`（壞掉 payload 的
   死信路徑），同樣會撞上 Core 的 ack 競態，一併套用了保護。

### 驗收過程

- **突變測試共 10 次，全數紅燈**：拿掉 `MarkPushFailed`、內部冷卻改回 10 分鐘、目標改回 LINE、
  派出後清空待辦、拿掉 30 秒節流、不移除 settled、改回裸 `SaveChanges`、
  入列 `Accepted` 而非 `NewlyAccepted`、移除逾時 catch、`LogWarning` 改回 `LogError`。
- **委派驗收**：agy 這段的檔案白名單、BOM（與基準 `2656b3c` 逐檔比對皆未變動）、NUL 掃描、
  正式碼可選參數 fallback（零命中）全部通過，沒有出現「宣稱完成但沒做」或「既有全綠冒充有測試」。
  唯一的問題是上面第 1 點——**成因是規格寫錯，不是 agy 沒照做**。
- 全套 1278 綠（基準 1257，+21）。

## 體檢交接

- **實作模型**：claude-opus-5（批次 A／B／C／E 自己做；批次 D／F 委派 agy `gemini-3.7-flash-high`，由 Claude 驗收並修正）。
- **交接時狀態**：`feature/edgeops-3@c736b86`，`dotnet test` 全量 **1278 綠、0 失敗**，工作樹乾淨，未併 dev。
- **體檢對象**：`dev..feature/edgeops-3`（5 個 commit）。

### 實作方最沒把握的地方（體檢請優先看，但不要只看這幾處）

1. **批次C 的收斂性**。保留待辦後，某個目標若始終無法刷新成功（例如 LINE 對該群組永久回錯），
   它會每 30 秒被重派一次直到 Core 端 staleness 判定不過期為止。理論上 Edge 端打 LINE 失敗
   會吃 10 分鐘冷卻、O(1) 丟棄，成本很低；但**沒有上限保護**，也沒有「重派 N 次後放棄」的機制。
   請確認這在「某群組永久刷不起來」的情境下不會變成長期的固定負載。
2. **批次A 推翻了一條有明確理由的舊定案**（「心跳偶發失敗不該把轉發拖入暫停」）。我的依據是
   180 秒寬限期足以擋住偶發失敗，且與成功側對稱。但這條舊定案當初的完整脈絡我沒有回去翻
   `docs/history`，若體檢方認為值得，可查 BIDIR-1／OBS-1 那幾輪的紀錄再確認一次。
3. **批次E 的 detach 後重存只重試一次**。第二次仍衝突就往外拋、由既有 ERROR 接住。
   我判斷第二次衝突機率極低（第一次已把被搶走的移出追蹤），但這是判斷不是證明。
4. **`EdgeControllerTests` 是直接呼叫 controller action 方法**，沒有走真實 HTTP 管線，
   所以路由與 `[RequiresCapability]` 閘門不在這幾個測試的涵蓋範圍（那些另有既有測試）。
5. **整條鏈路沒有端到端測試**。A～F 各自有單元測試，但「訊息落地 → Core 派工 → Edge 刷新 →
   結果回傳 → staleness 轉新鮮 → 停止重派」這條完整迴路只在規劃階段以程式碼推導驗證過，
   沒有一個測試把它串起來跑。實機驗證時這是最該看的。

### 實作方已知但刻意沒做的

- 沒有新訊息的舊群組不會被主動刷新（既有的以訊息觸發設計，已列在 PLAN 的「已知限制」）。
- `EdgeSettingsHotReloadTests` 的偶發失敗（既有問題，另有任務卡）——本輪全套第一次跑曾撞到一次，
  重跑即綠，與本輪改動無關。
