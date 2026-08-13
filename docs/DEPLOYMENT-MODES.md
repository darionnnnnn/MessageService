# 部署角色（Deployment Modes）

`MessageService.Web` 是唯一的可發佈專案，同一份成品可以部署成一台包辦，也可以拆成兩三台，
由每台主機各自的 `Deployment:Mode` 設定決定角色——不是不同的部署產物，只是設定差異。

> **2026-08-13 部署收斂輪**：原本收錄端（`MessageService`）與檢視端（`MessageService.Web`）
> 是兩個各自發佈的專案，模式叫 `Full`／`Line`／`Db`。這輪把兩者合併成一個專案、
> Schema 管理改用 EF `Database.Migrate()`、模式改名為 `AllInOne`／`Edge`／`Core`，並新增
> `Viewer` 這個第四種角色支援三台拓撲。舊名稱保留為列舉別名（`Full=AllInOne`／
> `Line=Edge`／`Db=Core`），既有 `appsettings.Production.json` 不用改也能繼續啟動，
> 詳見下方「設定鍵升級對照」。合併與改名之前的建置歷史（本文件「端到端驗證紀錄」
> 與「原始建置分期」兩節）予以保留，作為這些保證最初怎麼被驗證出來的紀錄。

## 四種角色

| | AllInOne | Edge | Core | Viewer |
|---|---|---|---|---|
| `/api/line/webhook` | ✓ | ✓ | ✗（路由不存在，非拒絕） | ✗ |
| `/api/ingest/*` | 視 `Ingest:ApiKey` 而定 | ✗ | 視 `Ingest:ApiKey` 而定 | ✗ |
| 檢視端 UI＋API | ✓ | ✗ | ✓（可用 `Viewer:Enabled=false` 關掉，三台拓撲用） | ✓ |
| 直連主資料庫 | ✓ | ✗ | ✓ | ✓ |
| 本機 outbox＋排空 | ✓ | ✓ | ✗（無 webhook，無事件可寫） | ✗ |
| 保留期清除 | ✓ | ✗ | ✓ | ✗（即使直連資料庫也不跑，避免三台拓撲下跟 Core 搶著清同一張表） |
| `Line:OutboundHere` 預設 | `true` | `true` | `false` | `false` |
| 落地方式 | outbox → `DirectIngestSink` | outbox → `HttpIngestSink` → 對方的 `/api/ingest/events(-batch)` | `IngestController` → `DirectIngestSink` | 不適用 |

內容下載／頭貼快取完全獨立於模式，只看 `Line:OutboundHere`（`bool?`，未顯式設定時依模式
推導，見上表）——這台主機要不要對外呼叫 LINE API。有資料庫的一端用
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
| （無） | `Database:AutoMigrate`（`bool`，預設 `true`） | 新增，啟動時自動跑 `Database.Migrate()`；嚴管環境可關閉改手動 `dotnet ef database update` |

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
                                content-work／content 端點，                                  的 profiles 端點）
                                 X-Ingest-Key 在具名 HttpClient
                                 註冊時就設好，見設計決策）
