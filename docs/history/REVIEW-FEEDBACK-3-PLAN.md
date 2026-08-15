# 審查回饋第三輪：效能與體驗

外部審查提出 16 項效能與體驗問題，逐項核對原始碼後**全部屬實、無誤報**。
15 項已實作，1 項（全文檢索索引）因技術前提不成立而記為已知限制。

實作分 11 段委派給 Antigravity CLI（agy）執行，每段各自驗收後才開下一段。
測試從 628 增加到 674，全綠、0 警告。

## 本輪定案的四個前提

| 項目 | 決定 |
|---|---|
| 正式環境加密 | 未啟用。加密路徑仍要正確，但不為它做大工程 |
| 加密模式搜尋範圍 | 維持只掃最新 300 則，改由前端說明原因，不做分頁掃 |
| 頭貼快取 | 保留 `no-cache`，只消除多餘 DB 讀取。不引入 `max-age` |
| 遮蔽規則快取 | 30 秒 `IMemoryCache`，接受拆機部署下非寫入端的漂移窗口 |

## 有別的選項但沒選的地方

### 頭貼快取不改 `max-age`

審查建議未加密的頭貼改 `private, max-age=3600`，理由是 `no-cache` 讓每次頁面載入都要對
每張頭貼送一次 conditional request。

**沒採用**。ETag 的組成是 `avatar-{id}-{PictureUpdatedAt}`，**不含 `NameDisplayMode`**。
改成時間式快取之後，管理者把顯示模式切到匿名，已經快取在值班電腦瀏覽器裡的真實頭貼
在有效期內仍然讀得出來——這是隱私回歸，不是效能改善。

實際的成本也不在 revalidation 本身：原本的程式碼**先把整張圖撈出來、才判斷
`If-None-Match`**，所以每次回 304，blob 都白讀一次。改成「先只投影 `PictureUpdatedAt`
算 ETag、比對通過直接回 304」之後，304 路徑完全不碰 blob，200 路徑多一次輕量查詢。
因為 `no-cache` 讓 304 成為常態，這筆交易划算。

### 遮蔽規則快取的 TTL 是 30 秒，不是更長

`MaskingService.LoadRulesAsync` 每次 3 次查詢，被訊息輪詢（3 秒）、側欄輪詢（10 秒）、
搜尋、以及**每一張頭貼**呼叫。30 秒足以吃掉絕大部分重複，同時把拆機部署的規則漂移
控制在可接受範圍。漂移窗口寫在 [DEPLOYMENT-MODES.md](../DEPLOYMENT-MODES.md) 的已知限制。

### 頭貼刷新的抑制窗口是 5 分鐘，不是 `ProfileCacheOptions.RefreshAfter`

原始規劃寫「TTL 取 `RefreshAfter`」（預設 7 天）。**這個設計是錯的**：
`GetStalenessAsync` 回報「不 stale」只代表 `UpdatedAt` 落在 7 天內——可能是 6 天前更新的，
再過 1 天就該刷新。若據此記成「未來 7 天都不用查」，那筆資料會被延後將近一整個週期才
刷新，等於把 TTL 悄悄變成最長兩倍。

真正要解決的痛點是「同一個人連發一串訊息、每則都查一次 staleness」——Edge 模式下那是
**每則訊息一次 HTTP round-trip 打到 Core**，序列執行。那是秒級到分鐘級的突發，
5 分鐘固定窗口就能吃掉絕大部分重複，而對 7 天刷新週期最多只延後 5 分鐘。

### `/healthz` 不用 `AddHealthChecks()` 框架

兩支極簡端點不值得引入 `IHealthCheck` 註冊表、JSON 回應格式那一整組抽象。用 `MapGet`。

`/healthz/ready` 在沒有資料庫的 `Edge` 模式**直接回 200 而不是 404**：那台主機的「就緒」
本來就不依賴本機資料庫，回 404 會讓監控把「這個模式沒有這個概念」誤判成故障，
統一回 200 才能讓所有部署模式共用同一份監控設定。

