# 部署收斂與體檢回饋實作規劃（CONSOLIDATION-PLAN）

> 狀態：規劃完成、未實作。2026-08-13 依外部體檢回饋（九項問題）＋使用者需求（第三節四項）逐條
> 對原始碼查證後產出。查證結論：九項全部屬實。
>
> 已定案的四個方向（使用者 2026-08-13 決定）：
> 1. 合併成單一專案，**以 `MessageService.Web` 為存續主體**，收錄端 `MessageService` 專案退場。
> 2. 部署模式**支援到三台主機**（LINE 端／DB+API 端／純檢視端）。
> 3. Migrations **每個 provider 一套**（SQLite／SQL Server 分開產生與維護）。
> 4. 範圍：**部署收斂＋效能與行為修正全部一輪做完**（問題 1~9 與 B1~B5 皆納入本輪）。
> 5. 機密與各主機的實際運作設定**全部放伺服器上的 `appsettings.Production.json`**（不進版控、
>    不在發佈成品內），web.config 不再承載環境變數機密；版控只放樣板（B5 的分界修訂）。
>
> 分支流程照舊：feature branch → 併 dev 實測 → 確認無誤才併 master。

---

## 執行進度

- **階段1（專案合併）已完成並 commit**（`801b728`，分支 `feature/deployment-consolidation`）。
  全 solution 建置 0 警告 0 錯誤，447 測試全綠。
  實作期間相對本文件原稿的兩個調整：
  1. **AllowedClientIps 拆分提前到階段1執行**（原規劃放階段2）：規劃階段沒注意到合併成單一
     `appsettings.json` 後，若檢視端與 ingest 白名單中介層還讀同一個 key，兩種網段完全不同的
     白名單會被迫共用同一份清單——這在分離部署（Edge+Core 合一）下是真的安全問題，不是
     「反正還沒到那步」可以延後的瑕疵，所以趁合併中介層順手拆成
     `Viewer:AllowedClientIps`／`Ingest:AllowedClientIps`。階段2不用再處理這件事。
  2. **發現一個合併的結構性副作用**：`MapStaticAssets()`／`.WithStaticAssets()` 的靜態資源後援
     endpoint（`{**path:file}`，只接受 GET/HEAD）合併後與 ingest／webhook 路由共用同一個
     endpoint routing 表。在 viewerEnabled 的模式下，對「模式排除掉的路由」送出非 GET/HEAD
     請求（例如 Db 模式 POST `/api/line/webhook`），會被路由層判定為「路徑有東西 match、方法
     不對」而回 405，不再是純粹的 404。已確認這只影響 webhook 路徑本身（`/api/ingest/*` 有
     `IngestApiKeyMiddleware` 在金鑰未設定時的顯式 404 短路，不受影響），且 405 與 404
     對「webhook controller 沒被路由到」這件事的驗證強度等價（都不是 401）。調整了對應測試
     的斷言並在測試內加註解，未動 pipeline 設計。

## 零、查證結果對照表（回饋 vs 原始碼）

