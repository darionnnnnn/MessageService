# 審查回饋第七輪規劃（升級路徑＋多主機同步）

## 0. 背景與範圍

- 輸入：外部審查對 `cf1ab3e`（blob 拆表）的回饋 P1a／P1b／P2／P3a／P3b／文件段落，加上本輪自行普查的多主機同步與單主機效能問題。
- 處理項目：作業 A～F（見 §2）。
- 明確不做：
  - 新增 log sink（P3b 審查誤判，專案已用 NLog 檔案 sink）。
  - DB 層分散式 migration 鎖（`sp_getapplock`）——SQLite 無對應，改用文件規範＋鎖不可得時跳過。
- 已定案決策（2026-08-16）：
  1. 跨機器 migrate：文件規定三台拓撲 Viewer `Database:AutoMigrate=false`；`onLockUnavailable` 改為跳過＋Warning，不硬跑。
  2. 啟動 reclaim：`MessageContents` 加 `ClaimedAt` 租約欄位，只回收逾期者；週期重掃共用。
  3. Sticker 回填只在 `RunsRetention` 主機跑，撞鍵不中止。
  4. Logging：只清理誤導設定＋註解，不加 sink。
  5. 效能三項＋Heartbeat 唯一索引進本輪，與 C 的 migration 合為同一支（每 provider 一支）。

## 1. 事實核對摘要

| 項 | 結果 | 要點 |
|---|---|---|
| P1a 啟動同步 Migrate／無 startupTimeLimit | ✅ | Viewer 也 migrate；mutex 只跨行程；`onLockUnavailable` 硬跑 |
| P1b 搬遷 INSERT 無冪等 | ✅ | 兩 provider 皆無 NOT EXISTS |
| P2 無孤兒清理 | ✅ | cascade 齊備，孤兒來源僅寫一半被殺 |
| P3a UpsertMember 無重試 | ✅ | 多主機 Core+Edge 皆 OutboundHere 時會撞 |
| P3b 無檔案 sink | ❌ | 已有 NLog；`appsettings` `Logging` 區段因 `ClearProviders()` 無效 |
| 文件段落位置 | ✅ | SplitBlobTables 藏在 SQL Server 專屬節 |
| 新：啟動 reclaim 競態 | 🔴 | 每次啟動無條件 Downloading→Pending |
| 新：Sticker 回填 Core+Viewer 同跑 | 🔴 | 撞 1:1 唯一鍵後整批中止 |
| 新：SQLite busy_timeout 未設 | 🟠 | 兩處註解「預設 30 秒」錯誤 |
| 新：HostHeartbeats 無唯一索引 | 🟡 | |
| 新：Retention 指標刷新非原子 | 🟡 | 僅雙 Core 誤設時發作，本輪不改程式，文件註明 |
| 新：效能 | — | GetPendingIds 無 Take／MessageType 無索引／outbox CreatedAt 無索引 |

## 2. 作業總覽

本輪委派模型：`claude-sonnet-4-6`（開工前查額度：Gemini 池週限剩 42%、Claude 池週限剩 98%／五小時限 97%；使用者未指派，取剩餘較高的 Claude 池）

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A 升級路徑 | startupTimeLimit、搬遷 INSERT 冪等、鎖不可得跳過 | — | Claude |
| B 孤兒清理 | Retention 每批後清 blob 孤兒 | — | agy |
| C 多主機互斥 | ClaimedAt 租約、Sticker 回填歸屬、UpsertMember 重試、Heartbeat 唯一索引、三索引，migration 合一 | — | agy |
| D SQLite 寫入韌性 | busy_timeout、修錯誤註解 | — | agy |
| E GetPendingIds 上限 | Take 上限＋Contains 分批 | C（同一 migration 不需要，可獨立） | agy |
| F 文件 | DEPLOYMENT-GUIDE 重整、Viewer AutoMigrate、Logging 說明 | A～E 驗收後 | Claude |

## 3. 作業明細

### 作業 A（Claude 自做，不外包）
1. `web.config` `<aspNetCore>` 加 `startupTimeLimit="3600"`。
2. 兩份 `SplitBlobTables` migration 的三段 INSERT 加 `WHERE NOT EXISTS`（子表已有同鍵者略過）；`SplitBlobTablesMigrationTests` 補一條「子表預先塞一列後跑 migration 不炸且不重複」。
3. `MessageServiceDatabaseMigrationExtensions` `onLockUnavailable`：改為 LogWarning 並跳過 migrate，不硬跑。

