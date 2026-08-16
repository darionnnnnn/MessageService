# 審查回饋第六輪規劃：blob 整份載入 + 拆表

## 0. 背景與範圍

輸入：外部審查對 dev@da56cde 的第六輪意見（P1 `DbContentWorkSource` 三處、P2 頭貼三處、P3 拆表結構建議）。
使用者定案：**三項全做（含拆表）**，並自行體檢一次把同型問題一輪收齊。

處理項目：
- P1 `DbContentWorkSource.CompleteAsync / FailAsync / GetAsync` 整實體載入。
- P2 `DbProfileStore.GetStalenessAsync`、`GroupLastMessageTracker.TrackAsync`、`RetentionCleanupService.RefreshStaleGroupPointersAsync`。
- P3 三個 blob 欄位拆成 1:1 獨立表。
- 體檢新增：`UsersController` 成員列表整實體、`MessagesController` 群組字典整實體、`GroupsController.RecoverDriftedLastMessageAsync`、`DbProfileStore.Apply*Upsert`（拆表後自然解決，列入普查驗收）；`CompleteAsync` 中繼資料更新在 try 之外；`FailedAttempts` 非原子累加；SQLite 失敗留下 zeroblob 殘骸（拆表後改為刪 blob 列即可根治）。

明確不做：
- `AvatarsController` 改串流（上限 2MB、已有 304 短路，收益低）。
- `SqliteBlob` 不吃 cancellationToken（記錄即可）。
- 對頭貼／內容 blob 改用檔案系統儲存。

已定案決策：
- 拆表後**中繼資料留在父表**（`PictureContentType/PictureFetchedUrl/PictureUpdatedAt`、`MessageContent` 全部純量），只有 byte[] 搬走。
- 三張新表：`GroupPictures(GroupId PK/FK→Groups, Content)`、`GroupMemberPictures(GroupId,UserId PK/FK→GroupMembers, Content)`、`MessageContentBlobs(MessageContentId PK/FK→MessageContents, Content)`，皆 `ON DELETE CASCADE`、主鍵不自動產生。
- 「有沒有圖」一律以導覽屬性 `!= null`（EF 翻成 EXISTS）判斷，不另加 bool 欄位。
- migration 一次搬資料（`INSERT…SELECT WHERE Content IS NOT NULL` → 移除舊欄），SQL Server 大表風險寫進 DEPLOYMENT-GUIDE 升級段。
- 資料修補 SQL 兩 provider 各寫各的；SqlServer 端無法本機實跑，以「Sqlite migration 測試 + SqlServer migration 產生的 script 人工核對」為驗收。

## 1. 事實核對摘要

| 項目 | 判定 | 證據 |
|---|---|---|
| P1a/b/c | ✅ | DbContentWorkSource.cs L83-95、L195、L254-266 無投影；同檔 L48-59 註解自禁此行為 |
| P1 相容性 | ✅ 無風險 | EF Core 10.0.10；ExecuteUpdate/Delete 兩 provider 已在用；測試全 Sqlite |
| P2a/b/c | ✅ | DbProfileStore.cs L17/27、GroupLastMessageTracker.cs L23（會 Add stub、與 ingest 同 DbContext 但分開 SaveChanges）、RetentionCleanupService.cs L164 |
| 體檢新增 | ✅ | UsersController.cs L26-33、MessagesController.cs L427-430、GroupsController.cs L144、DbProfileStore.cs L53/79 |
| 拆表影響 | 見 §3-D | raw SQL 表名／欄名字面量：ContentStreamService 8 處、DbContentWorkSource 3 處；PictureContent 無 raw SQL；刪除僅靠 CASCADE；測試 schema 走 EnsureCreated（fixture）+ Migrate（兩個 migration 測試）；docs 6 處描述 |

## 2. 作業總覽

| 作業 | 目標 | 依賴 | 執行 |
|---|---|---|---|
| A | `DbContentWorkSource` 三處改 ExecuteUpdate／投影、累加原子化、中繼資料更新納入錯誤處理 | — | agy |
| D | 拆表：模型＋DbContext → 兩 provider migration（含搬資料）→ 讀寫端改接 → 測試基礎設施 | A | agy（分 4 階段） |
| B | 拆表後普查：所有 Groups/GroupMembers/MessageContents 查詢符合規則；`GetStalenessAsync` 投影；`TrackAsync`/`RefreshStale…` 精簡 | D | agy |
| C | 文件：README 資料表段、ENCRYPTION 對照表、DEPLOYMENT-GUIDE 升級段、CLAUDE.md 資料層硬規則 | B | Claude |
| E | 體檢輪：獨立審 diff、跑全測試、雙 provider snapshot 一致 | C | Claude |

## 3. 作業明細

