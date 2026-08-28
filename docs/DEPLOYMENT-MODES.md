# 部署角色（Deployment Modes）

> 本文件只講現行的四種角色定義與設定；合併專案的原因、設計決策理由、雙行程端到端驗證紀錄
> 見 [docs/history/DEPLOYMENT-MODES-DECISIONS.md](history/DEPLOYMENT-MODES-DECISIONS.md)——
> 非必要不需要讀，避免浪費 token。

`MessageService.Web` 是唯一的可發佈專案，同一份成品可以部署成一台包辦，也可以拆成兩三台，
由每台主機各自的 `Deployment:Mode` 設定決定角色——不是不同的部署產物，只是設定差異。
`Deployment:Mode` 的合法值是 `AllInOne`／`Edge`／`Core`／`Viewer`；舊名稱 `Full`／`Line`／`Db`
仍是相容別名（讀到時 log 記一則提醒，不擋啟動）。

## 四種角色

| | AllInOne | Edge | Core | Viewer |
|---|---|---|---|---|
| `/api/line/webhook` | ✓ | ✓ | ✗（路由不存在，非拒絕） | ✗ |
| `/api/ingest/*` | 視 `Ingest:ApiKey` 而定 | ✗ | 視 `Ingest:ApiKey` 而定 | ✗ |
| 檢視端 UI＋API | ✓ | ✗ | ✓（可用 `Viewer:Enabled=false` 關掉，三台拓撲用） | ✓ |
| 直連主資料庫 | ✓ | ✗ | ✓ | ✓ |
| 本機 outbox＋排空 | ✓ | ✓ | ✗（無 webhook，無事件可寫） | ✗ |
| 保留期清除 | ✓ | ✗ | ✓ | ✗（即使直連資料庫也不跑，避免三台拓撲下跟 Core 搶著清同一張表） |
| 貼圖內容回填 | ✓ | ✗ | ✓ | ✗（同上：維護類背景工作只由一台負責。兩台同跑會撞唯一鍵，程式會逐筆補完該批未撞的部分，但那是白工） |
| `Line:OutboundHere` 預設 | `true` | `true` | `false` | `false` |
| 落地方式 | outbox → `DirectIngestSink` | outbox → `HttpIngestSink` → 對方的 `/api/ingest/events(-batch)` | `IngestController` → `DirectIngestSink` | 不適用 |

內容下載／頭貼快取完全獨立於模式，只看 `Line:OutboundHere`（`bool?`，未顯式設定時依模式
推導，見上表）——這台主機要不要對外呼叫 LINE API。「頭貼快取」除了名稱與來源 URL，也包含
圖檔本體；貼圖同樣走內容下載管線（見 README 的訊息型別表）。有資料庫的一端用
`DbContentWorkSource`／`DbProfileStore` 直接查表；沒有資料庫的一端（Edge）用
`ApiContentWorkSource`／`ApiProfileStore` 打 ingest API。一對拆機主機理論上兩邊都能設
`OutboundHere=true`（例如 Core 端自己也連得到 LINE），但實務上通常只有一台需要。

`AllInOne` 就是最初的單機行為：收 webhook、寫本機 outbox、由背景服務排空並直接寫進資料庫。
沒有設定 `Deployment:Mode` 就是這個模式。

三種拓撲怎麼組合：

- **一台**：`Mode=AllInOne`。
- **兩台**：有兩種切法，選哪一種看「收 webhook 的主機碰不碰得到資料庫」：
  - **A. `AllInOne`（`Viewer:Enabled=false`）＋獨立的 `Viewer`**——webhook 主機碰得到資料庫，
    純粹是想把網頁流量隔開（例如檢視端要開放給更多同事、不想讓瀏覽流量跟收錄行程搶
    資源）。沒有 ingest API、沒有 outbox 跨主機轉送、沒有 `Ingest:ApiKey` 要兩邊對齊，
    維運成本比 B 低很多。**硬前提：必須用 SQL Server**——`Viewer` 那台要直連跟 `AllInOne`
    那台同一顆資料庫，SQLite 是本機檔案，跨主機透過網路磁碟共用同一個 `.db` 檔案不可行
    （見 `appsettings.Production.Viewer.json` 的說明）。還在用 SQLite 的話只能選 B。
  - **B. `Edge` ＋ `Core`**——webhook 主機在 DMZ、碰不到資料庫所在的內網，才需要這一種。
    Core 那台繼續用 SQLite 也可以。
  - 沒有網段隔離需求、而且已經在用 SQL Server 時，選 A 不要選 B；有 DMZ 隔離需求，或還在
    用 SQLite，選 B。
