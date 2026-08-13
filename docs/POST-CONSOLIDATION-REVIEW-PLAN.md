# 部署收斂後外部審查回饋輪——實作規劃

> 狀態：規劃完成，待實作。
> 依據：外部審查（針對 dev@9b2451c）逐項查證結果——P0、B1～B7 與「其他觀察」全數屬實。
> 已定案的四個方向：健康監測採「DB 心跳＋設定頁顯示」；ENC1/MSE1 現在加 key id；
> UseForwardedHeaders 實作 KnownProxies；本輪全包含文件。

## 查證結論與審查修法的三處修正

1. **P0 屬實**，但審查漏了一個關鍵細節：outbox.db 走 `EnsureCreated()` 不走 migrations，
   在 `OnModelCreating` 加唯一索引只對全新檔案生效——既有 outbox.db（可能已含重複列）
   必須由 `OutboxSchemaUpgrader` 先去重再建索引，否則已卡死的現場升級時建索引直接失敗。
2. **B1 的 `Local\` 替代方案不可行**：ACL 檢查在具名物件本身，不在命名空間。
   採 catch `UnauthorizedAccessException` 降級（KISS），不引入 MutexAcl 套件。
3. **B7 不需要 .jsonc**：ASP.NET Core 的 JSON 設定載入器預設允許 `//` 註解，
   四份樣板直接寫行內註解。

另外確認：AllInOne 拓撲不受 P0 影響（`DirectIngestSink` 不經過 `ToDictionary` 那行，
重複列會自癒）；只有 Edge→Core 的 HTTP 路徑會永久卡死。

---

## 批次A：P0——outbox 重複事件讓批次 ingest 永久 500

**A1　`IngestController.SubmitEventsBatch` 容忍重複鍵**
`envelopes.ToDictionary(e => e.WebhookEventId)` 改為
`GroupBy(e => e.WebhookEventId).ToDictionary(g => g.Key, g => g.First())`。
同一 id 的兩筆 envelope 內容相同（同一事件的重送），取第一筆即可。

**A2　outbox 唯一索引（兩層）**
- `OutboxDbContext.OnModelCreating`：`entity.HasIndex(e => e.WebhookEventId).IsUnique()`（管全新檔案）。
- `OutboxSchemaUpgrader` 新增 `EnsureWebhookEventIdUniqueIndex(connectionString)`（管既有檔案）：
  1. `DELETE FROM Entries WHERE Id NOT IN (SELECT MIN(Id) FROM Entries GROUP BY WebhookEventId)`
     ——保最小 Id，與 forwarder 的 `OrderBy(Id)` 語意一致；
  2. `CREATE UNIQUE INDEX IF NOT EXISTS IX_Entries_WebhookEventId ON Entries(WebhookEventId)`。
  在 `Program.cs` 的 `capabilities.ReceivesWebhook` 區塊、`EnsureDeadLetterColumn` 之後呼叫。

**A3　`SqliteOutboxWriter.EnqueueAsync` 撞唯一索引＝已在 outbox，視為成功**
`catch (DbUpdateException)` 後直接 return（不 `NotifyNewEntry`——該事件已有既存項目在排程中）。
只攔唯一索引衝突情境；其他 `DbUpdateException`（磁碟滿等）原樣往外拋維持回 500 讓 LINE 重送。
判斷方式：檢查 inner `SqliteException.SqliteErrorCode == 19`（SQLITE_CONSTRAINT）。

**A4　`OutboxForwarderService.ProcessBatchAsync` 改 group 語意**
`entriesByWebhookEventId` 由 `Dictionary<string, OutboxEntry>`（索引子覆寫、首列被靜默丟出批次）
改為 `Dictionary<string, List<OutboxEntry>>`：結果回來時同 id 的所有列一起處理
（成功→全部 Remove；永久拒絕→全部死信；未提及→全部退避），一輪自癒。
`envelopes` 對重複 id 只送一份（去重後再送），減少無謂流量。

**A5　測試**
- `IngestControllerTests`：一批含兩筆相同 `WebhookEventId` → 斷言 200 且回應含該 id（現有等價性測試全是唯一 id，蓋不到）。
- `SqliteOutboxWriter`：同 id 入列兩次 → 表內只有一列、不拋例外。
- `OutboxSchemaUpgrader`：預先塞入重複列的舊 schema 檔案 → 升級後只剩最小 Id 那列且唯一索引存在。
- forwarder：預先繞過 writer 塞兩列同 id（模擬升級前殘留）→ 一輪批次後兩列都清掉。

**相容性**：A1 讓新 Core 對舊 Edge 完全相容；舊 Core 對新 Edge 仍有原 bug，
升級順序維持既有指引「先升 Core 再升 Edge」，於文件重申（見批次F）。

