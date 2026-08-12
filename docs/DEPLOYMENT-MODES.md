# 收錄端部署模式

收錄端（`MessageService`）所在網段未必碰得到資料庫，因此支援三種部署角色，
由 `Deployment:Mode` 設定決定；同一份程式碼、同一個專案，角色只是設定差異，不是不同的部署產物。

> **目前進度（2026-08-12）**：Stage 0～3 全部完成——`Full`／`Line`／`Db` 三種模式**功能完全
> 對等**，差別只在資料流經過幾台機器。訊息收送＋媒體下載＋頭貼快取的完整拆機拓撲都已用
> 真實雙行程端到端驗證過（webhook 進 Line 端、轉送到 Db 端、含斷線期間 outbox 累積＋恢復後
> 自動排空且無重複、Line 端背景服務透過 ingest API 撿媒體工作並回報結果，見「端到端驗證
> 紀錄」）。Stage 4 原本規劃的「blob 端到端串流」已在另一輪媒體管線改善（收錄端安全體檢
> 回饋輪）中，作為 `IContentWorkSource` 共用介面的一部分順帶完成——`ApiContentWorkSource`
> 現在用 `StreamContent` 邊讀邊送，不再整包 `byte[]` 進記憶體，見下方「目前限制」的更新說明。

## 三種模式

| | Full（預設） | Line | Db |
|---|---|---|---|
| `/api/line/webhook` | ✓ | ✓ | 404（路由不存在，非拒絕） |
| `/api/ingest/*` | ✓（需設 `Ingest:ApiKey` 才存在） | 404（路由不存在） | ✓（需設 `Ingest:ApiKey`） |
| 直連資料庫 | ✓ | ✗ | ✓ |
| 本機 outbox＋排空 | ✓ | ✓ | ✗（無 webhook，無事件可寫） |
| 落地方式 | outbox → `DirectIngestSink` | outbox → `HttpIngestSink` → 對方的 `/api/ingest/events` | `IngestController` → `DirectIngestSink` |
| 保留期清除 | ✓ | ✗ | ✓ |
| 內容下載 / 頭貼快取 | 依 `Line:OutboundHere`（預設 ✓） | 依 `Line:OutboundHere`（預設 ✗，設 true 即可） | 依 `Line:OutboundHere`（預設 ✗） |

內容下載／頭貼快取現在完全獨立於模式，只看 `Line:OutboundHere`——這台主機要不要對外呼叫
LINE API。有資料庫的一端用 `DbContentWorkSource`／`DbProfileStore` 直接查表；沒有資料庫的
一端（`Line`）用 `ApiContentWorkSource`／`ApiProfileStore` 打 ingest API。一對拆機主機理論上
兩邊都能設 `OutboundHere=true`（例如 `Db` 端自己也連得到 LINE），但實務上通常只有一台需要。

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
                              OutboxForwarderService（背景排空，暫時性失敗指數退避永遠重試，
                                        │              僅永久性失敗（PermanentIngestException）死信）
                                        ▼
                                   IIngestSink
                          ┌─────────────┴─────────────┐
                    DirectIngestSink              HttpIngestSink
                    （Full／Db，EF 直寫）      （Line，POST /api/ingest/events）
                     回傳 IngestResult              ＋ X-Ingest-Key 標頭
                     （含 ContentId）                        │
                                                          ▼
                                          IngestIpAllowlistMiddleware
                                          ＋IngestApiKeyMiddleware（X-Ingest-Key）
                                                          ▼
                                                  IngestController
                                                          ▼
                                              （對方主機的）DirectIngestSink
                                                          │
                                    兩邊都用回傳的 IngestResult 呼叫 IngestSideEffects，
                                    各自用自己host本地的佇列決定要不要接手下載／頭貼刷新
                                                          ▼
                          ┌──────────────────────────────┴──────────────────────────────┐
                    IContentDownloadQueue                                    IProfileRefreshQueue
                    （Line:OutboundHere=true 才是真 Channel，                （同左；false 時皆為
                     false 時是 NullContentDownloadQueue）                    NullProfileRefreshQueue）
                          ▼                                                              ▼
                 ContentDownloadService                                      ProfileRefreshService
                          ▼                                                              ▼
                  IContentWorkSource                                            IProfileStore
          ┌───────────────┴───────────────┐                          ┌───────────────┴───────────────┐
   DbContentWorkSource            ApiContentWorkSource          DbProfileStore              ApiProfileStore
  （有資料庫那端，直查表）      （Line，打 ingest API 的            （有資料庫那端）          （Line，打 ingest API
                                content-work／content 端點，                                  的 profiles 端點）
                                 X-Ingest-Key 在具名 HttpClient
                                 註冊時就設好，見設計決策）