- **三台**：Edge＋Core（`Viewer:Enabled=false`，只做資料庫與 ingest API）＋獨立的 Viewer
  （純檢視端）。

推導邏輯全部收斂在 `MessageService.Services.DeploymentCapabilities.Derive`
（`ReceivesWebhook`／`HasDatabaseAccess`／`IngestApiEnabled`／`ViewerEnabled`／`OutboundHere`／
`RunsRetention` 六個布林），`Program.cs` 與 `DeploymentModeConvention` 都只吃這個推導結果，
不會出現「改一處模式判斷、忘了改另一處」的分裂。

## 設定鍵升級對照

合併專案時把幾個容易設錯的地方順手修正，既有 `appsettings.Production.json` 大多不用改
（模式名稱有別名相容），但白名單 key 是硬性拆分（沒有相容別名，設錯會直接擋啟動）：

| 舊 key（合併前兩個專案各自的 `appsettings.json`） | 新 key | 相容性 |
|---|---|---|
| `Deployment:Mode` = `Full`／`Line`／`Db` | `AllInOne`／`Edge`／`Core` | **相容**：舊名稱仍是合法列舉值，讀到舊名記一行 Warning 但不擋啟動 |
| 收錄端的 `AllowedClientIps`（保護 `/api/ingest/*`） | `Ingest:AllowedClientIps` | **不相容**：啟動時偵測到根層級 `AllowedClientIps` 非空會直接丟例外——合併成單一設定檔後，兩個原本各自獨立的白名單如果共用同一個 key，會被迫套用同一份清單，這在真實拆機拓撲下是安全問題，寧可擋啟動也不要被悄悄忽略 |
| 檢視端的 `AllowedClientIps`（保護檢視端頁面／API） | `Viewer:AllowedClientIps` | 同上，不相容 |
| （無） | `Viewer:Enabled`（`bool?`） | 新增，三台拓撲用；未設定時依模式推導預設值 |
| （無） | `Http:UseHttpsRedirection`（`bool`，預設 `false`） | 新增，IIS 直接綁 HTTPS 的部署不需要應用層再轉址一次 |
| （無） | `Database:AutoMigrate`（`bool`，預設 `true`） | 新增，啟動時自動跑 `Database.Migrate()`；**三台拓撲請只在 Core 開、Viewer 設 `false`**（鎖不跨機器，見 DEPLOYMENT-GUIDE） |
| （無） | `Database:SqliteBusyTimeoutMs`／`ContentDownload:ClaimLeaseMinutes`／`ContentDownload:MaxPendingIdsPerScan` | 新增，說明見 README 設定表 |
| （無） | `Ingest:Channel`（預設 `Auto`）／`Ingest:EdgeBaseUrl`（預設空）／`Ingest:PullIntervalSeconds`／`Ingest:PullActivationSeconds`／`Ingest:PullFailureMaxBackoffSeconds`／`Ingest:PullStagingMaxBytes`／`Ingest:ChannelProbeIntervalMinutes` | 新增，通道方向相關；全部有預設值，不改設定即維持推送行為（見上方「通道方向」） |

## 通道方向：Edge↔Core 只需要開通單向防火牆

Edge 與 Core 之間的四條資料流（訊息、心跳、媒體、名稱／頭貼）可以整組走任一方向，
由 Edge 端的 `Ingest:Channel` 與 Core 端的 `Ingest:EdgeBaseUrl` 決定。