| # | 回饋主張 | 查證 | 出處 |
|---|---|---|---|
| 1 | IIS 集區回收殺掉四個 BackgroundService，文件沒提 | ✅ 屬實 | `RetentionCleanupService.GetDelayUntilNextRun()` 用 Task.Delay 等 03:00；DEPLOYMENT-GUIDE.md D2 節只教建集區、無 AlwaysRunning/閒置逾時/回收設定 |
| 2 | `Line:OutboundHere` 預設 true 與文件表格（Line/Db 預設 ✗）相反 | ✅ 屬實 | `LineOptions.cs:14`、`appsettings.json`；後果鏈經 `DeploymentValidator.cs:36` 驗證：照文件設 Mode=Db 會被擋啟動、照錯誤訊息補 token 會兩台重複下載 |
| 3 | `MessageDbSchemaUpgrader` 漏 `StickerId`/`PackageId` 與 `AnonymousIdentities` | ✅ 屬實 | upgrader 只補 SchemaHardeningRound1 批次（5 欄＋2 索引）；migration 20260804062907 與 20260730010058 的變更不在其中；README:269 只自首 AnonymousIdentities |
| 4 | `/api/groups` 全表 GroupBy＋未讀 N+1 | ✅ 屬實 | `GroupsController.cs:23`（無 WHERE）、`:44`（逐群組 CountAsync）；每分頁 10 秒輪詢一次 |
| 5 | 轉檔等待佔住 worker，3 支影片停擺整條下載線 | ✅ 屬實 | `MaxConcurrency` 預設 3；`WaitForTranscodingAsync` 每支最多 24×5=120 秒同步等待 |
| 6 | `aroundId` 排序非 sargable | ✅ 屬實 | `MessagesController.cs:106` `OrderBy(Math.Abs(m.Id - anchorId))` |
| 7 | IP 白名單解析失敗靜默丟棄 | ✅ 屬實 | 兩份複本（`IpAllowlistMiddleware.cs:52`、`IngestIpAllowlistMiddleware`）皆是 `TryParse` 失敗即 continue、無 log |
| 8 | 收錄端無條件 `UseHttpsRedirection` | ✅ 屬實 | 收錄端 `Program.cs:231`（Web 端 `Program.cs:70` 也是，但 viewer 面向人、風險低） |
| 9 | Outbox 逐筆排空吞吐 20~30 筆/秒 | ✅ 屬實 | `OutboxForwarderService.ProcessBatchAsync` 逐筆 `SubmitAsync`＋逐筆 `SaveChangesAsync`；量級估算合理 |

規劃期間另外發現、回饋沒提的事實：

- **合併的額外紅利**：現況 Full 模式是收錄端與 Web 端兩個行程跨行程共用同一顆 SQLite 檔，
  合併後變單行程，跨行程鎖競爭與「先啟動收錄端再啟動檢視端」的順序約束整個消失。
- **migrations 是對 SQL Server 產生的**（`nvarchar(max)`/`bit`/`datetimeoffset`/`AlterColumn`），
  「兩 provider 共用同一套」並非理所當然 → 已定案改為每 provider 一套。
- **`DirectIngestSink` 不建 Groups 列**（Groups 是頭貼快取那條路寫的，側欄靠 `cached?.GroupName ?? id`
  容錯）→ 問題 4 的反正規化必須在落地時 upsert Groups stub 列，見階段 4。
- `web.config` 不在版控內（B4 屬實）；`maxAllowedContentLength` 只寫在 GUIDE E1 節文件裡。
- 使用者第三節第 2 點（log 寫到 logs/）**現況已達成**：兩份 nlog.config 都是
  `${basedir}/logs/...-${shortdate}.log`，只差文件補「logs 資料夾要給集區帳號寫入權限」。

---

## 一、目標終態（一句話版）

**一個可發佈專案 `MessageService.Web`、一份發佈成品**；每台主機的部署動作 =
解壓成品 → 複製對應拓撲的樣板為 `appsettings.Production.json` 並填齊 key（含機密）→
跑一段集區設定指令稿。模式由 `Deployment:Mode` 一個 key 決定，schema 由 `Database.Migrate()`
自動維護，背景服務在 IIS 常駐設定下全天可靠執行。

### 模式定義（四個預設集＋兩個 override）

| 能力 | AllInOne | Edge | Core | Viewer |
|---|---|---|---|---|
| `/api/line/webhook`（webhook＋outbox） | ✓ | ✓ | ✗ | ✗ |
| `/api/ingest/*` | ✗ | ✗ | ✓ | ✗ |
| 檢視端 UI＋viewer API | ✓ | ✗ | ✓（可 override 關） | ✓ |
| 直連主資料庫 | ✓ | ✗ | ✓ | ✓ |
| 保留期清除 | ✓ | ✗ | ✓ | ✗ |
| `Line:OutboundHere` 預設 | **true** | **true** | **false** | **false** |

- 舊 `Full`/`Line`/`Db` → 新 `AllInOne`/`Edge`/`Core`。列舉值改名，**保留舊名為別名**
  （`JsonStringEnumConverter` 前先自行正規化字串，或列舉同值雙名），啟動時讀到舊名記一行
  Warning 提示改名，不擋啟動——避免既有部署升級即炸。