### 作業 A-階段 1：`DbContentWorkSource` 三個方法不再載入 `MessageContent` 實體
- **背景**：`CompleteAsync`／`FailAsync`／`GetAsync` 以完整實體操作，`Content` 會被整份讀進記憶體；同檔 `RevertClaimAsync` 已示範正確作法。
- **契約**：
  - `GetAsync` 只回傳 `ContentWorkItem` 所需四個值；`GroupMessage` 不存在或狀態非 Pending 時回 null（行為不變）。
  - `CompleteAsync` 收尾的四個純量欄位（ContentType/DownloadStatus/CompletedAt/FailedAttempts=0）以單一 UPDATE 語句完成，不經 change tracker；此更新失敗時要記 log 並往外拋，讓呼叫端走既有失敗路徑（不做 RevertClaim）。
  - `FailAsync` 以單一 UPDATE 完成，`FailedAttempts` 用 SQL 端 `+1`（原子）。
  - 不動 blob 串流寫入區塊與 provider 分支。
- **範圍**：`MessageService.Web/Services/DbContentWorkSource.cs`、`MessageService.Web.Tests/Services/DbContentWorkSourceTests.cs`。不動 docs、不動其他檔。
- **驗收**：build 零警告、`dotnet test` 全綠；新增測試：
  - `FailAsync_ConcurrentCalls_IncrementsAttemptsAtomically`：同一列並發呼叫兩次 FailAsync，FailedAttempts = 2。
  - `CompleteAsync_DoesNotTrackContentEntity`：完成後 `ChangeTracker.Entries<MessageContent>()` 為空。
  - `GetAsync_NonPending_ReturnsNull`／`GetAsync_MissingGroupMessage_ReturnsNull`（若既有已覆蓋則標註沿用）。
  - grep：`DbContentWorkSource.cs` 內不得再出現 `MessageContents.FirstOrDefaultAsync` 或 `MessageContents.FindAsync`。
- **回報格式**：改了哪些檔、測試總數／綠／紅、偏離契約處。

### 作業 D-階段 1：模型與 DbContext
- **背景**：三個 blob 欄位掛在常被整份載入的實體上，是連續五輪同型 bug 的根因。
- **契約**：
  - 新增實體 `GroupPicture`、`GroupMemberPicture`、`MessageContentBlob`，主鍵即外鍵、`Content` 為 required byte[]、`DeleteBehavior.Cascade`、主鍵 `ValueGeneratedNever`。
  - 父實體移除 `PictureContent`／`Content` 屬性，改為導覽屬性 `Picture`／`Blob`（名稱可自訂但需一致）。
  - SQLite 上 `MessageContentBlobs.MessageContentId` 必須是 rowid 別名（`INTEGER PRIMARY KEY`），`SqliteBlob` 才能以 Id 開啟。
  - 加密責任不變（應用層 `ChunkedBlobCipher`），`ApplyFieldEncryption` 不對新表套 ValueConverter。
- **範圍**：`MessageService.Data/Models`、`MessageService.Data/Data/MessageDbContext.cs`。此階段允許暫時編譯失敗於 Web 專案？**不允許**——請同時做最小接線讓整個 solution 可 build（可以先用 `Picture?.Content` 之類的過渡寫法，下一階段再收斂），但不得改 raw SQL（下一階段）。
- **驗收**：`dotnet build` 零警告；既有測試允許暫紅但要列出紅的清單與原因（下一階段負責轉綠）；兩 provider `HasPendingModelChanges` 測試預期紅（migration 尚未加）。

### 作業 D-階段 2：兩 provider migration（含搬資料）
- **契約**：
  - Sqlite 與 SqlServer 各一個 migration `SplitBlobTables`：建三張新表 → 從父表 `WHERE Content IS NOT NULL` 搬資料 → 移除父表舊欄。SqlServer 用 `varbinary(max)`；Sqlite 用 `BLOB`。
  - Down 要能反向搬回（可接受效能差）。
  - snapshot 更新，`HasPendingModelChanges` 兩 provider 皆 false。
  - Sqlite 端 `MessageContentBlobs` 建表 SQL 需為 `INTEGER NOT NULL … PRIMARY KEY`（rowid 別名）。
- **範圍**：`MessageService.Data/Data/Migrations/{Sqlite,SqlServer}/`、`MessageService.Web.Tests/Services/`（新增 migration 測試）。
- **驗收**：
  - `MessageDbMigrationsConsistencyTests` 兩測試綠。
  - 新增 `SplitBlobTablesMigrationTests`（Sqlite 實跑）：先 migrate 到 `AnonymousLabelUnique`、塞含 blob 的 Group／GroupMember／MessageContent 各一列與各一列 null blob → `Migrate()` → 斷言新表列數＝非 null 數、內容 bytes 相同、父表舊欄已不存在、刪父列後 CASCADE 帶走子列。
  - `LegacySqliteBaselinerTests.EnsureBaseline_ThenMigrate_ProducesSameSchemaAsFreshMigrate` 綠。
  - 產出 `dotnet ef migrations script <prev> SplitBlobTables --context SqlServerMessageDbContext` 的 SQL 存到 `.gemini-tasks/` 供人工核對（回報時附路徑）。