| `Ingest:Channel`（Edge） | 行為 |
|---|---|
| `Auto`（預設） | 推送優先。推送失敗就暫停主動連線，改由 Core 輪詢接手；之後每隔 `Ingest:ChannelProbeIntervalMinutes`（預設 60 分）放行一次推送當探測，通了就自動恢復。同時開放 `/api/edge` 供 Core 輪詢。 |
| `Push` | 只主動推送，不開放 `/api/edge`。 |
| `Pull` | 從不主動連 Core（`OutboxForwarderService` 不註冊），只開放 `/api/edge`。`Ingest:BaseUrl` 可留空。 |

Core 端的輪詢器只在有資料庫存取權且設定了 `Ingest:EdgeBaseUrl` 時才註冊；留空（預設）
就完全不輪詢。
啟動後，距離最後一次**推送**心跳超過 `Ingest:PullActivationSeconds`（預設 180 秒）
就開始每 `Ingest:PullIntervalSeconds`（預設 1 秒）輪詢一次；收到推送心跳就停止。
判斷只看推送通道的心跳——輪詢自己拉回來的心跳不算，否則輪詢會把自己停掉。

輪詢失敗時間隔指數退避到 `Ingest:PullFailureMaxBackoffSeconds`（預設 60 秒），
成功即回到正常間隔；連續失敗只在進入與恢復時各記一則 log，持續期間每 10 分鐘一則摘要。

### 拉取方向的資料流

```
Core ──每 1 秒──▶ POST /api/edge/poll ──▶ Edge
        request：待下載的媒體工作、待刷新的名稱／頭貼（含 Core 算好的 staleness）
        response：心跳、待送訊息批次、已收下的派工、已下載完成的內容 Id、
                  下載失敗的 Id、名稱／頭貼結果（單輪有位元組預算）

Core ──▶ POST /api/edge/outbox/ack        落地成功後才送，Edge 收到才刪 outbox
Core ──▶ GET  /api/edge/content/{id}      取回 blob，長逾時 client，取回中不重複發
Core ──▶ POST /api/edge/content/{id}/ack  完整落地後才送，Edge 收到才釋放記憶體暫存
```

訊息是 at-least-once：Core 落地成功才送 ack，Edge 收到 ack 才刪 outbox，重複投遞靠
`GroupMessages.WebhookEventId` 唯一索引去重。

媒體的重試次數與保留視窗仍由 Core 端的 `DbContentWorkSource` 判斷（`MaxRetries`、
`FailedRetryWindowDays`），Edge 只是換一個地方下載。派工不做認領，那些內容在 Core 端
一路維持 `Pending` 直到取回落地為止；`ContentDownload:ClaimLeaseMinutes` 的租約回收
只在推送方向的下載路徑上有作用。Core 每輪把新落地的媒體立刻派出去，另外每
`ContentDownload:RequeueIntervalMinutes` 做一次全表掃描撿回漏網的。

名稱／頭貼的 TTL 判斷在 Core（Edge 沒有資料庫），四類（群組名稱、成員名稱、群組圖片、
成員頭貼）都會回傳。圖片位元組隨 poll 回應一起帶回，單輪有總量預算，超出的下一輪再回。
這條流沒有 ack：回應在傳輸中遺失時該筆結果會消失，等 TTL 再次過期時重新刷新。

Edge 端下載完成的媒體放在記憶體（`Ingest:PullStagingMaxBytes`，預設 600MB，生效值不小於
`Ingest:MaxContentBytes`），收到 ack 前不釋放，所以取回中斷可以原樣重取；暫存滿時拒收新派工，
那些工作留在 Core 端維持 `Pending` 下一輪再派。Edge 重啟遺失暫存時，那幾筆同樣還是 `Pending`，
下一輪 poll 就會重新派工。

### 啟用拉取方向要設定什麼

Edge 端的 `/api/edge` 由既有的 `Ingest:ApiKey` 與 `Ingest:AllowedClientIps` 保護，
兩層都沿用 ingest 那組設定。**`Ingest:AllowedClientIps` 空清單等於全部拒絕**
（見 `IpAllowlistMiddleware`），所以啟用拉取方向時 Edge 必須把 Core 的 IP 加進去，
否則輪詢一律 403。