```

webhook 回應時間因此跟資料庫或遠端 API 是否可用完全脫鉤：即使主資料庫短暫斷線，
AllInOne 模式的訊息也只是在 outbox 裡等，恢復後自動排空，不會遺失。

### 批次 ingest（問題9：拆機模式的 outbox 排空吞吐）

Edge 端排空 outbox 時預設一次 HTTP 請求送整批（`Outbox:BatchSize` 筆為單位），取代逐筆各自
一次 round-trip。Core 端依序落地、回傳逐筆結果（成功／永久拒絕各自標記）；批次中途遇到
**暫時性失敗**（例如連線中斷）視為整批這次沒處理完，直接讓 outbox 那批整體照退避排程重試——
已經成功的項目重送是安全的（`IIngestSink` 的冪等保證，見下方設計決策），不需要逐筆記錄
「處理到哪」；批次中途遇到**永久拒絕**（`PermanentIngestException`，例如某筆 payload 格式
不合）只影響那一筆，其餘照常處理並從 outbox 移除。

**升級順序：先升 Core 再升 Edge。** Edge 打到還沒升級、沒有 `/api/ingest/events-batch`
這支端點的舊版 Core 時會收到 404，自動退回逐筆模式（每次都照樣先試批次端點，Core 升級後
不用重啟 Edge 就會自動改用批次），只記一次警告 log 避免過渡期洗版。

## 設定

| 設定鍵 | 說明 |
|---|---|
| `Deployment:Mode` | `AllInOne`（預設）／`Edge`／`Core`／`Viewer`（舊名 `Full`／`Line`／`Db` 相容） |
| `ConnectionStrings:Outbox` | 本機 outbox 的 SQLite 檔（`AllInOne`／`Edge` 才用得到），預設 `Data Source=Db/outbox.db`（相對路徑以 ContentRootPath 為基準，第一次啟動自動建立目錄） |
| `Line:OutboundHere` | `bool?`，這台要不要對外呼叫 LINE API（媒體下載＋頭貼快取）。未設定時依模式推導（`AllInOne`／`Edge`＝`true`，`Core`／`Viewer`＝`false`）。決定 `ContentDownloadService`／`ProfileRefreshService` 會不會在這台主機啟動，以及 `IContentDownloadQueue`／`IProfileRefreshQueue` 是真的 Channel 還是 Null 實作。判定為 `true` 時必須同時設定 `Line:ChannelAccessToken`，否則啟動失敗 |
| `Line:ChannelAccessToken` | 呼叫 LINE content／profile API 要用的權杖。只有 `OutboundHere` 判定為 `true` 時才需要 |
| `Viewer:Enabled` | `bool?`，這台要不要開檢視端。未設定時依模式推導（`AllInOne`／`Core`／`Viewer`＝`true`，`Edge`＝`false`）。三台拓撲下 Core 端顯式設 `false` 把檢視端交給獨立的 Viewer 主機 |
| `Viewer:AllowedClientIps` | 檢視端頁面／API 的 IP 白名單，空白名單視為全拒 |
| `Ingest:BaseUrl` | Edge 模式打去哪個 Core 模式主機的 ingest API（如 `https://core-host/`） |
| `Ingest:ApiKey` | 雙邊共用密鑰：Edge 端當 `X-Ingest-Key` 標頭送出、Core／AllInOne 端驗證進來的請求，兩邊必須一致。**留空時 `/api/ingest/*` 整個不存在（404）**——避免單機部署意外多開一個沒人保護的寫入端點 |
| `Ingest:AllowedClientIps` | `/api/ingest/*` 的 IP 白名單（跟 `Viewer:AllowedClientIps` 是分開的兩份設定，語意不同），空白名單視為全拒 |
| `Ingest:MaxContentBytes` | `PUT /api/ingest/content/{id}` 單次上傳允許的最大位元組數，預設 300MB（Kestrel 預設請求上限 30MB 擋得住 LINE 的大檔，這裡動態放寬，見 `IngestController.UploadContent`；IIS 前面還有一層 `web.config` 的 `maxAllowedContentLength`，要跟這個值對齊） |
| `Outbox:PollIntervalSeconds` | outbox 空的時候的保底輪詢間隔（寫入會立刻叫醒，這只是撿回到期重試項目用），預設 5 |
| `Outbox:BatchSize` | 一輪最多處理幾筆（也是批次 ingest 一次請求送幾筆的上限），預設 50 |
| `Outbox:BaseRetryDelaySeconds` / `MaxRetryDelaySeconds` | 指數退避：第 N 次失敗延遲 `Base × 2^(N-1)`，封頂 `Max`，預設 5／300。**沒有累計次數上限**——暫時性失敗永遠重試，只有 `PermanentIngestException` 第一次遇到就直接標記死信 |
| `Database:Provider` | `Sqlite`／`SqlServer`，選填。未設定時依 `ConnectionStrings:SqlServer` 有沒有值推導（有→`SqlServer`，沒有→`Sqlite`），顯式設定永遠優先於推導（見 `DatabaseProviderResolver`） |
| `Database:SqliteFallback` | `bool`，預設 `true`，只在 `Deployment:Mode=AllInOne` 有效。有效 provider 為 `SqlServer` 時，啟動當下先探測連得上、schema 也對（`AutoMigrate` 開啟時一併驗證 schema），探測失敗就改用本機 SQLite 撐起服務並記一則 Error log；設 `false` 改成探測失敗就直接啟動失敗。決定只在啟動當下做一次，行程存續期間不變（不做執行中動態切換），見 `DatabaseStartupProbe`／`DatabaseStartupDecision` |
| `Database:AutoMigrate` | 啟動時是否自動跑 `Database.Migrate()`，預設 `true`。嚴管環境可關閉，改成手動在部署流程裡跑 `dotnet ef database update` |
| `Heartbeat:IntervalSeconds` | 每台主機回報存活狀態的間隔，預設 60。**所有主機要設成一致**——設定頁「主機狀態」的 Online/Delayed/Offline 門檻是以檢視端這台的設定為基準判斷（見 `SettingsController.ComputeStatus`），各主機間隔不同時燈號會用錯的基準 |

