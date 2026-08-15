# 審查回饋第三輪：效能與體驗規劃

外部審查提出 16 項，全部核對原始碼後屬實。本文是實作規劃，尚未動工。

## 前提（本輪已定案）

| 項目 | 決定 |
|---|---|
| 正式環境加密 | **未啟用**。加密路徑仍要正確，但不為它做大工程 |
| 加密模式搜尋範圍 | 維持只掃最新 300 則，**改由前端說明原因**，不做分頁掃 |
| 頭貼快取 | 保留 `no-cache`，只消除多餘的 DB 讀取。不引入 `max-age` |
| 遮蔽規則快取 | 30 秒 `IMemoryCache`，接受拆機部署下非寫入端的漂移窗口 |
| 部署規模 | 群組 20 以內，訊息量大，文字／圖片／貼圖為主，檔案與語音必須可用 |
| 範圍 | 16 項全部本輪完成 |

---

## 批次 A：搜尋端點的實體投影（審查 #1）

**現況** [`MessagesController.Search`](../../MessageService.Web/Controllers/Api/MessagesController.cs)

```csharp
var members = await memberQuery.ToListAsync(cancellationToken);              // :276
var resultMembers = await dbContext.GroupMembers
    .Where(m => resultGroupIds.Contains(m.GroupId))
    .ToDictionaryAsync(m => (m.GroupId, m.UserId), cancellationToken);       // :413
```

兩處都是完整 `GroupMember` 實體，含 `PictureContent`（LINE 頭貼原圖 byte[]）。第一處在
`searchScope='all'` 時是全表。第二處撈的是命中群組的**全部**成員，不是結果裡出現的那幾位。
搜尋是 debounce 打字觸發，連打幾個字就是連續數次。

同檔 `GetMessages` 的成員查詢（:121）已有正確的投影寫法。

**做法**

1. 兩處改成只投影 `GroupId / UserId / DisplayName`。名稱比對與結果組裝都只用到這三欄。
2. 第二處順帶收斂為只查 `top` 裡實際出現的 `(GroupId, UserId)`，而非整個群組的成員。

**驗收**：搜尋結果內容與現行完全一致（含匿名模式的代號解析、姓名命中路徑）；新增一則測試
斷言 Search 不會具現化 `PictureContent`。

---

## 批次 B：高頻端點的重複查詢（審查 #7、#8）

### B1 遮蔽規則快取（#8）

`MaskingService.LoadRulesAsync` 每次 3 次查詢（ViewerSettings、MaskKeywords+Include、UserAliases），
被 `GetMessages`（3 秒輪詢）、`GetGroups`（10 秒輪詢）、`Search`、每支頭貼請求呼叫。

**做法**：包一層 `IMemoryCache`，TTL 30 秒，`SettingsController` 的寫入路徑主動 evict。
`IMaskingRuleSet` 目前是不可變的投影結果，可以安全共用。

**已知取捨**（要寫進 `docs/DEPLOYMENT-MODES.md`）：快取在程序記憶體，Core／Edge 拆機時
非寫入端最長 30 秒後才套用新規則。Full 模式不受影響。

### B2 頭貼端點（#7）

`AvatarsController.ProcessAvatarContent` 目前**先把整張圖撈出來，才判斷 `If-None-Match`**，
所以回 304 時 blob 已經白讀一次；而每支請求最前面的 `LoadRulesAsync` 只為了讀
`RevealsOriginalProfile`。30 個發言者 = 30 次 revalidation × (1 次 blob 讀 + 3 次設定查詢)。

**做法**

1. 拆成兩段查詢：先只投影 `PictureUpdatedAt` 算 ETag，比對 `If-None-Match` 通過就直接回 304，
   完全不碰 blob；沒通過才第二次查詢撈 `PictureContent`。
2. `IMaskingService` 新增輕量方法（只讀 `NameDisplayMode`），頭貼端點改用它。B1 的快取
   命中後兩者成本都趨近於零，但輕量方法讓冷快取路徑也只有 1 次查詢。

