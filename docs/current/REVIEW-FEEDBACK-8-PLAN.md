# 審查回饋第八輪規劃（多主機租約／migration 可觀測性／索引）

## 0. 背景與範圍

- **輸入**：外部審查對 `dev@1175dfd` 的第八輪意見（P1 租約回收死碼、P2 migration 進度不入 log、P3-1 `MessageType` 全表索引、P3-2 假承諾 log）。
- **處理項目**：P1、P2、P3-1、P3-2 全做。
- **明確不做**：`CompleteAsync`（`ClaimedAt == claimTime`）與 `RevertClaimAsync`／`FailAsync`（`ClaimedBy == ownerId`）fencing 欄位不一致——目前皆正確且有測試覆蓋，沒有實證問題，本輪不動，記為已知取捨。
- **已定案決策**：
  - P1 採「站台穩定 ownerId」為根因修法（死碼的根因是 ownerId 語意錯：它應代表「站台」而非「行程」），並同時把 `ClaimLeaseMinutes` 預設降到 15，讓極端情況下限也縮短。接受 IIS 重疊回收過渡期新舊行程同 ownerId 造成的「該筆重下載一次」代價，資料安全由 `CompleteAsync` fencing 兜底。
  - P3-2 只改 log 訊息分流，**不**在停用週期重掃時拿掉 `Take` 上限（`Take` 是記憶體／`Contains` 分批的保護；也不能用「啟動迴圈掃到見底」，Pending 列每次都會被撈到而死迴圈）。
  - P2 不放寬 `nlog.config` 的 `Microsoft.*` 規則（會放進 SQL 雜訊），改在自己控制的 logger 記錄。

## 1. 事實核對摘要

| 項 | 判定 | 證據 | 補充 |
|---|---|---|---|
| P1 死碼 | ✅ | `ProcessOwnerId.cs` 每行程隨機 Guid；唯一使用者 `ContentDownloadService`；所有拓撲註冊為單例 | 不涉及 mutex／SQLite lock／心跳；Edge 走同類別，改法對 Edge 同樣生效。既有測試 `DbContentWorkSourceTests.cs:1552` 斷言「同機不同 ownerId 互不干擾」，語意變更後須改寫 |
| P1 恢復退步到 60 分 | ✅ | `ContentDownloadOptions` Requeue=15、Lease=60 | |
| P2 | ✅ | `nlog.config` `Microsoft.*` maxlevel=Info final；`MigrateMessageServiceDatabase` 成功路徑零 log | `web.config` 註解與 `DEPLOYMENT-GUIDE.md` 兩處叫人「看 log」，目前做不到 |
| P3-1 | ✅ | `MessageDbContext.cs:77` 無 filter；兩 provider `MultiHostHardening` migration 皆無 filter | 全專案唯一低基數無 filter 索引；`MessagesController` 的 `MessageType == "text"` 查詢不受影響 |
| P3-2 | ✅ | `ContentDownloadService.cs:53` 週期任務不啟動；log 無條件印 | 普查其他 log 一致 |

## 2. 作業總覽

本輪委派模型：`gemini-3.7-flash-high`｜開工前額度：Gemini 週限 34%／五小時 86%，Claude 池週限 65%／五小時 100%｜使用者未指派。

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | ProcessOwnerId 站台穩定化 + Lease 預設 15 + 測試語意改寫 | 無 | agy |
| B | migration 執行過程入 log（pending 清單／耗時） | 無 | agy |
| C | `MessageType` 索引改 filtered，兩 provider 各補 migration | 無 | agy |
| D | P3-2 log 訊息分流 | 無 | Claude |
| E | 文件更新 | A~D | Claude |

## 3. 作業明細

### 作業 A-階段 1：ProcessOwnerId 對同一站台穩定
- **背景**：ownerId 目前含每行程隨機字尾，導致啟動回收條件 `isStartup && ClaimedBy == ownerId` 永遠不成立，崩潰孤兒要等租約逾期才被撿回。ownerId 的正確語意是「同一台機器上的同一個站台」。
- **契約**：
  - `ProcessOwnerId.Value` = `{MachineName}-{站台鍵的 SHA256 十六進位前 8 碼}`，站台鍵預設 `AppContext.BaseDirectory`，建構子允許傳入自訂站台鍵（暫定為可選字串參數）；總長仍上限 128。
  - 同一機器、同一 BaseDirectory 的兩個行程得到相同 Value；不同 BaseDirectory 得到不同 Value；不同機器不同 Value。
  - `ContentDownloadOptions.ClaimLeaseMinutes` 預設由 60 改為 15；`appsettings.json` 顯式值同步改 15；相關註解更新。
  - `GetPendingIdsAsync` 回收條件本身不改。
- **範圍**：`MessageService.Web/Services/ProcessOwnerId.cs`、`Options/ContentDownloadOptions.cs`、`appsettings.json`、`MessageService.Web.Tests`。**不准動** docs/、`DbContentWorkSource.cs` 邏輯、其他作業檔案。
- **驗收**：
  - `dotnet build` 零警告、`dotnet test` 全綠。
  - 新增測試：`ProcessOwnerId_SameSiteKey_ProducesSameValue`（同鍵兩實例相等）、`ProcessOwnerId_DifferentSiteKey_ProducesDifferentValue`、`ProcessOwnerId_Value_LengthNotExceed128AndStartsWithMachineName`。
  - 新增測試：`GetPendingIdsAsync_StartupReclaim_ReclaimsOrphansLeftByPreviousProcessWithSameOwnerId`——模擬前一行程用相同 ownerId 認領且租約未逾期的 Downloading 列，重啟後 `isStartup=true` 掃描要把它回收成 Pending。
  - 改寫既有 `GetPendingIdsAsync_StartupReclaim_ReclaimsOnlyMatchingOwnerId_NotDifferentOwnerOnSameMachine`：保留「不同 ownerId 不互撿」的斷言（對應同機不同站台），測試名稱與註解改成「不同站台」語意，不得刪除。
  - grep：`Guid.NewGuid` 不應再出現在 `ProcessOwnerId.cs`。