---

## 批次B：啟動與部署載具

**B1　migration mutex 降級**
`Program.cs` 的 `new Mutex(...)`／`WaitOne()` 包進 try：
- `AbandonedMutexException` → 照舊視為已取得；
- `UnauthorizedAccessException` → mutex 置 null、`LogWarning`
  「無法取得跨行程 migration 鎖（不同應用程式集區身分），改為不加鎖執行；單站台部署無影響」；
- finally 只在確實持有時 `ReleaseMutex()`。
配合 F1 的 Viewer 樣板 `AutoMigrate:false`，同機多站台的 migration 競爭情境本身也大幅縮小。

**B2　`Set-AppPool.ps1` 兩處**
- `Write-Warning "…" + "…"` 的參數繫結錯誤：字串先組進 `$msg` 變數再 `Write-Warning $msg`。
- 新增 `Set-ItemProperty $poolPath -Name "recycling.disallowOverlappingRotation" -Value $true`
  ——關閉重疊回收，避免過渡期兩個 worker process 同跑 forwarder／下載服務
  （落地端冪等所以無髒資料，但會重複下載媒體並放大 mutex 競爭）。

**B3　UseForwardedHeaders 實作 KnownProxies**
- 新設定鍵 `ForwardedHeaders:KnownProxies`（string[]，IP 位址）與
  `ForwardedHeaders:KnownNetworks`（string[]，CIDR），parse 後填入 `ForwardedHeadersOptions`。
- 開關開啟但兩個清單皆空 → `LogWarning`「預設只信任 loopback，跨機代理的 X-Forwarded-For
  會被忽略，此開關目前等於沒開」。
- parse 失敗（打錯 IP）→ 啟動失敗，比照 `IpAllowlistMiddleware` 對 CIDR 的處理原則
  （安全設定寧可炸啟動，不無聲失效）。
- README／LINE-BOT-SETUP 對應段落改寫：拿掉「留待有真實需求再做」，改為設定說明。

**B4　DeploymentValidator 兩條新警告**
- 空白名單警告條件由 `mode is Core && …` 放寬為
  `capabilities.ViewerEnabled && viewer.AllowedClientIps.Length == 0`（訊息去掉模式字樣），
  涵蓋最常見的 AllInOne（middleware 建構時那條警告要等第一個請求才觸發，啟動期就要講）。
- Provider 不一致警告：`Validate` 增加 `databaseProvider`、`hasSqlServerConnectionString`
  兩個參數（Program.cs 現成資料傳入）——`Provider=Sqlite` 卻設定了 `ConnectionStrings:SqlServer`
  時 `LogWarning`（多半是想切 SQL Server 但忘了改 Provider 鍵）。
  ※ 需求2 的語意定案：**維持顯式 Provider 鍵、預設 Sqlite**，不做「有連線字串就自動切換」
  的隱式推導（殘留設定不該悄悄換資料庫），以這條警告補齊誤設情境。

---

## 批次C：媒體下載認領＋效能

**C1　認領機制（修同 ContentId 並發下載的 blob 交錯寫入）**——**實作落點與規劃不同，見下方「開發時的修正」**
- `DownloadStatus` enum 新增 `Downloading`（存字串，migration 不用改型別）。
- ~~`DbContentWorkSource.GetAsync`：先認領~~ → 改在 `CompleteAsync` 開頭認領（原因見下）。
- `GetPendingIdsAsync`：`Pending` 查詢擴為 `Pending || Downloading`——下載主機重啟時把
  中斷的認領撿回來（`ExecuteUpdateAsync` 把 `Downloading` 重設回 `Pending` 後一併入列）。
  已知限制（與審查一致）：worker 崩潰而行程未重啟時，該筆停在 `Downloading` 直到下次重啟；
  接受，不為此加租約逾時機制（KISS）。