**不做**：`Cache-Control` 維持 `private, no-cache`（加密內容維持 `no-store`）。ETag 只含
`PictureUpdatedAt`，不含 `NameDisplayMode`——改成 `max-age` 會讓管理者切換到匿名模式後，
已快取在瀏覽器的真實頭貼在有效期內仍可被讀出，與現有去識別化設計衝突。

---

## 批次 C：SQLite 的 Range 請求（審查 #5）

**現況** [`ContentStreamService`](../../MessageService.Web/Services/ContentStreamService.cs)

```csharp
command.CommandText = isSqlite
    ? "SELECT substr(Content, @start, @length) FROM MessageContents WHERE Id = @id"
```

`substr()` 是 SQL 運算式，SQLite 必須先物化整個 blob 值才能切。拖影片進度條、或語音播放器
seek，每一次 Range 請求都會把整支檔案讀進記憶體再丟掉大部分。非 Range 路徑用
`reader.GetStream(0)` 拿到的才是增量式的 `SqliteBlob`，但 Range 才是媒體播放的主要模式。

**做法**：SQLite 的 Range 路徑改用 `SqliteBlob`（可 `Seek` 的 `Stream`），寫入端
[`DbContentWorkSource:238`](../../MessageService.Web/Services/DbContentWorkSource.cs) 已經在用同一個型別。
`MessageContents.Id` 在 SQLite 是 rowid 別名（見該檔 ETag 段落的註解），可直接當 rowid 用。

兩條路徑都要改：

- 明文 Range（`StreamAsync` 的 `isPartial` 分支）
- 密文 Range（`StreamEncryptedContentAsync` 撈 chunk 連續段那次 `substr`）

`ReadHeaderBytesAsync`（只取 16 bytes）與 `GetContentLengthAsync`（`LENGTH()` 有 SQLite 的
專屬最佳化，不會物化 blob）維持原樣。SQL Server 的 `SUBSTRING` 沒有這個問題，不動。

**驗收**（同時涵蓋使用者要求的「檔案與語音正常」）：對 image / video / audio / file 四種
`messageType`，明文與密文各驗一次——完整下載、`Range: bytes=N-`、`Range: bytes=N-M`、
越界 Range 回 416、0 bytes 內容、跨 chunk 邊界的區間。`file` 型別另外確認
`Content-Disposition: attachment` 與非 ASCII 檔名的 `filename*` 仍正確。

---

## 批次 D：查詢字串長度上限（審查 #3）

`web.config` 只設了 `maxAllowedContentLength`，IIS 預設的 `maxQueryString` 2048 bytes 生效。
超過時 IIS 回 404.15，應用程式的 log 什麼都看不到。

**兩個超長來源**

| 來源 | 估算 | 後果 |
|---|---|---|
| `pollPendingStatuses` 的 `?ids=` | 初載視窗上限 500 則，媒體多的群組（Edge 斷線後一次補上）易達上百個 pending id，`500 × 8 ≈ 4000` | `fetchJson` 拋例外 → `setConnectionOk(false)`，且那些圖**永遠卡在轉圈圈**，狀態輪詢再也回不來 |
| `pollGroups` 的 `?read=` | 每組 `{33 字元 groupId}:{id}`，`encodeURIComponent` 把 `:` `,` 各撐成 3 字元 → 約 42–45 字元／組，**約 45 組**破線 | 側欄輪詢永久失敗 |

群組數 20 以內，`?read=` 目前有安全邊際；仍一併處理，因為它是靜默失效。

**做法**

1. `pollPendingStatuses` 分批，每批 100 個 id 送一次請求。
2. `readQuerySuffix` 只送目前渲染在畫面上的群組。
3. 後端 `GetStatuses` 對 `contentIds.Count` 設上限（與前端批次大小一致），超過回 400——
   目前是一個沒有任何驗證的 IN 清單。