### 作業 D-階段 3：raw SQL 與串流讀寫端改接
- **契約**：
  - `DbContentWorkSource.CompleteAsync`：blob 寫入改為對 `MessageContentBlobs` 新增列（SqlServer 一句 INSERT 串流參數；SQLite `INSERT … zeroblob(@len)` 後 `SqliteBlob` 填入）。重試（列已存在）時先刪後插或 UPDATE，行為需明確且測試覆蓋。
  - `FailAsync`／`RevertClaimAsync` 之外新增：失敗時**刪除**已存在的 blob 列（根治 zeroblob 殘骸）；`GetPendingIdsAsync` 註解同步更新。
  - `ContentStreamService` 8 處 SQL 改指向新表；表名／欄名集中成常數；Range／SequentialAccess／表頭讀取／長度查詢行為不變。
  - `AvatarsController`、`DbProfileStore.ApplyPicture`、`IngestController` 頭貼寫入改走導覽屬性；`HasPicture` 以 `Picture != null` 投影。
- **範圍**：`MessageService.Web/Services/{DbContentWorkSource,ContentStreamService,DbProfileStore}.cs`、`Controllers/Api/AvatarsController.cs`、`Controllers/IngestController.cs`、對應測試檔。
- **驗收**：全測試綠（含 `ContentStreamServiceTests`、`ContentDownloadService*Tests`、`AvatarsControllerTests`）；新增：
  - `CompleteAsync_RetryAfterPartialWrite_ReplacesBlob`（SQLite）：模擬中途失敗留下 blob 列，再次 Complete 內容正確、只一列。
  - `FailAsync_RemovesOrphanBlobRow`。
  - grep：`"MessageContents"` 字面量不得再與 `Content` 欄位讀寫共用（只允許出現在中繼資料查詢）。

### 作業 D-階段 4：測試基礎設施與 seed
- **契約**：`WebAppFactoryFixture.SeedAsync` 呼叫端與所有 `PictureContent = bytes` 的測試 seed 改為導覽屬性；不得改變測試斷言意圖。
- **範圍**：`MessageService.Web.Tests/**`。
- **驗收**：全測試綠；grep 測試專案不得出現 `PictureContent =`／`Content = new byte`（除新實體建構）。

### 作業 B-階段 1：普查與熱路徑精簡
- **背景**：拆表後父實體已輕，但仍要把「只需幾個純量卻整份載入實體」的熱路徑收斂，並確立規則可 grep。
- **契約**：
  - `DbProfileStore.GetStalenessAsync` 改投影（含 `HasPicture = Picture != null`）。
  - `RetentionCleanupService.RefreshStaleGroupPointersAsync` 迴圈內改單一 UPDATE。
  - `GroupLastMessageTracker.TrackAsync`：保留與 ingest 同 DbContext 與 SaveChanges 節奏；先以投影判斷存在與 `LastMessageId`，不存在才 Add stub，存在且需更新時以 Attach 空殼標記兩欄位。既有 catch／重試行為不變。
  - `UsersController` 成員列表、`MessagesController` 群組字典改投影（只取用到的欄位）。
  - 為 `GroupLastMessageTracker` 補專屬測試檔。
- **驗收**：全測試綠；新增 `GroupLastMessageTrackerTests`：新群組建 stub、既有群組更新指標、較舊訊息不回退、tracker 不留下完整 Group 實體；`DbProfileStoreTests` 補 `GetStalenessAsync_DoesNotLoadPicture`。