### 搜尋回應改包裝物件，不用自訂 HTTP 標頭

加密限制的旗標可以用 `X-Search-Limited` 標頭傳，完全不動 API 形狀（代價是 17 處既有測試
不用改）。**沒採用**：「搜尋範圍受限」是回應語意的一部分，放在 body 前端才會把它當一等
公民處理；標頭容易被中間層剝除，而且 `fetchJson` 目前不回傳 header，改它會波及所有呼叫點。

### 前端不做虛擬化、不砍舊 DOM 節點

審查建議「prepend 累積超過門檻就砍掉尾端節點」。**沒採用**：被砍掉的節點若還有 pending
內容，狀態輪詢會找不到對應 DOM，那些圖會永遠卡在轉圈圈。牽動 `state.newestId` 與
`state.pendingContentIds` 的一致性，成本與風險不成比例。只做 CSS 佔位高度（`min-height: 8rem`）
解決捲動跳動——lazy load 的圖片在載入前高度是 0，`prependMessages` 同步算捲動位置時會算錯。

## 驗收抓到什麼

### 我的規格寫錯，做出功能回歸（`?read=` 收斂）

規格要求 `readQuerySuffix` 只送 `state.groups` 裡有的群組。agy 照做，語法完全正確。

但 `loadGroups` 的執行順序是「先呼叫 `readQuerySuffix()` 組網址 → 才把回應寫進
`state.groups`」，所以**第一次載入時 `state.groups` 還是空陣列**，過濾後一個基準都送不出去。
而 `GroupsController` 的語意是「沒帶基準的群組視為全部已讀」——結果是重新整理頁面後
整排未讀 badge 歸零，要等 10 秒後的下一輪輪詢才回來。

修法：`knownGroupIds.size === 0` 時退回送全部。`readState` 本來就被
`seedReadStateForNewGroups` 清成只含現存群組，長度受群組數限制，退路不會讓查詢字串失控。

**根因是我沒驗證規格裡的呼叫時序**，不是 agy 的問題。涉及既有函式呼叫順序的規格，
要先把呼叫鏈讀一遍。

### 資源釋放的連帶退步（`ContentStreamService`）

把 SQLite 與 SQL Server 兩條分支合併成同一個 `onDiskStream` 變數時，
SQL Server 路徑下 `reader.GetStream(0)` 拿到的 stream 不再被 dispose（原本有
`await using`），finally 裡只收了 reader。SQLite 那條由 `await using` 管 `SqliteBlob`
沒問題。**測試全部跑 SQLite，所以測試不會抓到**。

### 白名單外的必要修改

`EncryptionEndToEndTests.cs` 也消費搜尋 API，形狀改了不跟著改就編譯失敗。
規格白名單漏列了它——列白名單前要 grep 全 repo 找出所有消費點，不能只 grep 主測試檔。

### 突變測試證實測試不是假綠

每段驗收都對關鍵斷言做一次突變，確認測試真的抓得到：

| 段 | 突變 | 失敗數 |
|---|---|---|
| B1 遮蔽規則快取 | 拿掉 controller 的 `InvalidateCache()` | 1 |
| B2 頭貼 ETag | 讓 `If-None-Match` 比對永遠不命中 | 2 |
| G4 健康檢查 | 拿掉 `/healthz` 的白名單排除條件 | 2 |
| C SQLite Range | 兩處 `Seek` 位移改成 0 | **20** |
| E 頭貼刷新去重 | 拿掉短路判斷 | 1 |

## 踩到的坑（不要改回去）

- **`.msg-image` 的 `min-height` 不能拿掉**。沒有它，往前捲動時每張 lazy load 的圖片
  載完都會把後面內容往下推，畫面一路往下彈。
- **`maybeExitHistoricalView` 一定要把 `state.newestId` 交棒給 `windowNewestId`**。
  不交棒的話，即時輪詢會從舊基準重抓一次，畫面出現重複訊息。