```

webhook 回應時間因此跟資料庫或遠端 API 是否可用完全脫鉤：即使主資料庫短暫斷線，
`Full` 模式的訊息也只是在 outbox 裡等，恢復後自動排空，不會遺失
（`OutboxForwarderService.ProcessBatchAsync`／`MessageService.Tests/Outbox/`）。

## 設定

| 設定鍵 | 說明 |
|---|---|
| `Deployment:Mode` | `Full`（預設）／`Line`／`Db` |
| `ConnectionStrings:Outbox` | 本機 outbox 的 SQLite 檔（`Full`／`Line` 才用得到），預設 `Data Source=outbox.db` |
| `Line:OutboundHere` | 這台要不要對外呼叫 LINE API（媒體下載＋頭貼快取）。決定 `ContentDownloadService`／`ProfileRefreshService` 會不會在這台主機啟動，以及 `IContentDownloadQueue`／`IProfileRefreshQueue` 是真的 Channel 還是 Null 實作。`true` 時必須同時設定 `Line:ChannelAccessToken`，否則啟動失敗 |
| `Line:ChannelAccessToken` | 呼叫 LINE content／profile API 要用的權杖。只有 `Line:OutboundHere=true` 時才需要 |
| `Ingest:BaseUrl` | `Line` 模式打去哪個 `Db` 模式主機的 ingest API（如 `https://db-host/`） |
| `Ingest:ApiKey` | 雙邊共用密鑰：`Line` 端當 `X-Ingest-Key` 標頭送出、`Db`／`Full` 端驗證進來的請求，兩邊必須一致。**留空時 `/api/ingest/*` 整個不存在（404）**——避免單機部署意外多開一個沒人保護的寫入端點 |
| `Ingest:MaxContentBytes` | `PUT /api/ingest/content/{id}` 單次上傳允許的最大位元組數，預設 300MB（Kestrel 預設請求上限 30MB 擋得住 LINE 的大檔，這裡動態放寬，見 `IngestController.UploadContent`） |
| `AllowedClientIps` | `/api/ingest/*` 的 IP 白名單（跟 `MessageService.Web` 的同名設定是獨立的兩份，各自只保護各自的端點），只在 `Full`／`Db` 模式生效；空白名單視為全拒 |
| `Outbox:PollIntervalSeconds` | outbox 空的時候的保底輪詢間隔（寫入會立刻叫醒，這只是撿回到期重試項目用），預設 5 |
| `Outbox:BatchSize` | 一輪最多處理幾筆，預設 50 |
| `Outbox:BaseRetryDelaySeconds` / `MaxRetryDelaySeconds` | 指數退避：第 N 次失敗延遲 `Base × 2^(N-1)`，封頂 `Max`，預設 5／300。**沒有累計次數上限**——暫時性失敗永遠重試，只有 `PermanentIngestException`（payload 格式本身不合，重試也不會成功）第一次遇到就直接標記死信（見下方「目前限制」） |

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
- ~~**`Line:OutboundHere=true` 卻沒有 `ChannelAccessToken` 目前不會啟動失敗**~~（Stage 3 補上）：
  Stage 1／2 時這條規則刻意不加，因為還沒有任何註冊邏輯依據 `OutboundHere` 做決定。
  Stage 3 接上 `ContentDownloadService`／`ProfileRefreshService` 之後，`OutboundHere=true`
  卻沒有權杖會讓這兩個背景服務啟動後對 LINE API 持續打 401——不是啟動就爆炸、是悄悄
  一直失敗，所以在 `DeploymentValidator` 補了對應檢查，`Full` 模式關掉 `OutboundHere`
  則只記警告（單機部署這樣做可疑但不是錯誤）。
