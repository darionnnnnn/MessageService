# 部署角色：設計決策與驗證紀錄

> 本檔屬於 `docs/history/`（修改歷程／決策記錄），非必要不需要讀，避免浪費 token。
> 現行的四種角色定義、能力矩陣、設定鍵說明請看 [docs/DEPLOYMENT-MODES.md](../DEPLOYMENT-MODES.md)。

## 2026-08-13 部署收斂輪

原本收錄端（`MessageService`）與檢視端（`MessageService.Web`）是兩個各自發佈的專案，模式叫
`Full`／`Line`／`Db`。這輪把兩者合併成一個專案、Schema 管理改用 EF `Database.Migrate()`、
模式改名為 `AllInOne`／`Edge`／`Core`，並新增 `Viewer` 這個第四種角色支援三台拓撲。
舊名稱保留為列舉別名（`Full=AllInOne`／`Line=Edge`／`Db=Core`），既有
`appsettings.Production.json` 不用改也能繼續啟動。完整規劃與執行過程見
[CONSOLIDATION-PLAN.md](CONSOLIDATION-PLAN.md)。

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
- **合併後 Viewer 白名單與 Ingest 白名單拆成獨立 key**：合併前是兩個
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

## 原始建置分期（Stage 0～4，2026-08-12 之前完成；跟部署收斂輪是不同的分期編號）

| 階段 | 內容 | 狀態 |
|---|---|---|
| 0 | 模式列舉、設定、啟動驗證、路由閘門 | ✅ 已完成 |
| 1 | outbox＋forwarder＋`IIngestSink`／`DirectIngestSink` | ✅ 已完成 |
| 2 | ingest API controller＋`HttpIngestSink`＋死信 | ✅ 已完成，端到端驗證通過 |
| 3 | `IContentWorkSource`／`IProfileStore` 的 API 實作＋入列責任重構 | ✅ 已完成，端到端驗證通過（含抓到並修復真 bug） |
| 4 | blob 端到端串流、部署檢查表、設定樣板 | ✅ blob 串流已完成；部署檢查表與設定樣板由 2026-08-13 部署收斂輪的階段5接手完成 |