- **`afterId` 的回應不帶 `latestId` 也不帶有意義的 `hasMore`**（那兩個欄位只在初載計算），
  所以「是否追上最新」只能靠側欄的 `lastMessageId` 判斷。
- **`WebAppFactoryFixture.SeedAsync` 結尾的 `InvalidateCache()` 不能拿掉**。它直接寫資料庫、
  繞過 `SettingsController` 的失效呼叫；測試若在 seed 之前先打過任何載入規則的端點，
  之後 seed 的遮蔽設定不會生效，而且只在特定先後順序下才發生，失敗訊息看不出原因。
- **SQLite 的 Range 請求不能用 `substr()`**。它是 SQL 純量運算式，SQLite 必須先物化整個
  blob 值才能切片；拖影片進度條或語音 seek 時，每次 Range 都會把整支檔案讀進記憶體。
  用 `SqliteBlob`（可 Seek 的 `Stream`）。SQL Server 的 `SUBSTRING` 原生支援部分讀取，
  維持原樣。
- **`GetStatuses` 的數量上限與前端 `STATUS_POLL_BATCH_SIZE` 必須一致**（都是 100）。
  `?ids=` 超過 IIS 預設的 `maxQueryString`（2048）時回的是 404.15，應用程式 log 什麼都
  看不到，而且失敗後那些圖會永遠卡在轉圈圈——`pendingContentIds` 的移除只發生在成功
  回應的路徑上。

## 未做的：全文檢索索引（審查 #12）

`Text` 欄無索引，`LIKE '%q%'` 是全表掃描，這一項屬實。但**全文檢索索引在中文情境下
取代不了它**，實測（SQLite 3.53.3，`Microsoft.Data.Sqlite` 內建版本）：

| tokenizer | 查「腳踏車」(3字) | 查「踏車」(2字) | 查「今天」(2字) |
|---|---|---|---|
| `unicode61`（FTS5 預設） | **0 命中** | 0 命中 | 0 命中 |
| `trigram` | 1 命中 | **0 命中** | **0 命中** |

- `unicode61` 把連續中日韓字元當成單一 token，不分詞，中文查詢一律零命中。
  照原規劃實作會做出一個完全查不到東西的索引。
- `trigram` 可用，但顧名思義只索引 3 字元序列，**查詢字串少於 3 個字就用不到索引**。
  中文最常見的正是 2 字詞（「今天」「發票」「請假」）與單字姓氏（「王」）。

也就是說，照規劃實作 FTS 會讓 2 字以下的查詢從「搜得到」變成「搜不到」，是功能退步。
SQL Server 的 `CONTAINS` 是詞彙比對而非子字串比對，有同樣的問題，還要額外裝
Full-Text 元件與中文斷詞器。

要真正覆蓋 2 字查詢只能自建 n-gram 索引表（每則訊息拆成 N 個 2-gram 存輔助表），
代價是資料庫明顯膨脹。**本輪決定不做**，記為已知限制寫進
[README.md](../../README.md) 的「訊息搜尋」一節。日後若搜尋效能成為實際問題，
n-gram 輔助表是唯一能同時滿足「中文」與「子字串」的路線。

## 其他已知限制

- **保留期清除不會讓 SQLite 檔案縮小**。刪資料只把 page 標成 free。清除完成時 log 會記一則
  Warning 附上目前檔案大小，人工回收的步驟寫在
  [DEPLOYMENT-GUIDE.md](../DEPLOYMENT-GUIDE.md) 的「保留期清除與磁碟空間」。
  `PRAGMA incremental_vacuum` 不適用——它要求資料庫建立時就設 `auto_vacuum=INCREMENTAL`，
  既有資料庫改不了。
- **前端沒有自動化測試框架**。批次 F 的四項改動靠瀏覽器實測驗收（dev server ＋ 真實資料），
  沒有回歸測試保護。
- **`?read=` 的長度保護在群組數暴增時才會用到**。目前群組 20 以內，約 45 組才會撞上
  2048 上限。
