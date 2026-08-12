# 收錄端部署模式

收錄端（`MessageService`）所在網段未必碰得到資料庫，因此支援三種部署角色，
由 `Deployment:Mode` 設定決定；同一份程式碼、同一個專案，角色只是設定差異，不是不同的部署產物。

> **目前進度（2026-08-12）**：Stage 0＋1＋2 已完成——`Full`／`Line`／`Db` 三種模式**都能真的
> 跑起來**，訊息收送的完整拆機拓撲已用兩個真實實體端到端驗證過（webhook 進 Line 端、
> 轉送到 Db 端、含斷線期間 outbox 累積＋恢復後自動排空且無重複，見「端到端驗證紀錄」）。
> 還沒做的是 Stage 3：媒體下載與頭貼快取目前仍只能在有資料庫存取的主機執行，`Line` 模式
> 拆機後這兩件事暫時做不了（見下方「目前限制」）——純文字訊息的收送已經是完整可用的形態。

## 三種模式

| | Full（預設） | Line | Db |
|---|---|---|---|
| `/api/line/webhook` | ✓ | ✓ | 404（路由不存在，非拒絕） |
| `/api/ingest/*` | ✓（需設 `Ingest:ApiKey` 才存在） | 404（路由不存在） | ✓（需設 `Ingest:ApiKey`） |
| 直連資料庫 | ✓ | ✗ | ✓ |
| 本機 outbox＋排空 | ✓ | ✓ | ✗（無 webhook，無事件可寫） |
| 落地方式 | outbox → `DirectIngestSink` | outbox → `HttpIngestSink` → 對方的 `/api/ingest/events` | `IngestController` → `DirectIngestSink` |
| 保留期清除 | ✓ | ✗ | ✓ |
| 內容下載 / 頭貼快取 | ✓ | 尚未支援（Stage 3） | ✓ |

`Full` 就是今天的行為：收 webhook、寫本機 outbox、由背景服務排空並直接寫進資料庫。
沒有設定 `Deployment:Mode` 就是這個模式，**既有部署完全不受影響**。

## 架構：outbox 是唯一的落地路徑

```
LINE ──▶ LineWebhookController ──▶ WebhookEventHandler
                                        │ 驗簽已在 controller 做完；這裡只解析/過濾成
                                        │ IngestEnvelope，不碰資料庫、不打網路
                                        ▼
                              outbox.db（本機 SQLite，跟主資料庫完全獨立）
                                        ▲ 立即回 200
                                        │
                              OutboxForwarderService（背景排空，失敗按退避重試，
                                        │              死信門檻 MaxAttempts 或永久性失敗）
                                        ▼
                                   IIngestSink
                          ┌─────────────┴─────────────┐
                    DirectIngestSink              HttpIngestSink
                    （Full／Db，EF 直寫）      （Line，POST /api/ingest/events）
                                                          │
                                                          ▼
                                          IngestIpAllowlistMiddleware
                                          ＋IngestApiKeyMiddleware（X-Ingest-Key）
                                                          ▼
                                                  IngestController
                                                          ▼
                                              （對方主機的）DirectIngestSink
```

webhook 回應時間因此跟資料庫或遠端 API 是否可用完全脫鉤：即使主資料庫短暫斷線，
`Full` 模式的訊息也只是在 outbox 裡等，恢復後自動排空，不會遺失
（`OutboxForwarderService.ProcessBatchAsync`／`MessageService.Tests/Outbox/`）。

## 設定

| 設定鍵 | 說明 |
|---|---|
| `Deployment:Mode` | `Full`（預設）／`Line`／`Db` |
| `ConnectionStrings:Outbox` | 本機 outbox 的 SQLite 檔（`Full`／`Line` 才用得到），預設 `Data Source=outbox.db` |
| `Line:OutboundHere` | 這台要不要對外呼叫 LINE API（媒體下載＋頭貼快取）。**Stage 1 尚未依這個設定做任何事**，先聲明形狀，實際生效要等 Stage 3 的 `IContentWorkSource` |
| `Ingest:BaseUrl` | `Line` 模式打去哪個 `Db` 模式主機的 ingest API（如 `https://db-host/`） |
| `Ingest:ApiKey` | 雙邊共用密鑰：`Line` 端當 `X-Ingest-Key` 標頭送出、`Db`／`Full` 端驗證進來的請求，兩邊必須一致。**留空時 `/api/ingest/*` 整個不存在（404）**——避免單機部署意外多開一個沒人保護的寫入端點 |
| `AllowedClientIps` | `/api/ingest/*` 的 IP 白名單（跟 `MessageService.Web` 的同名設定是獨立的兩份，各自只保護各自的端點），只在 `Full`／`Db` 模式生效；空白名單視為全拒 |
| `Outbox:PollIntervalSeconds` | outbox 空的時候的保底輪詢間隔（寫入會立刻叫醒，這只是撿回到期重試項目用），預設 5 |
| `Outbox:BatchSize` | 一輪最多處理幾筆，預設 50 |
| `Outbox:BaseRetryDelaySeconds` / `MaxRetryDelaySeconds` | 重試退避：第 N 次失敗延遲約 `Base × N`，封頂 `Max`，預設 5／300 |
| `Outbox:MaxAttempts` | 累計失敗達到這個次數就標記死信、不再重試（見下方「死信」），預設 20 |