**開發時的修正**：規劃原打算把認領放在 `GetAsync`，實作時才發現這與既有的轉檔輪詢機制衝突——
影片／語音要靠 `GetAsync` 反覆查詢轉檔狀態（`ContentDownloadService.CheckTranscodingAsync`／
`EnqueueDelayed` 重新入列），同一個 worker 對同一筆內容會多次呼叫 `GetAsync`；若在那裡把
狀態從 `Pending` 改成 `Downloading`，第二次查詢會因為狀態不再是 `Pending` 而被自己的認領邏輯
擋下來，`ProcessAsync` 誤判成「這筆不見了」直接放棄，轉檔輪詢整個失效（本地測試直接跑出來：
`ContentDownloadServiceTests` 四個轉檔案例全部失敗）。改為把認領放在 `CompleteAsync` 開頭：
真正需要獨占的是「寫入同一顆 blob」這個動作本身，`GetAsync` 維持原樣（不改狀態，可安全重複
呼叫），`CompleteAsync` 用 `ExecuteUpdateAsync` 把 `Id==contentId && DownloadStatus==Pending`
改成 `Downloading`，`claimed == 0` 時直接 return（不寫入、不覆寫）。這樣既保住轉檔輪詢的重複
查詢語意，也還是防住兩個 worker 同時寫入同一顆 blob 的核心問題。`FailAsync` 不受影響。
測試涵蓋：`CompleteAsync` 第二次呼叫不覆寫已完成的內容、認領失敗時安靜跳過不拋例外。

**C2　`DownloadStatus` 篩選索引**
基底 `MessageDbContext` 加 `HasIndex(c => c.DownloadStatus)`，篩選條件因語法差異
在兩個衍生 context 各自指定：SqlServer `HasFilter("[DownloadStatus] <> 'Completed'")`、
Sqlite `HasFilter("\"DownloadStatus\" <> 'Completed'")`。兩套 provider 各出一個 migration
（與 C1、批次D的心跳表合併成同一個 migration，一輪只加一次 schema 版本）。

**C3　messages.db 啟用 WAL**
`EnableWalMode` 從 `OutboxSchemaUpgrader` 提為共用（或直接複用），`Program.cs` 在
`databaseProvider == "Sqlite" && capabilities.HasDatabaseAccess` 時對 messages.db 連線字串
呼叫一次（Migrate 之後）。持久屬性，設一次即可；涵蓋 Core+Viewer 同機與重疊回收過渡期。

**C4　加密內容不進瀏覽器磁碟快取**
`ContentStreamService`：`cipher.Enabled` 時 `Cache-Control` 由
`private, max-age=31536000, immutable` 改為 `no-store`；未加密維持現狀。

---

## 批次D：健康監測——DB 心跳＋設定頁（需求4＋B6）

**D1　心跳資料表 `HostHeartbeats`**（兩套 provider 同一個 migration，見 C2）
主鍵 `(Role, MachineName)` upsert，欄位：
`Role`（AllInOne/Edge/Core/Viewer）、`MachineName`（`Environment.MachineName`）、
`LastSeenAt`、`OutboxPending`（nullable，僅收 webhook 的主機回報）、
`OutboxOldestAgeSeconds`（nullable 同上）、`EncryptionKeyFingerprint`
（nullable，金鑰 SHA-256 前 8 hex；未啟用加密為 null——順帶解掉審查 A3 的
「金鑰沒對齊要等畫面出現 ENC1: 亂碼才發現」：設定頁比對各主機指紋即可）。
單列 upsert、不成長，不需保留期清除。

**D2　`HeartbeatService`（BackgroundService，所有模式都註冊）**
- 間隔 `Heartbeat:IntervalSeconds` 預設 60；失敗只 `LogWarning`，永不讓例外冒出 `ExecuteAsync`。
- 有 DB 的主機（AllInOne/Core/Viewer）：直接 upsert 自己那列。
- Edge（無 DB）：打新端點 `POST /api/ingest/heartbeat`（掛在 `/api/ingest` 路徑群組之下，
  自動吃既有的金鑰＋IP 白名單中介層），由 Core 代寫；心跳 payload 附上自己查好的
  outbox 統計。沿用 `Ingest:BaseUrl`／`ApiKey`（`IHttpClientFactory`，比照 `HttpIngestSink`
  的標頭作法）。
- outbox 統計：`COUNT(*) WHERE DeadLetteredAt IS NULL` 與 `MIN(CreatedAt)` 換算年齡。

**D3　outbox 積壓告警（B6 的第二半）**
`OutboxForwarderService.LogDeadLetterCountAsync` 的每小時迴圈順帶查
`MIN(CreatedAt) WHERE DeadLetteredAt IS NULL`，年齡超過 `Outbox:BacklogAlertMinutes`
（預設 30）→ `LogError`「outbox 最舊項目已滯留 N 分鐘，排空可能卡住」。
P0 這類無聲卡死從此第一個小時就會在 log 叫出來。

**D4　設定頁顯示**
Viewer 的既有設定頁新增「主機狀態」區塊：每列顯示角色／主機名／最後心跳時間／
狀態燈（<2× 間隔＝正常、<5×＝遲滯、否則離線）／outbox 積壓數與最舊年齡／
金鑰指紋（各列不一致時醒目標示）。純讀 `HostHeartbeats` 表，不打跨主機 HTTP。
※ UI 區塊屬視覺變動——**實作前先問使用者要不要套 ui-ux-pro-max**（全域規則）。