不改任何設定時 Edge 主動推送、Core 不輪詢：`Ingest:Channel` 預設 `Auto`，
Core 端 `Ingest:EdgeBaseUrl` 預設空、輪詢器不註冊。Edge 上有 `/api/edge` 路由，
沒設白名單時它對所有來源回 403。

## 架構：outbox 是唯一的落地路徑

```
LINE ──▶ LineWebhookController ──▶ WebhookEventHandler
                                        │ 驗簽已在 controller 做完；這裡只解析/過濾成
                                        │ IngestEnvelope，不碰資料庫、不打網路
                                        ▼
                              outbox.db（本機 SQLite，跟主資料庫完全獨立）
                                        ▲ 立即回 200
                                        │
                              OutboxForwarderService（背景排空，一次一批：暫時性失敗整批
                                        │              指數退避重試；PermanentIngestException
                                        │              只影響那一筆，其餘照常，直接死信）
                                        ▼
                                   IIngestSink.SubmitBatchAsync
                          ┌─────────────┴─────────────┐
                    DirectIngestSink              HttpIngestSink
                 （AllInOne／Core，介面預設實作      （Edge，POST /api/ingest/events-batch，
                  逐筆呼叫 SubmitAsync，              一次送整批；對方 404＝還沒升級，
                  本來就沒有每筆一次 RTT 的問題）      自動退回逐筆模式，見下方升級順序）
                  回傳每筆 IngestResult                        │
                  （含 ContentId）                             ▼
                                                  IngestIpAllowlistMiddleware
                                                  ＋IngestApiKeyMiddleware（X-Ingest-Key）
                                                                ▼
                                                        IngestController
                                                                ▼
                                                （對方主機的）DirectIngestSink
                                                                │
                                    兩邊都用每筆結果呼叫 IngestSideEffects，
                                    各自用自己host本地的佇列決定要不要接手下載／頭貼刷新
                                                                ▼
                          ┌──────────────────────────────┴──────────────────────────────┐
                    IContentDownloadQueue                                    IProfileRefreshQueue
                    （OutboundHere=true 才是真 Channel，                     （同左；false 時皆為
                     false 時是 NullContentDownloadQueue）                    NullProfileRefreshQueue）
                          ▼                                                              ▼
                 ContentDownloadService                                      ProfileRefreshService
                          ▼                                                              ▼
                  IContentWorkSource                                            IProfileStore
          ┌───────────────┴───────────────┐                          ┌───────────────┴───────────────┐
   DbContentWorkSource            ApiContentWorkSource          DbProfileStore              ApiProfileStore
  （有資料庫那端，直查表）      （Edge，打 ingest API 的            （有資料庫那端）          （Edge，打 ingest API
                                content-work／content 端點，                                  的 profiles 端點，頭貼
                                 X-Ingest-Key 在具名 HttpClient                               圖檔位元組也是走此既有端點傳輸）
                                 註冊時就設好）
```

webhook 回應時間因此跟資料庫或遠端 API 是否可用完全脫鉤：即使主資料庫短暫斷線，
AllInOne 模式的訊息也只是在 outbox 裡等，恢復後自動排空，不會遺失。

### 批次 ingest（拆機模式的 outbox 排空吞吐）

Edge 端排空 outbox 時預設一次 HTTP 請求送整批（`Outbox:BatchSize` 筆為單位），取代逐筆各自
一次 round-trip。Core 端依序落地、回傳逐筆結果（成功／永久拒絕各自標記）；批次中途遇到
**暫時性失敗**（例如連線中斷）視為整批這次沒處理完，直接讓 outbox 那批整體照退避排程重試——
已經成功的項目重送是安全的（`IIngestSink` 的冪等保證），不需要逐筆記錄
「處理到哪」；批次中途遇到**永久拒絕**（`PermanentIngestException`，例如某筆 payload 格式
不合）只影響那一筆，其餘照常處理並從 outbox 移除。