- Override 只有兩個：
  - `Line:OutboundHere` 改型別為 `bool?`：null＝依 Mode 推導（上表）；顯式設定優先。
    `Core`/`Viewer` 被顯式設成 true 時記 Warning「請確認 Edge 端已設 false，否則重複下載」。
  - `Viewer:Enabled`（新增，`bool?`）：null＝依 Mode 推導；三台拓撲時 Core 顯式設 false。
- 拓撲對應：一台＝AllInOne；兩台＝Edge＋Core；三台＝Edge＋Core(`Viewer:Enabled=false`)＋Viewer。
- `DeploymentValidator` 規則重寫：
  - Edge：必填 `Ingest:BaseUrl`＋`Ingest:ApiKey`＋`Line:ChannelSecret`。
  - Core：必填 `Ingest:ApiKey`；`Viewer` 啟用時建議設 `Viewer:AllowedClientIps`（空清單維持
    「全拒＋啟動 Warning」現行為）。
  - Viewer：不需要任何 Line/Ingest 設定；若設了記 Warning（可能放錯樣板）。
  - AllInOne：必填 `Line:ChannelSecret`；推導後 OutboundHere=true 時必填 `ChannelAccessToken`（現有規則保留）。
  - 保留期清除只在 AllInOne/Core 註冊——**三台拓撲下恰好一台（Core）跑清除**，Viewer 不跑，
    避免多實例併發清除。

### 設定 key 異動總表（breaking changes，文件要列升級對照）

| 舊 | 新 | 說明 |
|---|---|---|
| `AllowedClientIps`（收錄端＝ingest 白名單） | `Ingest:AllowedClientIps` | 一鍵兩義拆開 |
| `AllowedClientIps`（Web 端＝viewer 白名單） | `Viewer:AllowedClientIps` | 同上 |
| `Deployment:Mode` = Full/Line/Db | AllInOne/Edge/Core（＋新 Viewer） | 舊名為相容別名 |
| `Line:OutboundHere` 必填布林 | `bool?`，預設依 Mode 推導 | 消除文件與程式相反的預設值 |
| （無） | `Viewer:Enabled`（`bool?`） | 三台拓撲用 |
| （無） | `Http:UseHttpsRedirection`（bool，**預設 false**） | 問題 8；IIS 綁 HTTPS 時不需要應用層轉址 |
| `UseForwardedHeaders`（僅 Web 端有） | 保留同名，合併後全站生效 | 放白名單之前 |
| `Database:AutoMigrate`（新，bool，預設 true） | — | 關掉則啟動只驗證不遷移（給嚴管環境手動跑） |

---

## 二、實作階段

原則：每個階段結束時**全部測試綠、可獨立 commit**；階段 1 是純機械搬移不改行為，
行為變更集中在階段 2 之後，讓 review 可分層。

### 階段 1：專案合併（機械搬移，行為不變）

1. 收錄端 `MessageService` 專案的 `Controllers/`、`Middleware/`、`Options/`、`Outbox/`、
   `Services/`、`nlog.config` 搬入 `MessageService.Web`，**保留原命名空間**
   （`MessageService.Services` 等）以最小化 diff；solution 移除 `MessageService.csproj`。
2. `MessageService.Web.csproj` 補上收錄端的套件參照（`Swashbuckle.AspNetCore`、
   `Microsoft.EntityFrameworkCore.Design`）；`UserSecretsId` 沿用收錄端那組（本機開發
   secrets 不失效）。