4. `web.config` 補 `<requestLimits maxQueryString="8192" maxUrl="8192" />` 當保險，並比照
   `maxAllowedContentLength` 的既有註解寫明它對應前端的哪個批次大小。

---

## 批次 E：頭貼刷新佇列去重（審查 #4、#13）

**現況**：`IngestSideEffects.Apply` 對每筆 envelope 無條件 `profileRefreshQueue.Enqueue`，
`ProfileRefreshService` 單一 consumer，`ProcessAsync` 開頭必打一次 `GetStalenessAsync`。

- Full／Core：每則訊息一次 DB 查詢。浪費但可接受。
- **Edge（`Line:OutboundHere=true`）：每則訊息一次 HTTP round-trip 到 Core**，序列執行。
  同一人連發 50 則就是 50 次一模一樣的 staleness 查詢。Channel 是 unbounded，積壓只會拖更長。

`_failureCooldowns` 只在成功時 `TryRemove`，失敗過但之後再無訊息的 key 永久殘留（單例長期執行）。

**做法**：在 `ProfileRefreshService` 內加一張 `(GroupId, UserId) → 下次可查時間` 的記憶體表，
命中就整筆丟棄，根本不進 `GetStalenessAsync`。

- TTL 取 `ProfileCacheOptions.RefreshAfter`（預設 7 天）。
- 只有在 staleness 查詢回報「不 stale」或 upsert 成功之後才寫入下次可查時間；失敗路徑不寫，
  維持 `FailureRetryAfter` 既有的冷卻語意。
- 兩張表（TTL 表與 `_failureCooldowns`）合併成單一結構，過期條目在寫入時順手掃除，一併解掉
  #13 的緩慢洩漏。

在 queue 端去重會需要跨 producer 的共享狀態，放在 consumer 內較乾淨，也讓 Null 實作維持不變。

---

## 批次 F：前端體驗（審查 #9、#10、#11、#2）

### F1 圖片佔位高度（#9）

`.msg-image` 只有 `max-height: 16rem`，沒有 `min-height` 或 `aspect-ratio`，且掛
`loading="lazy"`。`prependMessages` 結尾的

```js
list.scrollTop = previousScrollTop + (list.scrollHeight - previousScrollHeight);
```

是同步計算的，此時新插入的圖片高度都還是 0；使用者往上捲、圖片陸續進入 viewport 才載入，
畫面就一路往下彈。

**做法**：給 `.msg-image` 一個 `min-height`（8rem 量級）保留位置。`.msg-sticker` 已是固定
`7.5rem × 7.5rem`，不需要處理。

**不做**：清單虛擬化，以及「prepend 累積超過門檻就砍掉尾端節點」——後者會牽動
`state.newestId` 與 `state.pendingContentIds` 的一致性（被砍掉的節點若還有 pending 內容，
輪詢會找不到對應 DOM），成本與風險不成比例。若日後仍有掉幀再單獨評估。

### F2 歷史檢視的「載入更新」（#10）

`jumpToSearchResult` 用 `aroundId` 取錨點前後各 250 則。往上有「載入更早」，**往下沒有任何
入口**——捲到底就停住，唯一出路是「回到最新」直接跳回即時畫面，中間內容看不到。

**做法**：後端已有 `afterId`。前端在 `historicalView` 時加一顆「載入更新」按鈕（並在捲到底時
自動觸發），走 `afterId=` 目前視窗最新一則的 id；接到 `latestId` 時自動退出 `historicalView`、
恢復即時輪詢。需要在 state 裡追蹤視窗內最新 id（目前 `state.newestId` 在歷史檢視下語意是
「即時基準」，兩者要分開）。

### F3 `truncated` 提示（#11）

`MessagesPageDto.Truncated` 後端算好也序列化了，`chat.js` 從頭到尾沒讀過。視窗內超過 500 則
被截斷時，使用者看到的是一段憑空開始的對話。