### 作業 C（Claude）：文件
- README「共用資料表」加三張新表、刪 `PictureContent varbinary(max)` 列、設計決策備忘改寫第 1-2 點。
- `docs/ENCRYPTION.md` 對照表改表名。
- `docs/DEPLOYMENT-GUIDE.md` 155-181「既有 SQL Server 升級」補：本 migration 會複製全部 blob → 需約兩倍空間與 log；建議維護時段；升級後可 `DBCC SHRINK`／SQLite `VACUUM` 回收。
- `docs/LINE-BOT-SETUP.md` 242 行診斷 SQL 檢查是否需改。
- 專案 `CLAUDE.md` 新增「資料層規則」：blob 只在三張 *Pictures/*Blobs 表；任何父表查詢預設不 Include blob；改狀態純量用 ExecuteUpdate；raw SQL 表名用常數。

### 作業 E（Claude）：體檢
- 獨立審全 diff、重跑全測試、雙 provider snapshot、對照本文件契約；核對 SqlServer migration script。

## 4. 測試計畫
見各階段驗收；總覽：A 3 條、D-2 migration 實跑 1 檔、D-3 2 條、B 5 條。

## 5. 文件更新
作業 C；全部驗收後由 Claude 執行，結案依 docs-current-vs-history 搬進 history。

## 6. 風險與回滾
- SqlServer migration 搬大量 blob：單一交易、log 暴漲。緩解：文件警示＋維護時段；Down 可回滾。
- `SqliteBlob` 需 rowid：若 EF 產生的建表 SQL 非 rowid 別名，串流會炸 → D-2 驗收明列，D-3 測試實跑覆蓋。
- Edge 模式無 DB 不受影響；Viewer 只讀新表。
- 每作業獨立 commit，可逐一 revert。

## 7. 執行紀錄

基準：dev@da56cde，692 測試綠。最終：717 綠、build 零警告。

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 DbContentWorkSource | agy | 完成 | 701 綠 | agy 把 `ILogger` 改成 optional + NullLogger 以迴避測試 DI，違反全專案「必填」慣例；Claude 改回必填、測試端補 `AddLogging()`。另補回被刪掉的 `FailedAttempts=0` 原因註解、log 訊息改繁中 |
| D-1 模型與 DbContext | agy | 完成 | build 零警告，測試 251 紅（migration 未加，預期內） | 無 |
| D-2 兩 provider migration | agy | 完成 | migration 四支測試綠，其餘 72 紅皆為 raw SQL 未改（預期內） | agy 逾時中斷未回報摘要，以 git diff 與自跑測試驗收。`LegacySqliteBaselinerTests` 5 紅不屬預期類別：測試用「現行 schema 砍欄位」模擬舊版庫，新表與已移除欄位要同步維護；Claude 補上三張新表的 DROP，agy 後續再補 `ADD COLUMN Content BLOB`（更貼近真實舊庫） |
| D-3 raw SQL 改接 | agy | 完成 | 704 綠 | 無。順手收掉一句冗贅註解 |
| D-4 測試 seed | — | 隨 D-1 完成 | grep 無 `PictureContent =` 殘留 | 無 |
| B-1 熱路徑收斂 | agy | 完成 | 710 綠 | `TrackAsync` 的 Attach 空殼會留在 change tracker，Claude 補註解記錄「同一 DbContext 之後不可讀 Group 其他欄位」的約束 |
| E 體檢 | Claude | 完成 | 見下 | 揪出一項真漏網（見 §8） |
| E-修正 頭貼 upsert | agy | 完成 | 717 綠 | agy 把 `ApplyPicture` 拆散成 4 份重複程式碼，Claude 收斂回 `UpsertPictureRow` + `ApplyPictureMetadata` 兩個 helper |
| C 文件 | Claude | 完成 | — | README／ENCRYPTION／DEPLOYMENT-GUIDE／LINE-BOT-SETUP／CLAUDE.md |

## 8. 體檢輪發現

| 項目 | 判定 |
|---|---|
| `DbProfileStore` 的 upsert 仍 `Include(... .Picture)` | **真漏網，已修**。這是本輪拆表唯一漏掉的整份載入點，而且無條件執行——即使這次不換頭貼（`PictureBytes == null`）也照樣把舊圖整份撈進記憶體，`ProfileRefreshService` 每次刷新都走一次 |
| CASCADE 鏈（Blob → MessageContents → GroupMessages） | 兩 provider 皆完整，保留期清除的 `ExecuteDelete` 靠它帶走 blob |
| 兩份 ModelSnapshot、SqlServer script | 與 migration 一致，Up 順序正確、只搬非 NULL、Down 對稱 |
| `TrackAsync` 空殼被 `DbProfileStore` 撿到的風險 | 目前無任何呼叫路徑會在同一 `DbContext` 內交會；即使發生也不會寫錯資料（被寫的欄位都明確覆寫）。屬設計脆弱點而非缺陷，已用註解與 `CLAUDE.md` 規則釘住 |
| `AvatarsController` 解密時 2× 記憶體峰值 | 不處理。頭貼上限 2MB，且常態路徑已被 304 短路 |
| 死程式碼 | 無殘留（`EncryptPictureContent`、`PictureBytes` 都仍在用） |

## 9. 待人工驗收

- SQL Server 的 `SplitBlobTables` migration **沒有在真實 SQL Server 上跑過**（本機無實例）。
  已產生 script 供人工核對，內容與 Sqlite 版同構、與 EF 產生的 Up 一致。
  正式環境升級前請先在測試庫跑一次，並依 `docs/DEPLOYMENT-GUIDE.md` 的兩倍空間與
  空間回收提醒安排維護時段。
- 本輪只在 SQLite 上驗證過 `SqliteBlob` 對新表的 rowid 存取。