3. **Program.cs 合併**（本階段暫時維持三模式語意，viewer 掛載條件＝`hasDatabaseAccess`）：
   - 服務註冊：收錄端的條件註冊矩陣原樣搬入；Web 端的 `ContentStreamService`/
     `MaskingService`/`AnonymousIdentityService`/`AddControllersWithViews` 在 viewer 啟用時註冊。
   - `MessageDbContext` 註冊收斂成一處（原本兩個 Program 各註冊一次）。
   - Pipeline 順序（定案，體檢時逐項核對）：
     1. `UseForwardedHeaders`（設定開啟時）
     2. viewer 白名單：`UseWhen(path 不是 /api/line 且不是 /api/ingest 且 viewer 啟用, IpAllowlistMiddleware)`
     3. `CancelledRequestMiddleware`（包住 viewer 與 API 全部）
     4. dev：Swagger；prod：`UseExceptionHandler("/Home/Error")`＋`UseHsts`（viewer 啟用時）
     5. `UseHttpsRedirection`（階段 2 改為設定開關，本階段先原樣保留）
     6. ingest 管線：`UseWhen(/api/ingest, IngestIpAllowlist + IngestApiKey)`（沿用現況，只在
        hasDatabaseAccess 時掛）
     7. `UseRouting`/`UseAuthorization`/`MapStaticAssets`＋`MapControllers`＋
        default route（viewer 啟用時才 Map default route 與 StaticAssets）
   - webhook（`/api/line/webhook`）與 ingest（`/api/ingest`）**永遠不經過 viewer 白名單**——
     webhook 靠簽章、ingest 靠自己的白名單＋金鑰，維持現有三層互不干涉的設計。
4. `DeploymentModeConvention` 擴充：viewer 的五支 controller（Groups/Messages/Settings/Users/Home）
   加 `[EnabledInModes(Full, Db)]`（階段 2 改成 capability 制）——**合併後 Line 模式絕不暴露
   viewer API**，這是合併最大的安全注意點。
5. nlog 合一：單一 `nlog.config`，target 檔名 `messageservice-${shortdate}.log`；
   `${basedir}/logs` 寫法保留。
6. 兩個 IP 白名單 middleware 複本合一：單一類別，建構子參數化（設定 section 名、log 字樣），
   本階段兩處仍讀舊 key `AllowedClientIps`（key 拆分放階段 2）。
7. 測試合併：`MessageService.Tests` 的檔案搬入 `MessageService.Web.Tests`（保留命名空間與
   目錄結構），solution 移除舊測試專案。**重點驗證**：
   - 兩套 `WebApplicationFactory` fixture 現在指向同一個 `Program`——逐一檢查 in-memory
     設定覆蓋是否完整（上一輪踩過「factory 撿到本機真密鑰導致測試假通過」，這次合併是同款
     地雷的高發區：收錄端 fixture 沒覆蓋 viewer 的 key、viewer fixture 沒覆蓋 Line 的 key）。
   - `DeploymentModeTests` 補「Line 模式下 viewer API/首頁 404」「Db/Full 模式下 viewer 正常」案例。
8. 收錄端專案目錄下的 `messages.db`/`outbox.db`/`logs/` 等本機工作檔的 .gitignore 狀態確認。

**退場明細**：`MessageService.csproj`、收錄端 `appsettings*.json`（內容併入 Web 端）、
收錄端 `nlog.config`、`MessageService.http`、`IngestIpAllowlistMiddleware`（併入共用類別）。

### 階段 2：模式重定義（AllInOne/Edge/Core/Viewer＋能力推導）

1. `DeploymentOptions`：列舉改名＋舊名別名＋新增 `Viewer` 值；新增 `DeploymentCapabilities`
   純函式類別，輸入（Mode, LineOptions, ViewerOptions, IngestOptions）輸出六個布林：
   `ReceivesWebhook`/`HasDatabaseAccess`/`IngestApiEnabled`/`ViewerEnabled`/`OutboundHere`/
   `RunsRetention`。Program.cs 與 Convention 全部改吃這個物件，**推導邏輯單點化、可單元測試**
   （吸取 LatestActivity「改共用欄位漏改讀取端」的教訓）。
2. `LineOptions.OutboundHere` 改 `bool?`；`ViewerOptions` 新增（`Enabled: bool?`、
   `AllowedClientIps: string[]`）；`IngestOptions` 新增 `AllowedClientIps`。
3. `EnabledInModesAttribute` 改為能力制 `[RequiresCapability(Capability.Viewer)]` 之類
   （viewer 五支掛 Viewer、IngestController 掛 IngestApi、LineWebhookController 掛 Webhook），
   Convention 依 capabilities 移除 controller。