- **入列（媒體下載／頭貼刷新）責任從 `DirectIngestSink` 移到呼叫端**：Stage 1／2 時
  `DirectIngestSink` 存檔後直接呼叫自己持有的 `downloadQueue.Enqueue(...)`。Stage 3 讓
  `IIngestSink.SubmitAsync` 開始回傳 `ContentId` 之後，若 `DirectIngestSink` 還繼續自己
  入列，`Full` 模式下 `OutboxForwarderService` 呼叫它、拿到回傳的 `ContentId` 後**又**呼叫
  一次入列，會造成同一筆內容被入列兩次（雖然 `ContentDownloadService.ProcessAsync` 的
  Pending 狀態檢查讓第二次只是空跑，不算資料錯誤，但終究是多餘、容易誤導人）。
  改成 `DirectIngestSink` 只管持久化、不碰任何佇列，`IngestController`（Db 端收到 Line
  轉來的請求）與 `OutboxForwarderService`（本機排空）兩個呼叫端各自用自己 host 本地的
  `IContentDownloadQueue`／`IProfileRefreshQueue`，透過共用的 `IngestSideEffects.Apply`
  靜態方法決定要不要接手——這台主機的佇列是真的還是 Null，呼叫端完全不用知道。
- **`ContentId` 值得為此擴充 `IIngestSink` 契約，`Stage 2` 的 409 卻不值得**：兩者性質不同。
  409 是純觀察用途、沒有任何行為依賴它；`ContentId` 是**功能上必需**——沒有它，拆機模式
  的媒體永遠不會知道要下載哪一筆。重複情境也必須回傳既有那筆的 `ContentId`（不能回
  `null`），否則 outbox 重試（代表前一次的回應可能遺失了）會讓那筆媒體卡到下次服務
  重啟的啟動重撈才補回。`DirectIngestSink` 的預查與 `DbUpdateException` 回查兩處都改成
  投影 `new { m.Id, ContentId }`（而不是只投影 `ContentId`）——「查無此列」與「該列存在
  但沒有媒體內容」兩種情況投影出的 `ContentId` 都是 `null`，若只投影 `ContentId` 會分不清
  該不該繼續往下插入新列。
- **`ApiContentWorkSource`／`ApiProfileStore` 忘記帶 `X-Ingest-Key`，只有真雙行程互打才測出來**：
  這兩個類別一開始完全沒加認證標頭，所有請求被 `IngestApiKeyMiddleware` 擋成 401。
  之所以自動化測試（含等價性測試、真實 host 整合測試）全部沒抓到，是因為那些測試不是直接
  呼叫 controller（帶著測試手動加的標頭），就是用 `HttpIngestSink`（它自己確實有加標頭）——
  沒有一個真的經過這兩個類別實際發出的請求。改成在 `Program.cs` 註冊具名 `HttpClient` 時
  就把 `X-Ingest-Key` 設成預設標頭，一次到位，不必要求每個方法自己記得加；並補一條
  整合測試直接從 DI 解析具名 client 檢查標頭在，防止回歸。**教訓：純文字打字看不出
  「這段程式碼會不會被兩個獨立行程執行」，這種缺口只有真的跑兩個行程互打才測得出來**
  （這也是本專案第二次因為同樣理由抓到 wiring bug，見 Stage 2 的路由閘門那次）。
