# 收錄端部署模式

收錄端（`MessageService`）所在網段未必碰得到資料庫，因此支援三種部署角色，
由 `Deployment:Mode` 設定決定；同一份程式碼、同一個專案，角色只是設定差異，不是不同的部署產物。

> **目前進度（2026-08-11）**：Stage 0＋1 已完成——模式切換的骨架、路由閘門、本機 outbox
> 與背景排空全部到位，**但只有 `Full` 模式功能完整可用**（等同過去唯一支援的形態，
> 行為零改變）。`Line` 模式目前**啟動就會失敗**（見下方「目前限制」）；`Db` 模式雖然
> 啟動得起來，但因為 ingest API 還沒實作，沒有任何管道能把資料送進來，實務上還不能用。

## 三種模式

| | Full（預設） | Line | Db |
|---|---|---|---|
| `/api/line/webhook` | ✓ | ✓ | 404（路由不存在，非拒絕） |
| 直連資料庫 | ✓ | ✗ | ✓ |
| 本機 outbox＋排空 | ✓ | ✓ | ✗（無 webhook，無事件可寫） |
| 落地方式 | outbox → DirectIngestSink | outbox → （Stage 2 才有的 HttpIngestSink） | 尚無接收端點 |
| 保留期清除 / 內容下載 / 頭貼快取 | ✓ | ✗（本輪不動） | ✓ |

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
                              OutboxForwarderService（背景排空，失敗按退避重試）
                                        ▼
                                   IIngestSink
                          ┌─────────────┴─────────────┐
                    DirectIngestSink           HttpIngestSink（Stage 2，尚未實作）
                    （Full／Db，EF 直寫）         （Line，打 ingest API）
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
| `Ingest:BaseUrl` / `Ingest:ApiKey` | `Line` 模式打去哪個 `Db` 模式主機、用什麼金鑰；`Db` 模式用同一把金鑰驗證進來的請求 |
| `Outbox:PollIntervalSeconds` | outbox 空的時候的保底輪詢間隔（寫入會立刻叫醒，這只是撿回到期重試項目用），預設 5 |
| `Outbox:BatchSize` | 一輪最多處理幾筆，預設 50 |
| `Outbox:BaseRetryDelaySeconds` / `MaxRetryDelaySeconds` | 重試退避：第 N 次失敗延遲約 `Base × N`，封頂 `Max`，預設 5／300 |

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
- **`Line:OutboundHere=true` 卻沒有 `ChannelAccessToken` 目前不會啟動失敗**：Stage 1
  還沒有任何註冊邏輯依據 `OutboundHere` 做決定（那是 Stage 3 才會接上的
  `IContentWorkSource`），現在就要求這個設定只會對還沒用到它的部署造成不必要的啟動失敗。
  等 Stage 3 真的接上後，這裡的驗證要一併補上。

## 目前限制（誠實記錄，別誤以為做完了）

- **`Deployment:Mode=Line` 現在啟動就會丟 `InvalidOperationException`**：`HttpIngestSink`
  （把 outbox 推去遠端 ingest API）是 Stage 2 的工作，還沒實作。寫在 outbox 裡的訊息
  沒有東西能把它排空，所以刻意讓這個模式現在選不了，而不是讓它悄悄跑起來、訊息卡死在本機
  outbox 永遠出不去。錯誤訊息會直接說明原因與現階段該用 `Full`。
- **`Deployment:Mode=Db` 沒有接收端點**：ingest API controller（`/api/ingest/*`）同樣是
  Stage 2 的工作。這個模式現在啟動得起來（資料庫、`DirectIngestSink` 都已就緒，
  是為了讓 Stage 2 落地時只需要加 controller，不必再動 DI 註冊），但沒有任何東西會呼叫它。
- **outbox 沒有死信處理**：一筆序列化失敗或永遠無法落地的項目會照退避規則無限重試
  （封頂在 `MaxRetryDelaySeconds`），只會在 log 看到持續的警告，不會被隔離或丟棄。
  單一使用者的內部工具目前可接受，量大或要長期無人值守時要補。
- **兩套 `IIngestSink` 的等價性還沒有測試釘住**：因為 Stage 1 只有 `DirectIngestSink`
  一種實作，「同一批輸入兩條路徑要產生相同結果」的等價性測試等 Stage 2
  `HttpIngestSink` 落地後才補得出來、也才有意義。
- **數百 MB 的媒體內容過 API 這件事還沒設計**：`IContentWorkSource` 的 API 實作、blob
  串流上傳，是 Stage 3 的範圍，目前完全沒有著手。

## 分階段

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 模式列舉、設定、啟動驗證、路由閘門 | ✅ 已完成 |
| 1 | outbox＋forwarder＋`IIngestSink`／`DirectIngestSink` | ✅ 已完成 |
| 2 | ingest API controller＋`HttpIngestSink` | 未開始 |
| 3 | `IContentWorkSource` 的 API 實作＋blob 串流上傳 | 未開始 |
| 4 | 設定樣板、部署檢查表、三模式測試矩陣、文件收尾 | 未開始 |