**升級順序：先升 Core 再升 Edge。** Edge 打到還沒升級、沒有 `/api/ingest/events-batch`
這支端點的舊版 Core 時會收到 404，自動退回逐筆模式（每次都照樣先試批次端點，Core 升級後
不用重啟 Edge 就會自動改用批次），只記一次警告 log 避免過渡期洗版。同一條順序也適用於
`content-work` 端點的 `reclaimDownloading` 參數：舊版 Core 會忽略它、一律撿回 `Downloading`，
新版 Edge 的週期重掃打到舊版 Core 就會把正在下載中的項目打回 `Pending`；舊版 Edge 打新版
Core 則沒有問題（參數預設值就是舊行為）。`startupAgeSeconds`（早期版本是 `isStartup`）與
`ownerId` 參數同理：舊版 Core 忽略它們，只是少了「啟動時立即回收自己孤兒」的優化，等租約
逾期仍會回收；舊版 Edge 只送得出 `isStartup=true`，新版 Core 收到它但沒有 `startupAgeSeconds`
時**不做**啟動回收（沒有基準時刻就無法分辨孤兒與兄弟行程正在進行的下載，寧可等租約逾期）；
舊版 Edge 沒帶 `ownerId` 時新版 Core 一律記成 `legacy-edge`。

## 設定

