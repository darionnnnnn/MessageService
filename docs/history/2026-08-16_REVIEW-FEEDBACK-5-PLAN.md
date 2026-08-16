# 審查回饋第五輪規劃（REVIEW-FEEDBACK-5）

> 依 `plan-before-dev` skill v3 產出。每個「階段」即交給外包 AI 的規格：只鎖契約與驗收，實作方式由執行端決定，執行端須自跑驗收全過才回報。
> 全域限制：不碰 `docs/`、不碰本階段範圍外的檔案、註解用台灣用語繁體中文、不順手重構或格式化。

## 0. 背景與範圍
- 輸入：外部審查對 dev@`86b1ed4` 的第五輪意見（P1 貼圖回填不入列、P2 Requeue 只啟動一次、P3 匿名代號撞名、P3-1 回填全表掃、P3-2 GET /api/groups 殘留）。全部五項本輪處理。
- 已定案（2026-08-16）：P3 加 `(GroupId, Label)` 唯一索引且 migration 先修補既有重複；P3-1 只做 `AnyAsync` 短路＋耗時 log，不加表；P3-2 不移除 GET（`settings.js` 是真實呼叫端），只改註解。
- 不做：一次性 marker 表；改 `IContentDownloadQueue` 介面。

## 1. 事實核對摘要
| 項目 | 成立 | 證據 | 補充 |
|---|---|---|---|
| P1 | ✅ | `StickerContentBackfillService` 沒注入 queue、SaveChanges 後不入列 | 回填註冊在 `HasDatabaseAccess`，下載服務在 `OutboundHere`；Core-only 拓撲 queue 是 Null → 入列 no-op，靠 P2 補齊 |
| P2 | ✅ | `ContentDownloadService.ExecuteAsync` 只啟動掃一次；Options 無間隔欄位 | Edge 走 `ApiContentWorkSource`→Core `DbContentWorkSource`，週期重掃在 Edge 也有效；重複入列由 `CompleteAsync` 認領 `claimed==0` 保護 |
| P3 | ✅ | `MessageDbContext` 只有 `(GroupId,UserId)` 主鍵；服務 catch 後用 `FirstAsync` | migration 每 provider 一套（Sqlite/SqlServer），有一致性測試 |
| P3-1 | ✅ | GroupMessages 無 MessageType 索引 | — |
| P3-2 | ⚠️ | 註解說只有測試在用 | 實際 `settings.js` 設定頁在用 |

## 2. 作業總覽
| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | 下載回收週期化（P2） | — | agy |
| B | 貼圖回填直接入列＋掃描短路（P1、P3-1） | — | agy |
| C | 匿名代號 `(GroupId,Label)` 唯一＋撞名重試（P3） | — | agy（migration 骨架 Claude 用 `dotnet ef` 產生） |
| D | GET /api/groups 註解修正（P3-2） | — | Claude |
| E | 文件更新＋體檢 | A~D 驗收後 | Claude |

順序 A → B → C → D → E。A 先做，因為它是拆機拓撲下 P1 的唯一修法。

## 3. 作業明細

### 作業 A-階段 1：`ContentDownloadService` 週期性重掃
- **背景**：`RequeuePendingAsync`（撈回 Pending／中斷 Downloading／可重試 Failed）目前只在啟動跑一次；貼圖回填在 Core 補出的列、worker 崩潰後卡住的列，在行程不重啟時永遠不會被撿。
- **契約**：
  - `ContentDownloadOptions` 新增 `RequeueIntervalMinutes`（int，預設 15；0 = 停用週期，只保留啟動那一次）。
  - 服務啟動後除既有那次掃描外，每隔該間隔再跑一次 `RequeuePendingAsync`；**先等一個間隔再掃**（不與啟動那次重複）。
  - 週期掃描發生例外只記 error log、迴圈不中斷、不影響 worker；停機時正常結束。
  - 註解須說明：重複入列安全（`DbContentWorkSource.CompleteAsync` 的認領檢查會跳過已被認領者），以及這是拆機拓撲下 Core 補資料的唯一回收路徑。
- **範圍**：`MessageService.Web/Options/ContentDownloadOptions.cs`、`MessageService.Web/Services/ContentDownloadService.cs`、`MessageService.Web.Tests/Services/` 下新增或修改測試；可用 `InternalsVisibleTo` 讓測試以短間隔驅動迴圈。不動 worker／`ProcessAsync` 邏輯。
- **驗收**：build 零警告；`dotnet test` 全綠；新增測試至少涵蓋：間隔到期後 work source 被再次查詢；間隔 0 時只查詢一次；某次掃描丟例外後下一輪仍執行。
- **回報格式**：改動檔案清單、測試總數／綠／紅、偏離契約處。