## 設計決策

- **為什麼合併成單一專案**：原本規劃是兩個各自發佈的專案（`MessageService` 收錄端 +
  `MessageService.Web` 檢視端），部署複雜度大部分來自「兩份成品、兩份 web.config、
  `Encryption:Key` 與 `Database:Provider` 兩邊要手動保持一致」，不是設定本身太多。合併成
  單一專案、單一發佈成品之後，這些「兩邊要一致」的約束從設計上直接消失（只有一份，不可能
  不一致），也順帶讓 `Full`（現 `AllInOne`）模式從「兩個行程共用同一顆 SQLite」變成單行程，
  跨行程鎖競爭與啟動順序約束一併消除。
- **只有 `IIngestSink` 有兩套實作**：webhook 收進來的路徑永遠只有「寫 outbox」一種。
  若改成每個資料庫操作（訊息、內容下載、頭貼快取）各自抽一層，雙實作的維護量會多出四五處。
- **防重送整個交給落地端**：`GroupMessages.WebhookEventId` 本來就有唯一索引
  （`MessageDbContext`），`DbUpdateException` 攔截也早就存在。`WebhookEventHandler`
  因此完全不需要任何資料庫讀取路徑——這是「webhook 只寫本機、不碰網路」能夠成立的前提，
  不然還是得先查一次資料庫才能決定要不要收。
- **outbox 用本機 SQLite，不是共用資料庫**：跟主資料庫（`MessageDbContext`）完全獨立、
  無論哪個模式都不共用，這是特意的——outbox 排空失敗不該卡住任何跟主資料庫有關的邏輯，
  反之亦然。因此 Schema 管理改用 `Database.Migrate()` 這輪也刻意不動 outbox.db，維持
  `EnsureCreated()`＋`OutboxSchemaUpgrader`：單表、單 provider、schema 極少變動，改制收益低。
- **`DbUpdateException` 用回查分辨「重複」與「暫時性失敗」**：撞鍵（真重複）要當成功讓 outbox
  刪掉該筆，儲存中途斷線／逾時要往外拋讓 outbox 重試——兩者都以 `DbUpdateException` 現身，
  一律當重複吞掉就會在暫時性失敗時把訊息弄丟，直接違反 outbox 的核心承諾。
- **路由閘門是「從 application model 移除 controller」，不是清空 Selectors**：清 Selectors
  會讓 action 被視為 conventional routing，與 `[ApiController]` 強制啟用的 ApiExplorer 衝突，
  host 啟動就丟例外——這是體檢時被真實 host 整合測試抓到的（單元測試看不到路由內部行為，
  所以 `DeploymentModeTests` 用 `WebApplicationFactory` 驗到「請求真的 404」為止）。合併專案
  時把這套機制從「依模式列舉」改成「依能力」（`RequiresCapabilityAttribute`）：能力可以被
  個別 override（例如 Core 模式關掉 Viewer），若直接寫死模式清單，override 後 controller
  的存在與否會跟能力的實際推導結果脫節。
- **`IngestApiEnabled` 同時涵蓋「模式是否允許」與「金鑰是否配置」，不再獨立成兩道閘門**：
  合併前 `ingestApiEnabled`（金鑰是否配置）純粹是「容器建好之前就要知道」的技術限制，
  不是刻意的防禦分層；折成一個之後兩者不會再有「改一邊忘了改另一邊」的機會。
- **`Line:OutboundHere` 而不是拆成「下載開關」＋「頭貼快取開關」**：媒體下載與頭貼快取
  都只需要 outbound HTTPS，沒有理由拆成兩個獨立設定；一對主機恰好一台要設 `true`，
  啟動時無法互相檢查，設錯（兩台都真或都假）不會啟動失敗，只會變成重複下載或永遠不下載。
- **入列（媒體下載／頭貼刷新）責任在呼叫端，不在 `DirectIngestSink`**：`DirectIngestSink`
  只管持久化、不碰任何佇列，`IngestController`（Core 端收到 Edge 轉來的請求）與
  `OutboxForwarderService`（本機排空）兩個呼叫端各自用自己 host 本地的
  `IContentDownloadQueue`／`IProfileRefreshQueue`，透過共用的 `IngestSideEffects.Apply`
  靜態方法決定要不要接手——這台主機的佇列是真的還是 Null，呼叫端完全不用知道。