4. `DeploymentValidator` 依第一節規則重寫＋測試全面翻新。
5. 白名單 key 拆分（`Viewer:AllowedClientIps`/`Ingest:AllowedClientIps`）；**啟動時讀到舊 key
   `AllowedClientIps` 非空就直接丟例外**，訊息寫明新 key 名——寧可啟動失敗也不要舊設定被
   靜默忽略後白名單形同虛設。
6. 問題 7：白名單條目解析失敗改成**啟動丟例外**（訊息含條目原文與正確寫法範例，
   如 `10.1.0.5/24 主機位元須為 0，請改 10.1.0.0/24 或 10.1.0.5/32`）。與「空清單全拒」的
   寧嚴勿鬆哲學一致。
7. 問題 8：`UseHttpsRedirection` 改由 `Http:UseHttpsRedirection` 控制，預設 false；
   GUIDE 註明「IIS 直接綁 HTTPS 者維持 false；沒有前端 TLS 的裸 Kestrel 才開」。
8. 問題 2 收尾：DEPLOYMENT-MODES.md 表格改為推導後的真實預設值。

### 階段 3：Schema 管理改 `Database.Migrate()`（每 provider 一套）

1. `MessageService.Data` 內建立兩個衍生 context：`SqliteMessageDbContext`/
   `SqlServerMessageDbContext`（空殼，只為讓 EF 區分 migrations 集），各配一個
   `IDesignTimeDbContextFactory`；migrations 目錄 `Data/Migrations/SqlServer/`（既有七個
   migration 搬入，**migration Id 不變**，只動 namespace——history 比對靠 Id 不受影響）與
   `Data/Migrations/Sqlite/`（重新 `dotnet ef migrations add InitialCreate` 對 Sqlite 產生，
   單一顆 InitialCreate 即可，內含完整現行 model）。
2. DI：依 provider 註冊對應衍生 context 為 `MessageDbContext`
   （`AddDbContext<MessageDbContext, SqliteMessageDbContext>` 形式），全站既有注入點不動。
3. 啟動流程（`HasDatabaseAccess` 時）：
   - `Database:AutoMigrate`（預設 true）→ `dbContext.Database.Migrate()`，SQLite 與
     SqlServer 同路徑。
   - **多實例防競態**：Migrate 前取具名 Mutex（`Global\MessageService.Migrate`）；SQL Server
     另有 migration 本身的鎖，這層主要防同機多站台同時啟動。
   - **既有 SQLite 檔 baseline**（一次性橋接，新類別 `LegacySqliteBaseliner`）：
     偵測「檔案存在＋有 GroupMessages 表＋無 `__EFMigrationsHistory`」→
     (a) 跑舊 upgrader 的 5 欄＋2 索引補齊、(b) 補 `StickerId`/`PackageId` 欄（問題 3 的漏洞）、
     (c) 建 `AnonymousIdentities` 表（若缺）、(d) 寫入 Sqlite InitialCreate 的 history 紀錄，
     之後 `Migrate()` 自然 no-op。既有部署升級**不需要刪檔**。
   - 全新檔案：直接 `Migrate()` 建全 schema，`EnsureCreated()` 與 `MessageDbSchemaUpgrader`
     從 messages.db 路徑退役（upgrader 邏輯被 Baseliner 吸收後刪除原類別與其測試，
     Baseliner 補新測試：舊檔升級後 schema 與全新 Migrate 檔逐表逐欄一致）。
4. `outbox.db` **維持現狀**（EnsureCreated＋OutboxSchemaUpgrader）：單表、單 provider、
   schema 極少動，改制收益低；在 code comment 註明這個取捨。
5. 正式 SQL Server：照文件用 `dotnet ef database update` 建的庫已有 history 表，`Migrate()`
   直接接手；GUIDE 移除「伺服器上跑 dotnet ef」步驟（改為可選的嚴管環境做法，配
   `AutoMigrate=false`）。