### 作業 B-階段 1：貼圖回填入列＋短路
- **背景**：`StickerContentBackfillService` 補建 Pending 列後沒有入列到 `IContentDownloadQueue`；且每次啟動都做一次無索引的反連結掃描。
- **契約**：
  - 每批 SaveChanges 後把新建 `MessageContent` 的 Id 入列到 `IContentDownloadQueue`；註解說明 Core-only 拓撲下 queue 為 Null 實作、入列 no-op，該拓撲靠作業 A 的週期重掃收回。
  - 開始前先以 `AnyAsync` 判斷有無待補；沒有就直接結束並記 debug log；有補時的 info log 加上耗時。
  - 不改查詢條件、批次大小、DI 註冊。
- **範圍**：`StickerContentBackfillService.cs`、其測試檔（用既有 `FakeContentDownloadQueue`）。
- **驗收**：build 零警告；`dotnet test` 全綠；測試涵蓋：回填後 queue 收到的 Id 集合等於新建列的 Id 集合、第二次執行不再入列、空表不入列。

### 作業 C-階段 1：模型唯一索引＋兩套 migration
- **背景**：`AnonymousIdentity` 只有 `(GroupId, UserId)` 主鍵，併發指派可讓兩個不同使用者拿到同一個 Label；既有資料可能已重複。
- **契約**：
  - `MessageDbContext` 對 `AnonymousIdentity` 加 `(GroupId, Label)` 唯一索引，註解說明用途。
  - Sqlite 與 SqlServer 各一支 migration `AnonymousLabelUnique`（骨架由 Claude 以 `dotnet ef` 產生後交付）；`Up()` 建索引**之前**先修補既有重複：同 `(GroupId, Label)` 依 `AssignedAt, UserId` 排序，第 2 筆起改為 `原Label (n)`（括號後綴，避免與服務端「Label n」格式再撞）。`Down()` 只 drop index，不還原 Label（註解說明）。
- **範圍**：`MessageService.Data/Data/MessageDbContext.cs`、兩個 provider 的 migration 資料夾與 snapshot、Data 層測試。
- **驗收**：build 零警告；`MessageDbMigrationsConsistencyTests` 綠；新增測試：以 Sqlite 建到前一版、塞兩筆同 (GroupId,Label) 不同 UserId、套用新 migration 後 Label 變 `X`／`X (2)` 且唯一索引存在。

### 作業 C-階段 2：服務撞名重試
- **背景**：`AnonymousIdentityService.GetOrAssignAsync` 讀計數→算後綴→寫入無序列化；catch `DbUpdateException` 後用 `FirstAsync` 會把暫時性故障變成 `Sequence contains no elements`。
- **契約**：SaveChanges 拋 `DbUpdateException` 時：
  1. 先查同 `(GroupId, UserId)` 是否已存在 → 存在就採用對方那筆。
  2. 否則若是 Label 撞名 → 後綴遞增重試，上限 50 次，超過拋帶說明的 `InvalidOperationException`。
  3. 既非主鍵撞也非 Label 撞（暫時性故障）→ 原例外往外拋，不吞。
  - 回傳型別、`AvatarIconCatalog`、逐筆 SaveChanges 的既有設計不變。
- **範圍**：`AnonymousIdentityService.cs`、`AnonymousIdentityServiceTests.cs`。
- **驗收**：build 零警告；既有 7 個測試綠；新增測試涵蓋：兩個不同使用者同 IconKey 併發（或模擬先佔 Label）→ Label 不同且都以 icon.Label 開頭；暫時性 DbUpdateException 原樣拋出（可用 fake/中斷連線模擬，難以模擬時說明並改測邏輯路徑）。

### 作業 D：GET /api/groups 註解（Claude 自做，不外包）
- 註解改為：不是側欄用的、未讀恆 0；呼叫端為設定頁 `settings.js` 與健康檢查／白名單測試；側欄一律用 `POST /api/groups/list`。`GroupDto.UnreadCount` 若有 XML 註解補一句。

### 作業 E：文件與體檢（Claude）
- 見第 5 節；跑全測試、看 `git diff --stat`、填執行紀錄。

## 4. 測試計畫
| 作業-階段 | 要證明的行為 |
|---|---|
| A-1 | 週期再查詢；interval 0 只查一次；例外後續跑 |
| B-1 | 入列 Id 集合正確；冪等不重入列；空表不入列 |
| C-1 | migration 修補重複 Label；索引存在；一致性測試 |
| C-2 | 併發不同人不同 Label；暫時性例外原樣拋 |

## 5. 文件更新（Claude，驗收後）
- `docs/DEPLOYMENT-MODES.md` 已知限制：worker 崩潰卡住／Core 補資料 Edge 不重啟 → 改為最多延遲 `ContentDownload:RequeueIntervalMinutes`（預設 15 分）。
- README 設定說明新增 `ContentDownload:RequeueIntervalMinutes`。
- 現行 design notes 若提到匿名代號指派，補「(GroupId, Label) 唯一＋後綴重試」。
- 結案後本檔移入 `docs/history/`。