- **等價性測試與其他真實 host 整合測試不能用預設的 Development 環境**：`WebApplicationFactory`
  預設環境是 Development，會自動載入 `dotnet user-secrets`——這台開發機的 user-secrets
  存了一把真的 LINE Channel Access Token（先前手動測試真實 LINE bot 用的），會讓
  `Line:OutboundHere` 預設 `true` 卻沒設 `ChannelAccessToken` 的啟動驗證規則被意外滿足，
  在**這台機器**「湊巧通過」，換一台乾淨機器或 CI 就會炸。所有這類測試改成
  `builder.UseEnvironment("Testing")`（沿用 `MessageService.Web.Tests` 既有的同款作法），
  讓 `appsettings.json`（值都是空字串或類別預設）成為唯一基底，不受任何本機殘留設定影響。
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
- **`IProfileStore.GetStalenessAsync` 跟 upsert 方法分開，不合併成一支**：TTL 判斷一定要在
  打 LINE API **之前**完成才能省到配額；群組與成員的過期判斷合併成一次往返（減少 Line
  端到 Db 端的來回次數），但查詢跟寫入本身不能合併——這是既有 `ProfileRefreshService`
  「查 TTL → 打 LINE → 過期才 upsert」流程沒變過的結構，只是把首尾兩步抽到
  `IProfileStore` 而已。
- **媒體下載失敗不疊加 outbox 式的死信機制**：`ContentDownloadService` 本來就有一套
  `MaxRetries`／`DownloadStatus.Failed` 狀態機（單機模式沿用至今），`ApiContentWorkSource`
  遇到任何非 2xx 或連線層錯誤一律往外拋，直接交給這套既有邏輯處理，不像 `IIngestSink`
  那樣需要分辨「永久失敗」——兩層死信機制疊加只會增加心智負擔，沒有對應的好處。
- **頭貼快取的例外不重試**：`IProfileStore` 的實作（含 `ApiProfileStore`）遇到例外一律
  往外拋，由 `ProfileRefreshService` 既有的「記 log、不重試」處理——頭貼快取是非關鍵
  資料，API 不通時這次刷新失敗即可，下一則該群組／成員的訊息進來時會重新入列，
  不需要比照訊息落地疊加 outbox 那樣的保護。

## 端到端驗證紀錄

兩輪都用真實本機雙行程驗證（非模擬、非單元測試 mock）——這是本輪唯一能真正證明
「拆機版本跟單機版本一模一樣」的方式，過程中也各抓到一個自動化測試沒抓到的真 bug。

### Stage 2（2026-08-12）：純文字訊息收送

`Db` 模式（`localhost:5081`，接資料庫，設 `Ingest:ApiKey`＋`AllowedClientIps`）與
`Line` 模式（`localhost:5082`，`Ingest:BaseUrl` 指向前者）。用正確 HMAC-SHA256 簽章送
真實格式的 LINE webhook payload：

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

證實了本輪最初的三個需求（長期兩形態都要支援／LINE 連外落在哪端都能跑／API 不通不能掉訊息）
在純文字訊息的收送路徑上真正成立。

### Stage 3（2026-08-12）：媒體下載的完整 wiring

同樣兩個實體，這次 `Db` 端設 `Line:OutboundHere=false`、`Line` 端設 `OutboundHere=true`
（含一個假的 `ChannelAccessToken`——沒有真實 LINE 憑證能完成真正下載，但足以驗證
除了「打 LINE API 本身」之外的每一段 wiring）：

1. **第一次跑就抓到真 bug**：`Line` 端啟動時 `ContentDownloadService.RequeuePendingAsync`
   立刻收到 401——`ApiContentWorkSource` 打 `Db` 端的請求完全沒帶 `X-Ingest-Key`。
   這是實作時就存在的疏漏，所有自動化測試都沒抓到，因為沒有一個測試真的經過這兩個
   類別實際發出的請求（見上面設計決策的說明）。修好後（在具名 `HttpClient` 註冊時
   設定預設標頭）重新驗證：