**做法**：`renderWindow` 與 `jumpToSearchResult` 在 `page.truncated` 時，於清單頂端插一條
「此區間訊息過多，僅顯示最近 500 則」的分隔提示。與 `hasMore`（還有更早的訊息）語意不同，
兩者可同時出現。

### F4 加密模式的搜尋範圍說明（#2）

加密啟用時 `Take(SearchCandidateLimit)` 套在關鍵字比對**之前**，所以實際只檢查最新 300 則
文字訊息，`SearchWindowDays=14` 發揮不了作用，使用者搜三天前的內容會得到「找不到符合的訊息」
而毫無提示。本輪決定不改行為，改為讓使用者知道原因。

**做法**

1. `Search` 的回應從裸陣列改為包裝物件，帶 `results` 與 `limitedByEncryption` 旗標
   （旗標僅在加密啟用時為 true）。**這是 API 形狀變更**，`Search` 的既有測試要一併調整。
2. 前端在旗標為 true 時，於搜尋面板頂端常駐一行說明：內容已加密，僅能搜尋最近 300 則文字
   訊息，建議指定群組縮小範圍。零結果時也顯示，避免誤判為「真的沒有」。

正式環境未啟用加密，這條在目前部署下不會出現，屬於防止日後啟用時的靜默失效。

---

## 批次 G：維運面（審查 #6、#14、#15、#16）

### G1 SQLite 保留期清除不回收空間（#6）

`RetentionCleanupService` 分批 `ExecuteDeleteAsync` 沒問題，但 SQLite 刪資料只把 page 標成
free，檔案大小不變。三年保留期到期清掉數十 GB 影片後，磁碟一個 byte 都不會還回來。

**限制**：`PRAGMA incremental_vacuum` 需要建庫時就是 `auto_vacuum=INCREMENTAL`，現存的
`messages.db` 改不了，只能整庫 `VACUUM` 重建（需要等同 DB 大小的暫存空間、期間全鎖）。
因此本輪**不做程式自動回收**。

**做法**

1. `RunCleanupAsync` 在 `totalDeleted > 0` 且 provider 是 SQLite 時，log 明確寫出
   「已刪除 N 筆，SQLite 不會自動回收磁碟空間，需人工 VACUUM」，並附上目前檔案大小。
2. `docs/DEPLOYMENT-GUIDE.md` 補一段維運說明：何時該跑 `VACUUM`、前置條件（停機、暫存空間）、
   以及 SQL Server 不需要這個步驟。

至少不要讓維運人員以為清過了空間就會回來。

### G2 outbox 迴圈內的線性查找（#14）

`OutboxForwarderService:210` 的 `envelopes.First(e => e.WebhookEventId == item.WebhookEventId)`
在迴圈內，`BatchSize=50` 時是 2500 次字串比較。同一個方法上面已經有
`entriesByWebhookEventId`，比照建一個 `envelopesByWebhookEventId` 即可。純整理。

### G3 `HttpIngestSink` 的 404 訊息會誤導（#15）

`IngestApiKeyMiddleware` 在 Core 端未設定 `Ingest:ApiKey` 時就是回 404，於是 Edge 會印
「Core 端還沒升級，退回逐筆模式」——實際原因是設定漏了。而且退回逐筆後每筆也都拿 404，
全部被當暫時性失敗退避，每輪變成 1+N 個請求。

**做法**：警告文字補上「或 Core 端未設定 `Ingest:ApiKey`」。訊息措辭改動，不改行為。

### G4 `/healthz`（#16）

`HostHeartbeats` 解決的是跨主機互看，IIS／負載平衡器／監控要的是一支不碰 DB、不吃 IP 白名單的
端點。

**做法**

- `GET /healthz`：`Results.Ok()`，不碰 DB。
- `GET /healthz/ready`：DB ping（`CanConnectAsync`），失敗回 503。