6. 測試：兩個 provider 各一組「空庫跑完整 migrations → 與 EF model 比對無 pending changes」
   的守門測試（`Database.GetPendingMigrations()`＋model diff），防止日後「改了 model 忘了
   add migration」或「只加了一邊 provider」。

### 階段 4：效能與行為修正（問題 4/5/6/9）

**4a. 側欄反正規化（問題 4）**

1. `Groups` 表加 `LastMessageId (bigint null)`/`LastMessageAt (datetimeoffset null)`——
   兩套 migrations 各加一個 migration；migration 內含回填 SQL（一次性
   `UPDATE Groups SET ... FROM (SELECT GroupId, MAX(Id) ...)`）。
2. `DirectIngestSink` 落地訊息時 upsert Groups：**列不存在就插 stub 列**（GroupId＋
   LastMessageId/At，GroupName 留 null 讓頭貼快取之後補），存在就更新兩欄。與頭貼快取的
   寫入路徑（DbProfileStore 那條）併發時以 GroupId 主鍵衝突處理（catch duplicate → 改 update），
   比照既有「並發重複寄」教訓補併發測試。
3. `GroupsController` 改讀 `Groups`（幾十列的小表）：
   - 側欄清單、最後訊息預覽（用 LastMessageId 撈單列）、成員數維持現查詢。
   - 未讀數維持逐群組 COUNT（有 baseline 且 LastMessageId > baseline 的群組才查，
     上限 UnreadCap 截斷）——N+1 的 N 從「所有群組」縮成「真有未讀的群組」，且每查有
     `(GroupId, Id)` 索引；全表 GroupBy 從此消失。
   - LastMessageId 指向已被清除訊息時沿用現有「跳過該群組、下輪重算」容錯：撈不到預覽列
     就 fallback 查一次該群組 MAX(Id) 並順手修正 Groups 列。
4. `RetentionCleanupService` 清完後，對「LastMessageAt < cutoff」的群組重算 LastMessageId/At
   （群組數量級小，逐群組一次 MAX 查詢可接受）；整群組訊息清空時兩欄設 null。

**4b. 轉檔等待改延遲重排（問題 5）**

1. `WaitForTranscodingAsync` 廢除迴圈等待：查一次狀態，`Succeeded` → 續跑下載；`Failed` → Fail；
   `Processing` → **把 contentId 延遲重新入列後立刻 return**，worker 馬上服務下一個項目。
2. 延遲機制：`ContentDownloadQueue` 加 `EnqueueDelayed(id, TimeSpan)`——實作為
   `Task.Delay(delay, stoppingToken).ContinueWith(enqueue)` 的受控背景排程（統一收在 queue 類別
   內、掛服務停機 token，避免裸 fire-and-forget 吞例外；比照本專案對背景例外的既有紀律）。
3. 輪詢次數上限：in-memory `ConcurrentDictionary<long,int>` 記每個 contentId 的轉檔查詢次數，
   達 `TranscodingMaxPolls` 即 Fail 並清項；行程重啟計數歸零可接受（重啟後由
   `RequeuePendingAsync` 撈回，等同重新給滿額度，語意與現狀相同）。完成/Fail 時務必移除
   dictionary 項（防洩漏）。
4. 測試：三支影片＋一張圖片並發时，圖片不被卡（現有 `ContentDownloadServiceConcurrencyTests`
   延伸）；輪詢上限；重啟接續。

**4c. `aroundId` 改雙段查詢（問題 6）**

1. 錨點兩側各查一次：`Id <= anchor` 降冪 take (半窗+1)、`Id > anchor` 升冪 take (半窗+1)，
   合併後依 Id 排序輸出；`truncated` 判斷改為「任一側超出半窗」。兩段都走 `(GroupId, Id)` 索引。
2. 語意差異記入 code comment：截斷時從「全域最近 500 則」變「每側最多 250 則」，錨點極端
   靠近視窗邊緣時另一側不會借額度——對搜尋跳轉場景無感，換到的是索引可用。
3. 既有 `MessageSearchTests`/`MessagesControllerTests` 的 aroundId 案例全數重跑＋補
   「錨點在視窗邊緣」「單側訊息不足」案例。