## 6. 風險與回滾
| 作業 | 風險 | 觀察 | 回滾 |
|---|---|---|---|
| A | 大量 Failed 時每週期重掃 DB | requeue log 筆數 | 設 `RequeueIntervalMinutes=0` |
| C | 既有重複 Label 修補後含 `(2)`；SqlServer migration 未實測 | 部署前跑 `migrations script` | `Down()` drop index，Label 不還原 |

## 7. 執行紀錄

基準：委派前 679 測試全綠（dev@86b1ed4）。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | agy | `RequeueIntervalMinutes`（預設 15，0 停用）＋與 worker 並行的重掃迴圈，先等一個間隔再掃 | build 0 警告；683 綠（+4） | 無。多做一筆「間隔 ≤ 0 立即返回」的防禦測試，保留 |
| B-1 | agy（第 2 次） | 每批存檔後入列新建內容 Id；掃描前 `AnyAsync` 短路＋耗時 log | build 0 警告；684 綠；突變測試（拿掉入列）確認測試會紅 | 第 1 次零變更：規格要它「開工前先跑基準測試」，時間全耗在等測試後逾時。改成直接給定基準數字重跑成功 |
| C-1 | agy | `(GroupId, Label)` 唯一索引；Sqlite／SqlServer 各一支 `AnonymousLabelUnique`，`Up()` 先 ROW_NUMBER 修補重複再建索引 | build 0 警告；685 綠；migration 一致性測試綠 | 白名單外連帶修改：SqlServer 端 Label 由 `nvarchar(max)` 改 `nvarchar(450)`（不改無法建索引），屬必要連帶，採納 |
| C-2 | agy | `DbUpdateException` 三路判斷（同人被搶先／Label 撞名遞增重試上限 50／其餘 rethrow） | build 0 警告；689 綠；`FirstAsync` 已無命中 | 無 |
| D | Claude | `GET /api/groups` 與 `GroupDto.UnreadCount` 註解改寫（點名 settings.js 是真實呼叫端、側欄用 POST /list） | build 綠 | 未移除端點——審查建議移除，但設定頁實際在用 |
| E | Claude | README 補 `ContentDownload:RequeueIntervalMinutes`；DEPLOYMENT-MODES 已知限制改為「最多延遲一個重掃週期」；`DbContentWorkSource` 過時註解同步 | 689 綠 | 無 |

### 體檢輪（獨立審查整批 diff）

| 嚴重度 | 發現 | 處置 |
|---|---|---|
| 高 | 週期重掃沿用 `GetPendingIdsAsync` 會把 `Downloading` 無條件打回 `Pending`——啟動時前提是「沒有 worker 活著」，週期時 worker 正在下載大檔（最長 10 分鐘），被打回後另一 worker 再度認領，兩邊同時寫同一顆 blob，正是 `CompleteAsync` 認領互斥要擋的情境；Edge 拓撲經 ingest API 同樣中招。**規劃 A 時漏掉的交互，A-1 的測試也抓不到。** | `IContentWorkSource.GetPendingIdsAsync` 加 `reclaimDownloading`：啟動 `true`（舊行為）、週期 `false`（只撈 Pending＋可重試 Failed）；ingest API 以 query 帶過去、預設 `true` 相容舊版 Edge。行程活著時下載失敗本就由 `RevertClaimAsync` 改回 Pending，所以週期不碰 Downloading 幾乎不損失回收能力。補 4 筆測試（Db 來源不動 Downloading、迴圈一律傳 false、啟動傳 true、API 帶參數）。文件同步：README／DEPLOYMENT-MODES 已知限制與升級順序 |
| 低 | `RequeueIntervalMinutes` 設到 >24.8 天會讓 `Task.Delay` 拋 `ArgumentOutOfRange` 冒出 `ExecuteAsync` → StopHost | 封頂 24 天 |
| 中（不改） | 第 1 路採用他人寫入的列後本地計數用 `Math.Max` 遞增，同批下一人可能多一次撞名重試才拿到號 | 最終正確（唯一索引＋重試兜底），代價是多一次 INSERT，不值得再查一次 |
| 中（不改） | migration 修補後綴「X (2)」若與既有資料撞會讓建索引失敗、啟動炸 | 專案沒有任何路徑會產生「X (n)」格式（服務端是「X n」、無 UI 可編輯 Label），實務不可能 |

體檢後：692 綠、build 0 警告。

待人工驗收：SqlServer 的 migration 只跑過模型一致性測試，實機部署前先 `dotnet ef migrations script -c SqlServerMessageDbContext` 確認修補 SQL 與 `nvarchar(450)` 轉型在真實資料上沒問題。