### 作業 B-階段 1：保留期清除補 blob 孤兒清理
- **背景**：拆表後空間回收依賴兩層 cascade，失效時靜默；migration／寫一半被殺也會留孤兒。
- **契約**：`RetentionCleanupService` 每完成一批 `GroupMessages` 刪除後，以單一 `ExecuteDelete` 刪除沒有對應 `MessageContents` 的 `MessageContentBlobs`；刪除數 >0 時 LogWarning（cascade 正常應為 0）。整輪結束再跑一次（涵蓋既有孤兒）。
- **範圍**：`RetentionCleanupService` 與其測試。不動 docs、migration。
- **驗收**：build 零警告、test 全綠；新增測試：`OrphanBlobs_AreRemoved_AfterBatch`（手動插孤兒 blob→執行→消失）、`CascadeIntact_OrphanCleanupDeletesZero`。
- **回報格式**：改檔清單、測試數字、偏離。

### 作業 C-階段 1：資料模型與 migration
- **背景**：本輪多主機修正需要一次 schema 變更；每 provider 恰一支 migration，名稱 `MultiHostHardening`。
- **契約**：
  - `MessageContents` 新增可為 null 的 `ClaimedAt`（UTC）。
  - `HostHeartbeats` 加 `(Role, MachineName)` 唯一索引；migration 前先以「留最新一列」去重既有重複。
  - `GroupMessages.MessageType` 加索引；outbox 不在主庫（略），主庫 outbox 相關無需索引。
  - Sqlite／SqlServer 各一支 migration，`ModelSnapshot` 同步；SqlServer 版須以 SQL Server 語法通過 `dotnet ef migrations script` 產生。
- **範圍**：`MessageService.Data`（Entities、DbContext、Migrations、Snapshot）；不動服務程式。
- **驗收**：build 零警告；既有 migration 測試（含 `SplitBlobTablesMigrationTests` 樣式）新增 `MultiHostHardeningMigrationTests`：SQLite 實跑通過、重複心跳列被去重且唯一索引存在。
- **回報格式**：同上。

### 作業 C-階段 2：Downloading 租約
- **背景**：啟動 reclaim 把所有 Downloading 打回 Pending，多主機下害同一 blob 並行寫入。
- **契約**：
  - 認領（`DownloadStatus→Downloading`）時同一句 UPDATE 寫入 `ClaimedAt=now`；Completed／Failed／回 Pending 時清為 null。
  - 新設定 `ContentDownload:ClaimLeaseMinutes`（預設 30）。回收改為「Downloading 且 `ClaimedAt < now-lease` 或 `ClaimedAt` 為 null」才回 Pending；啟動與週期重掃共用同一條規則，啟動不再無條件回收。
  - `Api` work source（Edge）行為由 Core 端同一 SQL 決定，不需改 Edge。
- **範圍**：`DbContentWorkSource`、`ContentDownloadService`、選項類、appsettings 預設；測試。
- **驗收**：build／test 綠；新增：`Reclaim_SkipsFreshDownloading`、`Reclaim_ReturnsExpiredDownloading`、`Claim_SetsClaimedAt`、`Complete_ClearsClaimedAt`；既有 reclaim 測試依新規則調整而非刪除。
- **回報格式**：同上。

### 作業 C-階段 3：Sticker 回填歸屬＋撞鍵不中止
- **契約**：`StickerContentBackfillService` 註冊條件改為 `capabilities.RunsRetention`；回填改為每筆／小批獨立 SaveChanges，撞 `DbUpdateException` 時 `ChangeTracker.Clear()`、記 Information、跳過該筆繼續；啟動偵測改為對 `MessageType` 索引友善的查詢（有 C-1 索引後即可）。
- **範圍**：該服務、DI 註冊、測試。
- **驗收**：新增 `Backfill_ContinuesAfterDuplicateKey`、`Backfill_NotRegistered_ForViewer`；build／test 綠。

### 作業 C-階段 4：UpsertMember 重試＋Heartbeat 撞鍵
- **契約**：`DbProfileStore` 將 Group 版的「DbUpdateException→Clear→重跑一次」抽成兩支 Upsert 共用；`DbHeartbeatStore` upsert 撞唯一鍵時同樣重試一次（改為更新）。
- **範圍**：兩個 store 與測試。
- **驗收**：新增 `UpsertMember_RetriesOnceOnDuplicate`、`HeartbeatUpsert_NoDuplicateRows_UnderRace`；build／test 綠。

### 作業 D-階段 1：SQLite busy_timeout
- **契約**：主庫與 outbox 的 SQLite 連線在開啟時執行 `PRAGMA busy_timeout=<ms>`（或連線字串等價設定），毫秒數由設定 `Database:SqliteBusyTimeoutMs` 控制，預設 30000；SqlServer 不受影響。修正 `OutboxSchemaUpgrader`、`MessageServiceDatabaseMigrationExtensions` 兩處錯誤註解。
- **範圍**：連線建立點（DbContext 設定／連線攔截器）、outbox 連線、選項；測試。
- **驗收**：新增 `SqliteConnection_HasBusyTimeoutApplied`（開連線查 `PRAGMA busy_timeout` 回傳設定值）；grep 不再出現「busy_timeout 預設 30 秒」；build／test 綠。