2. **正常路徑（含媒體）**：送一則圖片 webhook → Line 端寫 outbox → 轉送到 `Db` 端
   → `DirectIngestSink` 寫入 `GroupMessage`＋`MessageContent`（`Pending`）→ `ContentId`
   透過 HTTP 回應帶回 Line 端 → `IngestSideEffects.Apply` 用 Line 端**自己的**
   （因為 `OutboundHere=true` 而是真的）`IContentDownloadQueue` 入列 → Line 端
   `ContentDownloadService.ProcessAsync` 撿起工作 → `ApiContentWorkSource.GetAsync`
   正確認證、成功取回 `ContentWorkItem` → 嘗試下載（假憑證，預期對真實 LINE API 404）
   → 重試耗盡後 `ApiContentWorkSource.FailAsync` 呼叫 `Db` 端 `/api/ingest/content/{id}/failed`
   → 直接查 `Db` 端 SQLite 確認 `MessageContent.DownloadStatus` 正確變成 `Failed`。

證實了 Stage 3 新增的每一段介面銜接（`ContentId` 回傳與跨行程傳遞、`IngestSideEffects`
的本機佇列判斷、`ApiContentWorkSource` 的認證與端點呼叫、失敗回報）在真實雙行程下
完全正確——唯一沒驗證到的只有「打真實 LINE API 拿到真的檔案」這一小段，那不是這個
架構要驗證的範圍。

## 目前限制（誠實記錄，別誤以為做完了）

- **blob 傳輸已改為端到端串流**（原 Stage 4 規劃項目，已在另一輪媒體管線改善中順帶完成）：
  `ILineContentClient.GetContentAsync` 現在回傳未緩衝的 `Stream`（`HttpCompletionOption.ResponseHeadersRead`），
  `IContentWorkSource.CompleteAsync` 的兩套實作都改成串流——`DbContentWorkSource` 對 SQL Server
  用 `SqlParameter.Value = Stream` 邊讀邊寫、對 SQLite 用 `zeroblob`＋`SqliteBlob` 分塊寫入；
  `ApiContentWorkSource.CompleteAsync`（Line 模式打 Db 端 ingest API 的路徑）改用
  `StreamContent` 邊讀邊送，不再整包 `byte[]` 進記憶體。唯一的例外是 LINE 回應沒帶
  `Content-Length` 的少數情況——這時會先落一個會自動刪除的暫存檔量出實際長度，量完
  再從暫存檔串流寫入，只佔用磁碟不佔用記憶體（`ContentDownloadService.CompleteFromResultAsync`）。
  數百 MB 檔案在拆機情境下的實際吞吐量仍建議部署前實測，但記憶體峰值已不再跟檔案大小
  成正比。
- **outbox 死信沒有專用的重送介面**：收到 `PermanentIngestException` 的項目會被標記
  `DeadLetteredAt`、停止自動重試（暫時性失敗則永遠退避重試、不會死信），但資料留在
  `outbox.db` 裡不會自動消失，只能手動查 `LastError` 欄位後決定怎麼處理。
  `OutboxForwarderService` 每小時記一次目前死信筆數，量大時要考慮補一個管理介面。
- **`Line:OutboundHere` 設錯無法跨主機驗證**：一對拆機主機理論上恰好一台要設 `true`，
  但啟動驗證只能看到自己這台的設定，兩台都 `true`（重複下載，浪費 LINE 配額，但有
  唯一約束擋著不會產生髒資料）或兩台都 `false`（媒體永遠 `Pending`）都不會啟動失敗，
  只能靠部署檢查表把關。

## 分階段

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 模式列舉、設定、啟動驗證、路由閘門 | ✅ 已完成 |
| 1 | outbox＋forwarder＋`IIngestSink`／`DirectIngestSink` | ✅ 已完成 |
| 2 | ingest API controller＋`HttpIngestSink`＋死信 | ✅ 已完成，端到端驗證通過 |
| 3 | `IContentWorkSource`／`IProfileStore` 的 API 實作＋入列責任重構 | ✅ 已完成，端到端驗證通過（含抓到並修復真 bug） |
| 4 | blob 端到端串流、部署檢查表、設定樣板 | blob 串流已在另一輪媒體管線改善中完成；部署檢查表、設定樣板未開始 |