## 設計決策

- **為什麼落在 `MessageService` 而不是新專案或 `MessageService.Data`**：最初規劃過
  「新增 `MessageService.Core`＋`MessageService.Ingest` 兩個專案」，但這會讓一個 25 個檔案的
  服務為了部署彈性長出兩個新專案。改成單一專案三種模式後，同一份程式碼在不同模式下跑，
  也天然消除「兩地邏輯漂移」的風險——這正是本專案在 `/Home/Settings` 退場時記取的教訓
  （見 [docs/WEB-UI-DESIGN-NOTES.md](WEB-UI-DESIGN-NOTES.md)）。放在 `MessageService.Data` 也不對：
  那個專案同時被唯讀的 `MessageService.Web` 參照，HTTP 推送邏輯放進去會讓 Web 平白拖進用不到的程式碼。
- **只有 `IIngestSink` 有兩套實作**：webhook 收進來的路徑永遠只有「寫 outbox」一種。
  若改成每個資料庫操作（訊息、內容下載、頭貼快取）各自抽一層，雙實作的維護量會多出四五處。
- **防重送整個交給落地端**：`GroupMessages.WebhookEventId` 本來就有唯一索引
  （`MessageDbContext`），`DbUpdateException` 攔截也早就存在。`WebhookEventHandler`
  因此完全不需要任何資料庫讀取路徑——這是「webhook 只寫本機、不碰網路」能夠成立的前提，
  不然還是得先查一次資料庫才能決定要不要收。
- **outbox 用本機 SQLite，不是共用資料庫**：跟主資料庫（`MessageDbContext`）完全獨立、
  無論 `Full`／`Db` 兩種模式都不共用，這是特意的——outbox 排空失敗不該卡住任何跟主資料庫
  有關的邏輯，反之亦然。
- **`DbUpdateException` 用回查分辨「重複」與「暫時性失敗」**：撞鍵（真重複）要當成功讓 outbox
  刪掉該筆，儲存中途斷線／逾時要往外拋讓 outbox 重試——兩者都以 `DbUpdateException` 現身，
  一律當重複吞掉就會在暫時性失敗時把訊息弄丟，直接違反 outbox 的核心承諾。`DirectIngestSink`
  不解析各 provider 的錯誤碼，直接回查資料庫確認該筆到底有沒有進去；回查前先
  `ChangeTracker.Clear()`，否則失敗實體會留在同一個 scope 的 context 上污染同批後續每一筆。
- **路由閘門是「從 application model 移除 controller」，不是清空 Selectors**：清 Selectors
  會讓 action 被視為 conventional routing，與 `[ApiController]` 強制啟用的 ApiExplorer 衝突，
  host 啟動就丟例外——這是體檢時被真實 host 整合測試抓到的（單元測試看不到路由內部行為，
  所以 `DeploymentModeTests` 用 `WebApplicationFactory` 驗到「請求真的 404」為止）。
- **`Line:OutboundHere` 而不是拆成「下載開關」＋「頭貼快取開關」**：媒體下載與頭貼快取
  都只需要 outbound HTTPS，沒有理由拆成兩個獨立設定；一對主機恰好一台要設 `true`，
  啟動時無法互相檢查，設錯（兩台都真或都假）不會啟動失敗，只會變成重複下載或永遠不下載。
- **`Line:OutboundHere=true` 卻沒有 `ChannelAccessToken` 目前不會啟動失敗**：Stage 1／2
  都還沒有任何註冊邏輯依據 `OutboundHere` 做決定（那是 Stage 3 才會接上的
  `IContentWorkSource`），現在就要求這個設定只會對還沒用到它的部署造成不必要的啟動失敗。
  等 Stage 3 真的接上後，這裡的驗證要一併補上。
- **ingest API 判定「重複」不回獨立狀態碼，一律 200**：規劃階段本來想讓 `IngestController`
  對「新寫入」回 200、對「判定為重複」回 409，讓客戶端更容易觀察。實作時發現這會逼
  `IIngestSink.SubmitAsync` 多開一個回傳值來區分兩種結果，而這個介面的既有契約
  （Stage 1 就定案、已被測試釘住）明講「成功（含判定為重複而略過）就正常回傳」——
  對呼叫端而言兩者都是「這筆已經在後端了，outbox 可以刪掉」，沒有行為上的差異，
  純觀察用途不值得為此打破一個已穩定的契約。**因此 `IngestController` 對兩者一律回 200**，
  `HttpIngestSink` 也只看 `IsSuccessStatusCode`，不特別處理 409。