- **`ContentId` 值得為此擴充 `IIngestSink` 契約**：`ContentId` 是**功能上必需**——沒有它，
  拆機模式的媒體永遠不會知道要下載哪一筆。重複情境也必須回傳既有那筆的 `ContentId`
  （不能回 `null`），否則 outbox 重試（代表前一次的回應可能遺失了）會讓那筆媒體卡到
  下次服務重啟的啟動重撈才補回。
- **`ApiContentWorkSource`／`ApiProfileStore` 忘記帶 `X-Ingest-Key`，只有真雙行程互打才測出來**：
  改成在 `Program.cs` 註冊具名 `HttpClient` 時就把 `X-Ingest-Key` 設成預設標頭，一次到位，
  不必要求每個方法自己記得加；並補一條整合測試直接從 DI 解析具名 client 檢查標頭在，
  防止回歸。**教訓：純文字打字看不出「這段程式碼會不會被兩個獨立行程執行」，這種缺口
  只有真的跑兩個行程互打才測得出來**——這在收發解耦與部署收斂兩輪各發生過一次。
- **等價性測試與其他真實 host 整合測試不能用預設的 Development 環境**：`WebApplicationFactory`
  預設環境是 Development，會自動載入 `dotnet user-secrets`——這台開發機的 user-secrets
  存了一把真的 LINE Channel Access Token，會讓 `OutboundHere` 判定為 `true` 卻沒設
  `ChannelAccessToken` 的啟動驗證規則被意外滿足，在**這台機器**「湊巧通過」，換一台乾淨
  機器或 CI 就會炸。所有這類測試改成 `builder.UseEnvironment("Testing")`，讓
  `appsettings.json`（值都是空字串或類別預設）成為唯一基底，不受任何本機殘留設定影響。
- **ingest API 判定「重複」不回獨立狀態碼，一律 200**：對呼叫端而言「新寫入」與「判定為
  重複」都是「這筆已經在後端了，outbox 可以刪掉」，沒有行為上的差異，純觀察用途不值得
  為此打破一個已穩定的契約。
- **合併後 Viewer 白名單與 Ingest 白名單拆成獨立 key（見上方升級對照）**：合併前是兩個
  各自 `appsettings.json` 裡的同名 key，互不影響；合併成單一設定檔後，如果還共用同一個
  key，會被迫套用同一份清單——這在真實拆機拓撲下是錯的（office LAN 不該同時也是 ingest
  的允許來源）。
- **IP 白名單解析失敗改成啟動丟例外，不再無聲略過**：`.NET` 的 `IPNetwork.TryParse` 要求
  CIDR 主機位元全為 0，`10.1.0.5/24` 這種常見打字習慣會 parse 失敗——舊版直接把那條規則
  丟掉，使用者被 403 之後 log 只會說「not in AllowedClientIps」，完全查不出是設定寫錯。
  跟「空白名單全拒」的寧嚴勿鬆哲學一致，改成解析失敗直接擋啟動，訊息附上錯誤條目原文與
  正確寫法範例。

## 端到端驗證紀錄（收發解耦建置期，2026-08-12，早於部署收斂輪）

兩輪都用真實本機雙行程驗證（非模擬、非單元測試 mock）——這是收發解耦這個功能本身唯一能
真正證明「拆機版本跟單機版本一模一樣」的方式，過程中也各抓到一個自動化測試沒抓到的真 bug。
下文用當時的模式名稱（`Db`／`Line`），對應現在的 `Core`／`Edge`。

### 純文字訊息收送

`Db`（`Core`）模式（`localhost:5081`，接資料庫，設 `Ingest:ApiKey`＋`AllowedClientIps`）與
`Line`（`Edge`）模式（`localhost:5082`，`Ingest:BaseUrl` 指向前者）。用正確 HMAC-SHA256 簽章送
真實格式的 LINE webhook payload：

1. **正常路徑**：Edge 端驗簽通過 → 寫本機 outbox → 回 200 → forwarder 排空 → `HttpIngestSink`
   打 Core 端 `/api/ingest/events` → 中介層放行 → `IngestController` → `DirectIngestSink`
   寫入。直接查 Core 端 SQLite 確認訊息內容、`GroupId`、`UserId`、`LineMessageId`、
   `WebhookEventId` 全部正確落地。