### 作業 E-階段 1：GetPendingIds 上限
- **契約**：`DbContentWorkSource.GetPendingIdsAsync` 加上限（設定 `ContentDownload:MaxPendingIdsPerScan`，預設 5000，依 `ReceivedAt` 舊者優先）；後續 `Contains(ids)` 查詢以 ≤500 一批切分，避免 SQL Server 2100 參數上限；行為與結果順序不變。
- **範圍**：`DbContentWorkSource`、選項；測試。
- **驗收**：新增 `GetPendingIds_RespectsCap`、`Contains_BatchesOver500Ids`（插 1200 筆驗證全部處理）；build／test 綠。

## 4. 測試計畫
見各階段驗收；總數應「既有不少＋新增 ≥ 12」。

## 5. 文件更新（作業 F，Claude 全部驗收後）
- `DEPLOYMENT-GUIDE.md`：SplitBlobTables 拉成獨立節（適用兩 provider）；SQLite 升級前置：停站→離線 `dotnet ef database update`→確認兩倍空間→部署；升級期 `stdoutLogEnabled=true`；三台拓撲 Viewer 設 `Database:AutoMigrate=false`；雙 Core 不支援（Retention 指標刷新非原子）說明；新設定鍵三個。
- `appsettings.json` `Logging` 區段加註解或移除，指向 `nlog.config`（Claude 順手）。
- design notes：ClaimedAt 租約、回填歸屬。
- 結案依 docs-current-vs-history 搬 history。

## 6. 風險與回滾
- migration 每 provider 一支，回滾用 `Remove-Migration`／`database update <前一支>`；Heartbeat 去重不可逆但資料無價值。
- 租約 30 分鐘過短時大檔會被回收重下（僅重複下載，不損資料）；可調設定。
- 各作業獨立 commit。

## 7. 執行紀錄

分支 `feature/multihost-review-7`（自 dev）。委派模型中途切換：C-1 的回饋輪撞上 Claude 池
五小時額度用罄，之後全部改用 `gemini-3.7-flash-high`（使用者同時把 skill 預設改成它）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A 升級路徑 | Claude | 完成 `966727f` | 722→ build 0 警告 | 搬遷 SQL 抽成 `SplitBlobTablesDataMove` 供兩 provider 與測試共用（避免測試驗的是抄過去的另一份 SQL） |
| B 孤兒清理 | agy(sonnet) | 完成，721 綠 | 通過 | 無 |
| C-1 schema | agy→Claude | 完成 `8c47af2`，722 綠 | 通過 | agy 為了不存在的問題自建 EF 內部 API 的 `IMigrationsAssembly`（過度設計），回饋要求還原；重產 migration 由 Claude 用 `dotnet ef` 做掉。**真正的衝突不是 `ClaimedAt` 而是新索引**：`LegacySqliteBaselinerTests` 的「模擬舊檔」是用今日模型 `EnsureCreated` 後再砍後期欄位，新索引要一併列入砍除清單 |
| C-2 租約 | agy | 完成 `19690b2`，728 綠（+6） | 通過 | 逾時中斷未自驗，由 Claude 重跑 |
| C-3 貼圖回填 | agy | 完成 `d43095a`，733 綠（+5） | 通過 | 無 |
| C-4 撞鍵重試 | agy | 完成，736 綠（+3） | 通過 | 逾時中斷未自驗 |
| D busy_timeout | agy | 完成，740 綠（+4） | 通過 | 它在 `appsettings.json` 加了 `//` 註解（該檔其餘無註解），Claude 移除 |
| E 掃描上限 | agy | 完成，745 綠（+5） | 通過 | 無 |
| F 文件 | Claude | 完成 | — | `SplitBlobTables` 拉成兩 provider 共通節＋SQLite 離線升級步驟；DEPLOYMENT-MODES 補四個新設定鍵、貼圖回填歸屬、雙 Core 不支援、NLog 說明 |

**推翻原規劃之處**：`HostHeartbeats` 原本判斷「缺唯一索引」是錯的——它的主鍵本來就是複合鍵
`(Role, MachineName)`，撞鍵表現是拋 `DbUpdateException` 而非插出重複列，所以 C-1 的 migration
只剩 `ClaimedAt` 與 `MessageType` 索引，心跳改成純撞鍵重試（C-4）。