- **`/api/ingest` 的兩個中介層只在 `hasDatabaseAccess`（`Full`／`Db`）才註冊**：一開始
  不分模式一律掛上 `UseWhen`，結果 `Line` 模式的主機每次啟動都印一行
  「AllowedClientIps 是空的」警告——這台主機本來就不會收到 `/api/ingest` 流量，
  這個警告只會讓人誤以為忘了設定。改成只在 controller 有機會存在的模式才註冊中介層。
- **`X-Ingest-Key` 用固定時間比較，`AllowedClientIps` 是 ingest API 專屬的獨立設定**：
  跟 `MessageService.Web/Middleware/IpAllowlistMiddleware.cs` 是同一套邏輯的獨立複本
  （兩專案互不參照），也刻意不跟 webhook 的簽章驗證共用任何機制——服務對服務的憑證
  跟「LINE 平台可信」是兩種完全不同的信任來源，混在一起會讓兩邊的威脅模型糾纏不清。

## 端到端驗證紀錄（2026-08-12，Stage 2 完工後）

用兩個真實本機實體驗證過完整拆機拓撲（非模擬）：`Db` 模式（`localhost:5081`，接資料庫，
設 `Ingest:ApiKey`＋`AllowedClientIps`）與 `Line` 模式（`localhost:5082`，
`Ingest:BaseUrl` 指向前者）。用正確 HMAC-SHA256 簽章送真實格式的 LINE webhook payload：

1. **正常路徑**：Line 端驗簽通過 → 寫本機 outbox → 回 200 → forwarder 排空 → `HttpIngestSink`
   打 `Db` 端 `/api/ingest/events` → `IngestIpAllowlistMiddleware`／`IngestApiKeyMiddleware`
   放行 → `IngestController` → `DirectIngestSink` 寫入。直接查 `Db` 端 SQLite 確認訊息內容、
   `GroupId`、`UserId`、`LineMessageId`、`WebhookEventId` 全部正確落地。
2. **斷線容忍**：中途 `kill` 掉 `Db` 實體，Line 端再收一則 webhook——**webhook 仍回 200**
   （沒有因為後端不通就讓 LINE 判定失效重送），訊息留在 Line 端本機 outbox（`Attempts` 遞增、
   `LastError` 記錄連線失敗訊息），資料沒有遺失。
3. **自動恢復**：重啟 `Db` 實體後，Line 端的 `OutboxForwarderService` 在下一輪退避到期時
   自動重試成功，outbox 排空回 0 筆，兩則訊息（含斷線期間那則）都正確落地、無重複列——
   驗證了 `WebhookEventId` 唯一索引在拆機場景下確實擋住了 outbox 重試可能產生的重複寫入。

這證實了本輪最初的三個需求（長期兩形態都要支援／LINE 連外落在哪端都能跑／API 不通不能掉訊息）
在純文字訊息的收送路徑上已經真正成立，不只是通過自動化測試。

## 目前限制（誠實記錄，別誤以為做完了）

- **媒體下載與頭貼快取還只能在有資料庫存取的主機執行**：`ContentDownloadService`／
  `ProfileRefreshService` 目前仍直接依賴 `MessageDbContext`。`Line` 模式拆機後，
  這兩個背景服務不會在該主機啟動（見 `Program.cs` 的 `hasDatabaseAccess` 判斷），
  訊息本身會正常收送，但媒體內容與頭貼快取要等 Stage 3 的 `IContentWorkSource`／
  `IProfileSink` 落地才能在拆機情境下運作。純文字訊息不受影響。
- **outbox 死信沒有專用的重送介面**：達到 `MaxAttempts` 或收到 `PermanentIngestException`
  的項目會被標記 `DeadLetteredAt`、停止自動重試，但資料留在 `outbox.db` 裡不會自動消失，
  只能手動查 `LastError` 欄位後決定怎麼處理。啟動時若偵測到死信筆數 >0 會印一行
  Warning log 提醒，量大時要考慮補一個管理介面。
- **`IContentWorkSource`／`IProfileSink` 的 API 實作、blob 串流上傳**：Stage 3 的範圍，
  目前完全沒有著手。

## 分階段

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 模式列舉、設定、啟動驗證、路由閘門 | ✅ 已完成 |
| 1 | outbox＋forwarder＋`IIngestSink`／`DirectIngestSink` | ✅ 已完成 |
| 2 | ingest API controller＋`HttpIngestSink`＋死信 | ✅ 已完成，端到端驗證通過 |
| 3 | `IContentWorkSource`／`IProfileSink` 的 API 實作＋blob 串流上傳 | 未開始 |
| 4 | 設定樣板、部署檢查表、兩套 sink 等價性自動化測試、文件收尾 | 未開始 |