2. **斷線容忍**：中途 `kill` 掉 Core 實體，Edge 端再收一則 webhook——**webhook 仍回 200**
   （沒有因為後端不通就讓 LINE 判定失效重送），訊息留在 Edge 端本機 outbox（`Attempts` 遞增、
   `LastError` 記錄連線失敗訊息），資料沒有遺失。
3. **自動恢復**：重啟 Core 實體後，Edge 端的 `OutboxForwarderService` 在下一輪退避到期時
   自動重試成功，outbox 排空回 0 筆，兩則訊息（含斷線期間那則）都正確落地、無重複列——
   驗證了 `WebhookEventId` 唯一索引在拆機場景下確實擋住了 outbox 重試可能產生的重複寫入。

### 媒體下載的完整 wiring

同樣兩個實體，這次 Core 端設 `OutboundHere=false`、Edge 端設 `OutboundHere=true`
（含一個假的 `ChannelAccessToken`——沒有真實 LINE 憑證能完成真正下載，但足以驗證
除了「打 LINE API 本身」之外的每一段 wiring）：

1. **第一次跑就抓到真 bug**：Edge 端啟動時 `ContentDownloadService.RequeuePendingAsync`
   立刻收到 401——`ApiContentWorkSource` 打 Core 端的請求完全沒帶 `X-Ingest-Key`。
   修好後（在具名 `HttpClient` 註冊時設定預設標頭）重新驗證：
2. **正常路徑（含媒體）**：送一則圖片 webhook → Edge 端寫 outbox → 轉送到 Core 端
   → `DirectIngestSink` 寫入 `GroupMessage`＋`MessageContent`（`Pending`）→ `ContentId`
   透過 HTTP 回應帶回 Edge 端 → `IngestSideEffects.Apply` 用 Edge 端**自己的**
   （因為 `OutboundHere=true` 而是真的）`IContentDownloadQueue` 入列 → Edge 端
   `ContentDownloadService.ProcessAsync` 撿起工作 → `ApiContentWorkSource.GetAsync`
   正確認證、成功取回 `ContentWorkItem` → 嘗試下載（假憑證，預期對真實 LINE API 404）
   → 重試耗盡後 `ApiContentWorkSource.FailAsync` 呼叫 Core 端
   `/api/ingest/content/{id}/failed` → 直接查 Core 端 SQLite 確認
   `MessageContent.DownloadStatus` 正確變成 `Failed`。

## 目前限制（誠實記錄，別誤以為做完了）

- **outbox 死信沒有專用的重送介面**：收到 `PermanentIngestException` 的項目會被標記
  `DeadLetteredAt`、停止自動重試（暫時性失敗則永遠退避重試、不會死信），但資料留在
  `outbox.db` 裡不會自動消失，只能手動查 `LastError` 欄位後決定怎麼處理。
  `OutboxForwarderService` 每小時記一次目前死信筆數，量大時要考慮補一個管理介面。
- **`Line:OutboundHere` 設錯無法跨主機驗證**：一對拆機主機理論上恰好一台要設 `true`，
  但啟動驗證只能看到自己這台的設定，兩台都 `true`（重複下載，浪費 LINE 配額，但有
  唯一約束擋著不會產生髒資料）或兩台都 `false`（媒體永遠 `Pending`）都不會啟動失敗，
  只能靠部署檢查表把關。
- **outbox 批次排空的吞吐量提升沒有正式量測**：問題9的修復把 Edge→Core 的 round-trip
  從逐筆改成整批，理論上吞吐量會明顯提升，但目前只有功能面的等價性測試，沒有實際負載
  下的量測數據。

## 原始建置分期（Stage 0～4，2026-08-12 之前完成；跟本文件開頭的部署收斂輪是不同的分期編號）

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 模式列舉、設定、啟動驗證、路由閘門 | ✅ 已完成 |
| 1 | outbox＋forwarder＋`IIngestSink`／`DirectIngestSink` | ✅ 已完成 |
| 2 | ingest API controller＋`HttpIngestSink`＋死信 | ✅ 已完成，端到端驗證通過 |
| 3 | `IContentWorkSource`／`IProfileStore` 的 API 實作＋入列責任重構 | ✅ 已完成，端到端驗證通過（含抓到並修復真 bug） |
| 4 | blob 端到端串流、部署檢查表、設定樣板 | ✅ blob 串流已完成；部署檢查表與設定樣板由 2026-08-13 部署收斂輪的階段5接手完成 |