**D5　測試**
心跳 upsert（首寫／更新）、Edge 心跳端點的請求形狀與閘門（比照終檢輪補的 Api 側測試慣例）、
積壓告警門檻、設定頁區塊的狀態燈換算。

---

## 批次E：加密信封 key id（趁尚無正式加密資料）

**E1　key id 定義**：金鑰 SHA-256 的前 4 bytes；文字側用 8 碼小寫 hex，blob 側用原始 4 bytes。
由金鑰自動導出，不新增設定鍵。心跳的 `EncryptionKeyFingerprint`（D1）用同一個值。

**E2　文字欄位（FieldCipher）**：新寫入改 `ENC2:<keyId8hex>:<base64>`；
解密端同時認 `ENC1:`（舊格式，直接試現用金鑰）與 `ENC2:`（比對 keyId，符合才解，
不符合走既有「解不開原樣回傳」路徑——行為與現在拿錯金鑰一致，但 log 能講清楚是
「金鑰指紋不符」而不是解密失敗）。

**E3　blob（ChunkedBlobCipher）**：新 magic `MSE2`＝magic(4)＋keyId(4)＋chunkSize(4)＋明文總長(8)；
讀取端同時認 `MSE1`（舊）與 `MSE2`。`ComputeEncryptedLength` 隨表頭長度調整（僅 MSE2 路徑）。

**E4　本輪只做「信封帶 id＋讀取端雙格式」**；多金鑰解密（`Encryption:RetiredKeys`）留待
真有輪替需求再做——本輪的價值是讓那天到來時舊資料「認得出是哪把金鑰」，不必全庫重加密。

**E5　測試**：新格式 round-trip、舊格式解密不變、keyId 不符時的行為與 log；
ENCRYPTION.md 同步更新格式說明。
**相容性**：單一發佈成品、全拓撲同輪升級即無混版問題；文件註明「啟用加密的環境
升級時所有主機同輪升級」（目前尚未有啟用加密的正式環境，實際風險為零）。

---

## 批次F：樣板與文件（全包含）

**F1　四份 deploy 樣板**
- 全部加 `//` 行內註解：每個鍵為什麼是這個值、跨主機耦合的鍵要跟哪一台對齊
  （`Ingest:AllowedClientIps` 填 Edge 對外 IP 不是辦公室網段、`Ingest:ApiKey` 兩端一致、
  `Encryption:Key` 與 DB 備份分開保管等）。
- Viewer 樣板：`Provider` 改 `SqlServer`（附連線字串範例）、加 `"AutoMigrate": false`、
  註明「schema 由 Core 負責；只有與 Core 同機兩站台時才可改回 SQLite 指同一檔案，
  跨主機共用 SQLite 檔案不可行」。
- AllInOne／Core 維持 Sqlite 預設（符合需求2），註解註明規模到哪該換 SQL Server。

**F2　DEPLOYMENT-GUIDE**
- Part C 補「既有 SQL Server 環境升級注意事項」：`SchemaHardeningRound1` 的多個
  `ALTER COLUMN` 會整表重寫並持 Sch-M 鎖（`MessageContents` 含所有 blob）——
  先估列數、安排維護時段、確認交易紀錄檔空間、先備份。
- 新增 Part I「備份與還原」：SQL Server 備份要點；SQLite 要連 `-wal`/`-shm` 一起處理
  （或先 `PRAGMA wal_checkpoint`）；`Encryption:Key` 與備份分開保管但同時保管
  （金鑰丟失＝加密資料永久不可讀）；還原演練清單。
- 驗收清單改用心跳／設定頁狀態取代「隔天早上翻 log」；重申 Edge/Core 升級順序。

**F3　README**：設定鍵總表補 `ForwardedHeaders:KnownProxies/KnownNetworks`、
`Heartbeat:IntervalSeconds`、`Outbox:BacklogAlertMinutes`；`UseForwardedHeaders` 段落改寫。

---

## 批次G：體檢輪

全案完成後照慣例：建置＋全測試套件（現況 502 綠，本輪淨增測試）、
平行審查（重點：A4 group 語意的邊界、C1 認領與重啟接續的競態、D2 Edge 心跳的
認證標頭與閘門、E2/E3 雙格式讀取的相容矩陣），修正後再終檢一次。

## 實作順序與依賴

A（P0，獨立）→ B（獨立）→ C（C2 的 migration 與 D1 合併）→ D → E（獨立）→ F（收尾）→ G。
批次間無強依賴，A 最優先；migration 全輪只出一個（HostHeartbeats＋DownloadStatus 索引）。