- **回報格式**：改了哪些檔（一行一檔）、測試數字（總數／綠／紅）、有無偏離契約之處與理由。

### 作業 B-階段 1：migration 進度記入應用 log
- **背景**：`nlog.config` 把 `Microsoft.*` Info 以下濾掉，EF 的「Applying migration」看不到；`MigrateMessageServiceDatabase` 成功路徑完全靜默，維運人員分不出「正在搬資料」與「卡死」。
- **契約**：
  - 在既有 `ILogger<Program>` 上：migrate 前若有 pending migration，記一則 Information 含數量與名稱清單；migrate 完成後記一則 Information 含耗時；沒有 pending 時記一則 Information「資料庫結構已是最新」（暫定等級 Information；若嫌吵可降 Debug 但要說明）。
  - 兩則訊息用繁中，格式與檔內既有 Warning 一致。
  - `nlog.config` 規則**不改**。
  - 適用所有會走 `Migrate()` 的路徑（含 SQLite 救場分支、SqlServer 分支）。
- **範圍**：`MessageService.Web/Startup/MessageServiceDatabaseMigrationExtensions.cs`、`MessageService.Web.Tests`。不准動 nlog.config、docs/、其他作業檔案。
- **驗收**：
  - build 零警告、test 全綠。
  - 新增測試（用 SQLite 記憶體庫＋可攔截的 logger）：`MigrateMessageServiceDatabase_WithPendingMigrations_LogsMigrationNamesAndElapsed`、`MigrateMessageServiceDatabase_WhenUpToDate_LogsUpToDateWithoutMigrationList`。若既有測試基礎設施無法攔截該 logger，改為驗證 `ILogger` 收到的訊息片段（測試名保留）。
- **回報格式**：同 A。

### 作業 C-階段 1：`IX_GroupMessages_MessageType` 改為篩選索引
- **背景**：該索引只服務貼圖回填的 `MessageType == "sticker"` 查詢，`MessageType` 選擇性極低，無 filter 的索引讓每筆寫入永久付出成本。專案已有 `MessageContents.DownloadStatus` 篩選索引範例。
- **契約**：
  - `MessageDbContext` 該索引加 filter：SQLite `"MessageType" = 'sticker'`、SqlServer `[MessageType] = 'sticker'`（依既有 `Database.IsSqlite()` 分流寫法）。
  - 兩 provider 各新增一支 migration（名稱暫定 `FilterMessageTypeIndex`），Up 以 drop＋create 帶 filter 換掉舊索引，Down 還原為無 filter；snapshot 同步。
  - `StickerContentBackfillService` 查詢與行為不變。
- **範圍**：`MessageService.Data`（DbContext、兩套 Migrations 資料夾）。不准動 docs/、Web 專案、其他作業檔案。
- **驗收**：
  - build 零警告、test 全綠（含 `MessageDbMigrationsConsistencyTests`）。
  - 新增測試：`GroupMessages_MessageTypeIndex_HasStickerFilter_OnSqlite`（用 EF model 讀取索引 filter 字串驗證）。
  - grep：兩套 Migrations 各存在一支含 `FilterMessageTypeIndex` 的檔，且 SQLite snapshot 中該索引有 `HasFilter`。
- **回報格式**：同 A。

### 作業 D（Claude）：P3-2 log 分流
- 契約：`DbContentWorkSource` 掃描達上限時，若週期重掃啟用維持原 Information；若 `RequeueIntervalMinutes <= 0` 改為 Warning，說明「週期重掃已停用，剩餘項目要等下次啟動才會處理」。需要讓 work source 能知道該設定（它已注入 `ContentDownloadOptions`）。
- 驗收：既有測試綠；補一支 `GetPendingIdsAsync_LimitReached_WhenPeriodicRequeueDisabled_LogsWarning`。

## 4. 測試計畫
見各階段驗收；總數應由 762 增加 ≥ 7。

## 5. 文件更新（Claude，全部驗收後）
- `docs/DEPLOYMENT-GUIDE.md`：migration 段補「進度會記在 `logs/messageservice-{日期}.log`（開始／完成／耗時）」；設定表 `ClaimLeaseMinutes` 預設改 15；多主機段補一句「IIS 重疊回收期間新舊行程同 ownerId，進行中下載可能重跑一次，資料由 fencing 保護」。
- `web.config` 註解「沒有任何線索」語句更新。
- `README.md` 若有列 ContentDownload 預設值同步。
- 本文件填 §7 後依 docs-current-vs-history 收尾。

## 6. 風險與回滾
- A：Web Garden（maxProcesses>1）下多行程同 ownerId 會互相搶回未逾期認領——SQLite 本就不支援 Web Garden，SqlServer 環境若開 Web Garden 會重複下載但不壞資料；文件註明。回滾：還原檔案即可，無資料層變更。
- C：migration 在大表上重建索引需時間；篩選後索引小，重建成本低於原建立。回滾走 Down。
- B、D：純 log，無風險。

## 7. 執行紀錄
| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| | | | | |