**4d. Outbox 批次排空（問題 9）**

1. 新增 ingest 批次端點 `POST /api/ingest/events:batch`（或 `/events-batch`）：一個請求帶最多
   `Outbox:BatchSize` 個 envelope，Core 端**單一交易、依序落地**，回傳逐筆結果（含每筆
   ContentId／permanent-reject 標記）。順序在單請求內保證——**不採用並行 forwarder**，
   因為 viewer 依 Id 排序顯示，並行落地會打亂同群組訊息順序（本專案在郵件迴圈已為保序
   踩過一次）。
2. `HttpIngestSink` 加 `SubmitBatchAsync`；`OutboxForwarderService.ProcessBatchAsync` 改為
   一次送整批、依回傳逐筆決定刪除/退避/死信，outbox 端一次 `SaveChangesAsync`。
   `DirectIngestSink`（AllInOne）維持逐筆語意不變（本來就沒有 RTT 問題），介面用
   default interface method 或 forwarder 依 sink 能力分流。
3. 相容性：Edge 新版打到舊版 Core（無批次端點）→ 404 fallback 回逐筆模式並記一次 Warning；
   升級順序文件寫明「先升 Core 再升 Edge」。
4. 冪等：批次內單筆重送靠既有唯一索引去重（與現行逐筆行為一致），測試補「批次中間一筆
   permanent-reject、其餘照常」案例。

### 階段 5：部署載具

1. **`web.config` 樣板進版控**（`MessageService.Web/web.config`，publish 自動合併）——
   只承載**非機密的宿主設定**，不再放環境變數機密：
   - `requestLimits maxAllowedContentLength="314572800"`（對齊 `Ingest:MaxContentBytes`，
     並在兩處旁邊互相註記「改一邊要改另一邊」）。
   - `<aspNetCore>` 只留 `ASPNETCORE_ENVIRONMENT=Production` 一個環境變數（讓 host 載入
     `appsettings.Production.json`）與 `hostingModel="inprocess"`。
2. **機密與運作設定全放 `appsettings.Production.json`**（B5 修訂版）：
   - 每台主機的實際設定（Mode、白名單、`Line__ChannelSecret`/`ChannelAccessToken`、
     連線字串、`Ingest:ApiKey`、`Encryption:Key`）集中在站台目錄的
     `appsettings.Production.json` 一份檔案，**不進版控、publish 也不會產出這個檔**——
     重新部署解壓成品時不會被覆蓋，設定天然存活於重佈之間（repo 內的 `appsettings.json`
     只放開發預設值，會隨成品發佈，實際環境靠 Production 檔逐 key 覆蓋）。
   - `.gitignore` 加 `appsettings.Production.json`（含子目錄），防止任何人在本機建了真設定
     誤入版控；`MessageService.Web.csproj` 確認不把該檔納入 publish 輸出（檔案不在 repo 內
     本來就不會，加一條防呆註記即可）。
   - 檔案含明文機密，保護面與原 web.config 方案相同（都是站台目錄下的明文檔）：站台目錄
     ACL 只開集區帳號與管理者，這條寫進部署文件與驗收清單。
3. **appsettings 樣板組**（`deploy/` 目錄，不參與編譯）：
   `appsettings.Production.Edge.json`／`.Core.json`／`.Viewer.json`／`.AllInOne.json`，
   部署時**複製改名為 `appsettings.Production.json`** 再填值；每份含該拓撲需要的拓撲 key
   （3~6 個）＋機密 key 空殼（`ChannelSecret`/`ChannelAccessToken`/`Ingest:ApiKey`/
   `Encryption:Key`/連線字串，值留空白），全部 **SQLite 預設**（使用者需求：預設拆機＋SQLite）；
   repo 根的 `appsettings.json` 預設 `Mode=AllInOne`＋`Database:Provider=Sqlite`（開發即用；
   收錄端舊 appsettings 的 `SqlServer` 預設一併退場，正式環境靠 Production 檔顯式指定）。