**要注意的接線**：兩支都必須排在 `IpAllowlistMiddleware` **之前**，且不套
`RequiresCapability`——否則監控會被白名單擋掉，或在某些部署模式下整支消失。回應不得洩漏
版本、連線字串或主機名。

---

## 批次 H：文字搜尋索引（審查 #12）

`Text` 上的 `LIKE '%q%'` 無法用索引（前綴萬用字元），每次搜尋在 SQL Server 上都是
`GroupMessages` 全表掃描，而且是打字時觸發。保留期三年，只會越來越慢。

**兩個 provider 的路徑不同，且都有前置條件**：

| Provider | 方案 | 前置條件 |
|---|---|---|
| SQL Server | full-text catalog + index on `Text`，`EF.Functions.Contains` | 伺服器需安裝 Full-Text Search 元件（非預設），且 migration 無法用一般 EF 產生，要走 raw SQL |
| SQLite | FTS5 外部內容表 + 觸發器同步 | 需新增虛擬表與三個觸發器；既有資料要一次性回填 |

兩者在**加密啟用時都完全失效**（索引的是密文）。

**做法**：實作成可退回的偵測式路徑，而不是硬性依賴。

1. 啟動時偵測索引是否存在（SQL Server 查 `sys.fulltext_indexes`；SQLite 查 FTS5 表是否存在
   且 FTS5 模組可用），結果快取在單例。
2. `Search` 依偵測結果選路徑：有索引且未加密走全文檢索，否則退回現行 `LIKE`。行為與結果
   排序保持一致——遮蔽後複驗、配額機制、`SearchResultLimit` 全部沿用，只換候選來源。
3. SQLite 的 FTS5 表建立與回填放進 migration；SQL Server 的 full-text 建立寫成
   `docs/DEPLOYMENT-GUIDE.md` 的選用步驟（附腳本），不強制執行。
4. 遮蔽後複驗必須保留：全文檢索是在原文上比對，被關鍵字規則遮掉的詞不能因此變得搜得到。

**這是本輪最大的一批，風險也最高**（新虛擬表、觸發器、跨 provider 分歧、與加密的互動）。
建議排在最後，前七批穩定後再進。若實作中發現 FTS5 觸發器與現有的保留期批次刪除（
`ExecuteDeleteAsync` 不觸發 EF 事件，但會觸發 DB 層觸發器）有效能問題，退回「僅記為已知
限制」是可接受的結果——本輪其餘 15 項不受影響。

---

## 建議實作順序

| 順序 | 批次 | 風險 | 說明 |
|---|---|---|---|
| 1 | A 搜尋投影 | 極低 | 單行等級，收益最大，先落地 |
| 2 | B 快取層 | 低 | A 之後搜尋路徑已乾淨，再疊快取好驗證 |
| 3 | D 查詢字串 | 低 | 前後端＋web.config 一起改，獨立 |
| 4 | G 維運 | 低 | 四項互不相干，可並行 |
| 5 | C 媒體串流 | 中 | 需要完整的 Range 矩陣驗收 |
| 6 | E 佇列去重 | 中 | 觸及 Edge 模式，需雙程序驗證 |
| 7 | F 前端體驗 | 中 | F2、F4 動到 state 與 API 形狀 |
| 8 | H 搜尋索引 | 高 | 獨立一批，可放棄而不影響其餘 |

每批完成後跑一次完整測試；全部完成後照慣例進體檢輪，再併 `dev` 等實測。

## 需要在收尾時同步的現行文件

- `docs/DEPLOYMENT-MODES.md`：B1 的規則快取漂移窗口
- `docs/DEPLOYMENT-GUIDE.md`：G1 的 VACUUM 維運說明、G4 的 `/healthz`、H 的 SQL Server
  full-text 選用步驟
- `docs/ENCRYPTION.md`：F4 的搜尋範圍限制（目前只在程式碼註解裡）
- `README.md`：`/healthz` 端點