模式判斷本身只看 `Deployment:Mode`；其餘設定鍵（`Line:OutboundHere`、`Viewer:Enabled`、
`Ingest:*`、`Database:*`、`Heartbeat:*` 等）的完整清單與說明見
[README.md](../README.md#收錄) 的設定表，這裡不重複一份。

合併專案與模式改名的原因、逐項設計決策的推演過程、雙行程端到端驗證紀錄見
[docs/history/DEPLOYMENT-MODES-DECISIONS.md](history/DEPLOYMENT-MODES-DECISIONS.md)。

## 已知限制

- **Edge 設 `Pull` 但 Core 沒設 `Ingest:EdgeBaseUrl` 會靜默停擺**：Edge 不主動連線、
  Core 也不輪詢，webhook 照收進 outbox 但永遠不會落地。啟動驗證看不到另一台的設定
  （與 `Line:OutboundHere` 同一類跨主機組合問題），只能靠設定頁「主機狀態」的心跳
  逾期告警發現。設定 `Pull` 時務必同時設好 Core 端的 `Ingest:EdgeBaseUrl`。
- **`Pull` 模式下防火牆重新開通不會自動升級回推送**：`Pull` 是「明知這個方向封死」的
  宣告，Edge 不做任何探測。要自動升級請用預設的 `Auto`。
- **拉取方向的媒體暫存在記憶體**：Edge 重啟會遺失暫存中的內容，那幾筆會在下一輪 poll
  被重新派工、重新從 LINE 下載一次（LINE 內容 API 不冪等計費）。
- **拉取方向的名稱／頭貼結果沒有 ack**：poll 回應在傳輸中遺失時該筆結果會消失，
  要等 TTL（`ProfileCache:RefreshAfter`）再次過期才會重新刷新。訊息與媒體不受影響，
  那兩條流都有 ack。
- **outbox 死信沒有專用的重送介面**：收到 `PermanentIngestException` 的項目會被標記
  `DeadLetteredAt`、停止自動重試（暫時性失敗則永遠退避重試、不會死信），但資料留在
  `outbox.db` 裡不會自動消失，只能手動查 `LastError` 欄位後決定怎麼處理。
  `OutboxForwarderService` 每小時記一次目前死信筆數，量大時要考慮補一個管理介面。
- **`Line:OutboundHere` 設錯無法跨主機驗證**：一對拆機主機理論上恰好一台要設 `true`，
  但啟動驗證只能看到自己這台的設定，兩台都 `true`（重複下載，浪費 LINE 配額，但有
  唯一約束擋著不會產生髒資料）或兩台都 `false` 都不會啟動失敗。兩台都 `false` 的後果比
  「媒體永遠 `Pending`」更廣：貼圖與頭貼圖檔一樣不會下載，名稱也不會回填，前台看到的會是
  LINE 的原始 ID——很容易被誤判成檢視端壞掉。`DeploymentValidator` 會對
  `Core + OutboundHere=false` 記一則說明性 log 指出線索在 Edge 端，但最終仍只能靠
  部署檢查表把關。
- **待下載內容的回收最多延遲一個重掃週期**：`ContentDownloadService` 除了啟動時掃一次，
  之後每隔 `ContentDownload:RequeueIntervalMinutes`（預設 15 分鐘）重掃一次 `Pending` 與
  仍可重試的 `Failed`。Core 端補出但由 Edge 端下載的 `Pending` 項目最壞要等一個週期才會被撿回。
  `Downloading` 走**認領租約**（`ClaimedAt`／`ClaimedBy`，回收規則與啟動掃描條件見 README
  設定表的 `ContentDownload:ClaimLeaseMinutes`），別台主機或同站台兄弟行程正在下載中的內容
  不會被誤收。把間隔設為 0 會退回「只在啟動時掃一次」。
  ownerId 的粒度是站台，因此有兩個部署前提：**不可啟用 ASP.NET Core Module 的 shadow copy**
  （BaseDirectory 每次啟動都變，ownerId 會退回每行程一個，啟動掃描就認不出自己的孤兒）、
  **同機兩個站台不可共用同一個實體目錄**（會拿到相同 ownerId）。IIS 重疊回收與 Web Garden
  可以用，但要知道兩個效應：新行程的啟動掃描會把舊行程**進行中**的下載接手過來（那幾筆重
  下載一次；資料不會壞——SQL Server 是單一原子 INSERT、SQLite 的舊 blob handle 會因列被刪而
  失效，落後的一方寫入會被放棄並記 Warning）；極端情況下舊行程對同一筆的失敗標記可能落在
  新行程的認領上，多累計一次失敗後由 Failed 重試路徑補跑。啟動掃描另以「認領時間早於本行程
  啟動時刻」為條件，避免收走兄弟行程在本行程啟動之後才建立的認領（跨機時鐘不互信，所以用
  相對時間換算）。
- **單輪掃描有上限**：`ContentDownload:MaxPendingIdsPerScan`（預設 5000）限制一輪最多撈多少
  待處理內容，Id 小的先處理，其餘留給下一輪（被截斷時會記一筆 log，等級依
  `RequeueIntervalMinutes` 而定，見 README 設定表）。沒有上限時，
  積壓幾十萬筆會整包載入記憶體，而且 SQL Server 端會撞到 2100 個查詢參數的硬上限——
  積壓越嚴重越跑不動。
- **同一個角色不支援部署兩台**（例如為了 HA 開兩台 Core）：維護類背景工作靠「每個角色恰好
  一台」這個部署約定互斥，沒有資料庫層的租約。兩台 Core 會在同一個
  `Retention:CleanupTimeOfDay` 同時跑保留期清除；批次刪除本身冪等，但清完之後刷新
  `Groups.LastMessageId/At` 是「查 MAX → UPDATE」兩步，兩台交錯會把指標寫成互相覆蓋的舊值。
  要橫向擴充只該擴 Viewer（純讀）。
- **outbox 批次排空的吞吐量提升沒有正式量測**：Edge→Core 的 round-trip 從逐筆改成整批，
  理論上吞吐量會明顯提升，但目前只有功能面的等價性測試，沒有實際負載下的量測數據。
- **遮蔽規則快取在拆機部署下有最長 30 秒的漂移窗口**：`MaskingService` 把遮蔽設定與規則
  （顯示模式、關鍵字、別名、個資偵測開關）快取 30 秒在程序記憶體裡，`SettingsController`
  的寫入路徑會主動讓它失效。但那個失效只作用在**接到該次寫入的那個程序**——`Core` 與
  `Viewer` 分機部署時，在別台改的設定最長要 30 秒才會在其他台生效。`AllInOne` 只有一個
  程序，不受影響。這是拿一致性換高頻端點（訊息輪詢 3 秒、側欄 10 秒、每張頭貼各一次）
  的重複查詢，30 秒是刻意選的上限。