3. **集區設定指令稿**（`deploy/Set-AppPool.ps1`）：`startMode=AlwaysRunning`、
   `processModel.idleTimeout=0`、`recycling.periodicRestart.time=0`、站台
   `preloadEnabled=true`、檢查並提示安裝 Application Initialization 功能。集區層級設定進不了
   web.config（applicationHost.config 層級），這支指令稿就是「手動設定服務時間」的固化版；
   使用者要自訂回收時段時改這支的參數即可。

### 階段 6：文件收尾

1. `DEPLOYMENT-GUIDE.md` 全面改寫：單一成品流程（一次 publish）、四模式的
   `appsettings.Production.json` 樣板複製表（原 D3「編輯 web.config 環境變數」步驟整段換掉）、
   集區指令稿、**驗收清單**新增：
   - 部署完隔天早上確認 log 出現 `Retention cleanup removed ...`（問題 1 的哨兵）。
   - `logs/` 資料夾集區帳號寫入權限（連同 db 資料夾權限那條）。
   - 拆機時 Edge/Core 的 `OutboundHere` 組合檢查（現在多數情況靠預設即正確）。
   - 站台目錄 ACL 只開集區帳號與管理者（`appsettings.Production.json` 含明文機密）。
   - 重佈演練：解壓新成品後確認 `appsettings.Production.json` 仍在且未被覆蓋。
2. `DEPLOYMENT-MODES.md` 改寫：四模式能力矩陣（本文件第一節的表）、設定 key 升級對照表
   （舊 key → 新 key）、批次 ingest 端點、「先升 Core 再升 Edge」。
3. `README.md`：專案結構（兩個可發佈專案 → 一個）、schema 管理段（EnsureCreated/upgrader
   敘述改 Migrate＋Baseliner）、貼圖/匿名段的「舊檔要刪」註記移除（Baseliner 接手）。
4. `ENCRYPTION.md`：補「多主機（Core＋Viewer）時 `Encryption:Key` 仍須逐字一致」——合併
   消除的是 AllInOne 的雙份設定，多台直連 DB 的拓撲該約束仍在。
5. 本規劃檔補上「已完成」戳記與實際 commit 對照。

### 階段 7：全案體檢輪（依本專案慣例）

- 全測試綠（目標：既有 444 全過＋新增案例）。
- 雙行程實測：Edge＋Core 兩個行程互打（比照部署三模式輪抓到 4 個真 bug 的驗證手法），
  三台拓撲加開 Viewer 行程驗證白名單與模式閘門。
- SQLite 升級實測：拿一顆 2026-07 時期（無 StickerId/AnonymousIdentities）的舊 messages.db
  跑 Baseliner → Migrate → 貼圖與匿名模式功能實測。
- IIS 實測（至少一台）：集區指令稿 → 隔日 retention log 哨兵確認。
- 平行審查：pipeline 順序（白名單繞過路徑）、模式閘門（每個模式逐一列出暴露的 endpoint 清單
  核對）、Baseliner 與兩套 migrations 的 schema 等價性。

---

## 三、風險與注意事項（review 重點）

1. **模式閘門是安全邊界**：合併後「Edge 不暴露 viewer、Viewer 不暴露 ingest」全靠
   Convention＋capabilities，體檢輪必須逐模式枚舉實際存活的 endpoint。
2. **測試工廠密鑰隔離**：同一個 Program 之後，所有 fixture 的設定覆蓋要重驗（歷史踩雷點）。
3. **Baseliner 的等價性**：舊檔升級後 schema 必須與全新 Migrate 檔完全一致，用自動化比對
   測試把關，不靠人工。
4. **設定 breaking changes**：白名單 key 改名採「舊 key 非空即啟動失敗」的硬提示；模式舊名
   採軟相容。兩者策略不同是刻意的——前者設錯是安全洞，後者設錯只是名稱過時。
5. **批次 ingest 的順序與冪等**：不並行、單交易、唯一索引去重；Edge/Core 升級順序入文件。
6. **`Ingest:MaxContentBytes` 與 web.config `maxAllowedContentLength` 雙處同步**：兩處互相
   註記；日後如引入更動要一起改。
