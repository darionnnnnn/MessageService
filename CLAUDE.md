# MessageService 專案規則

## 文件紀律：現行版本 vs 修改歷程

本專案的 Markdown 文件分成兩層，動筆前後都要遵守：

- **現行文件**：repo 根目錄的 `README.md` 與 `docs/*.md`（`DEPLOYMENT-GUIDE`、`DEPLOYMENT-MODES`、
  `ENCRYPTION`、`LINE-BOT-SETUP`）。只寫「現在就是如此」的事實。
- **`docs/current/`**：進行中輪次的工作文件（計畫、回饋清單、驗收紀錄）。新一輪開工就建在這裡。
- **`docs/history/`**：已完成輪次的過程記錄與決策理由，附 `README.md` 索引。

### 讀取紀律

預設**只讀現行文件**。`docs/history/` 非必要不要讀（先看它的 `README.md` 索引決定要開哪一份），
避免浪費 token。

### 寫作紀律

現行文件不寫「為什麼變成這樣」。定稿前掃一次這些字眼——「原本」「曾經」「之前是」「後來」
「改成／改為」「經過討論」「決定採用」「為了解決」「修正了」「第 X 輪」「回饋」「審查後」
「取代」「棄用」「不再」——抓到就只留動作後的結果、用現在式陳述，把原因與過程挪進
`docs/history/`。真的需要提示背景時只能用一行連結帶過，不可就地展開。

同一主題只在一份文件寫完整版，其他地方改成連結，不要重複解釋。

### 每輪完工的四步收尾

1. `git mv docs/current/XXX.md docs/history/YYYY-MM-DD_XXX.md`（加日期前綴，確認 `git status`
   顯示的是 rename 而非新增＋刪除）。
2. 更新 `docs/history/README.md` 索引，補一列一行摘要。
3. 更新現行文件的「目前狀態」段落，只寫結論。
4. 同步這輪改動導致過時的現行文件內容，並跟程式碼／設定檔核對一次。

## 資料層規則

### blob 只住在三張子表

`MessageContentBlobs.Content`、`GroupPictures.Content`、`GroupMemberPictures.Content` 是全庫僅有的
大 blob 欄位（訊息附檔可達數百 MB，頭貼上限 2MB）。父表 `MessageContents`／`Groups`／`GroupMembers`
上**沒有** blob，可以放心整列撈。規則：

- **父表的查詢不要 `Include` 這三個子實體。** 只需要知道「有沒有」時用 `子實體 != null`
  投影成布林，EF 會翻成 SQL 的存在性判斷，不會把 blob 傳回來。
- 真的要 blob 的路徑只有兩條：`ContentStreamService`（訊息附檔，走 raw SQL 串流 + Range）
  與 `AvatarsController`（頭貼，先比 ETag 走 304，未命中才只投影 `Content`）。
  新增第三條之前先想清楚有沒有必要。
- 寫入 blob 一律不經 EF 的 byte[] 屬性（那樣整份會進 change tracker）：SQL Server 用
  `SqlParameter` 串流參數，SQLite 用 `zeroblob` + `SqliteBlob` 增量寫入。
- `MessageContentBlobs` 的主鍵在 SQLite 上必須維持 rowid 別名（`INTEGER`），
  `SqliteBlob` 靠 rowid 開啟 blob。動到這張表的 migration 時要回頭確認產生的建表 SQL。

### 只改幾個純量欄位就不要載入實體

改狀態、計數、指標這種只動純量欄位的操作，用 `ExecuteUpdateAsync` 直接下 SQL，不要
「查出實體 → 改屬性 → `SaveChangesAsync`」。累加類（例如 `FailedAttempts`）一定要用
SQL 端的 `x => x + 1`，讀出來加一再寫回在併發下會遺失計數。

例外是需要跟呼叫端共用 change tracker／交易邊界的地方（如 `GroupLastMessageTracker`），
那裡改用「Attach 一顆只帶主鍵的空殼 + 標記要改的欄位為已修改」，同樣不載入整列——
但這顆空殼會留在 change tracker 裡，同一個 `DbContext` 之後不可以再讀它的其他欄位
（EF 的 identity map 不會用查詢結果覆寫已追蹤實體的屬性，會讀到假的 null）。

## 開發紀律（重複踩過才寫進來的）

- 測試基線：**1366+ 綠**（`dotnet test`）。改完必須全綠且測試數不減。
- **不要用 `when (ex is not OperationCanceledException)` 過濾例外**——HttpClient 逾時丟的 `TaskCanceledException` 也是它的子類，會被當成「呼叫端取消」放行：在背景服務迴圈裡會穿出 `ExecuteAsync`（預設 `StopHost` 把整站停掉），在失敗計數處會漏算。判斷依據看 token：`catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }` 再接一般 catch（`ContentDownloadService` 的 worker 迴圈是範本）。
- server 端拼 HTML 的頁面（如 `/edge-admin`）**不要寫死根路徑的 action／href／redirect**——IIS 子 application 部署下會 404；一律前置 `Request.PathBase`（HttpClient 方向的同一坑見 `HttpBaseAddress.cs` 註解）。
- **不要為新相依加「可選參數 `= null` ＋ fallback」**——需要就宣告為必要相依，讓 DI 與測試替身誠實跟上（同一形狀在委派實作中出現過三次，全部被驗收退回）。
- **不要用顯示字串做行為判斷**（例如比對中文標籤決定邏輯分支）——加旗標或 enum。
- **Bootstrap modal 在 `show()` 的淡入轉場結束前呼叫 `hide()` 會被直接忽略**——「開啟後依請求結果立刻關閉」的流程，請求快到 300ms 內回來時對話框就永久卡住。初始化時掛一個常駐的 `shown.bs.modal` 監聽，配一個「待補關」旗標：要關就設旗標再 `hide()`，`shown` 看到旗標就補關，開啟前一律清旗標。不要每次呼叫都掛 once 監聽器（`hide()` 有多條提早 return 的路徑，任何一條沒觸發事件就殘留、等下一次開啟才引爆），也不能靠「還有沒有 `show` class」判斷。`hideDeleteModal` 是範本。
- **`ResizeObserver.observe()` 對每個新目標必定送一次初始回呼**——用它做「內容長高就捲到底」時，剛 append 的每一列都會在下一幀觸發一次；觀察範圍要限定在真的該跟隨的列（往下接進來的），`prepend` 的舊訊息列不要觀察，否則「載入更早」會被拉回底部。
- **背景服務的註冊條件要對齊「它產物的消費端」，不是對齊「它資料來源的能力」**——`ProfileBackfillService` 的產物是丟進 `IProfileRefreshQueue` 的工作，若照直覺註冊在 `HasDatabaseAccess`（有資料庫才掃得到候選）下，Core＋`OutboundHere=false` 的拆機拓撲會把它掛到 `NullProfileRefreshQueue` 上：每輪照掃、log 照記「已入列 N 筆」，沒有一筆被消費，單機開發與測試都抓不到。判準是「誰消費它的產物」，`MessageServiceCoreServiceCollectionExtensions.cs` 的 `capabilities.OutboundHere` 區塊是範本。
- **節流／冷卻／TTL 類邏輯注入 `TimeProvider`**（DI 已註冊單例），不要直接用 `DateTimeOffset.UtcNow`——否則時間長度永遠測不到，只能測「有沒有發生」。
