# PROFILE-1 規劃：名稱與頭貼的即時同步與自癒

> 狀態：全案完成（2026-09-03）
> 基準：dev@d392b49（1308 綠）
> 來源：使用者回報兩個症狀 ＋ 一次即時同步路徑的正確性審查
> 實作方式：整輪委派 **agy**，模型 `gemini-3.8-flash-high`（本輪起改用 3.8，中途不換）
> 分支：`feature/profile-1`（從 dev 開）

## 使用者回報的症狀

1. Core／Edge 兩邊防火牆都通的情況下，**使用者名稱、使用者大頭照、群組名稱都要重新整理頁面才會出現**。
2. **群組照片連重新整理都一直沒有出現。**

## 核對結果（全部實際讀過程式碼）

| # | 事實 | 位置 |
|---|---|---|
| A1 | 訊息列的名稱與頭貼是後端**查詢當下**解析的；`pollNewer` 只用 `afterId` 取增量，已渲染的列永不重解析；前端訊息快取只存 `{text,userId}`，沒有可就地更新的欄位 | `MessagesController` DTO 解析、`chat.js` 的 `pollNewer`／`appendMessages` |
| A2 | 側欄十秒全量重建**會**補上群組名稱與頭貼，但**聊天標頭**的 `updateChatHeader` 只在 `selectGroup`／搜尋跳轉／面板重置被呼叫，不在十秒刷新路徑上 | `chat.js` 的 `refreshGroupList` 與 `updateChatHeader` |
| A3 | 側欄 API 產生群組頭貼網址時**沒有**檢查 `RevealsOriginalProfile`，但 `AvatarsController.GetGroupAvatar` 第一件事就是檢查並回 404；成員那條在 `MessagesController` 已正確處理 | `GroupsController`／`AvatarsController`／`MessagesController` |
| A4 | profile 刷新的**唯一入列點**是訊息落地（`IngestSideEffects`），全系統沒有啟動掃描也沒有週期補刷 | 全域搜尋 `Enqueue(` 僅一處 |
| A5 | 過期判定的缺圖子句要求 `!hasPicture`：換頭貼但下載暫時失敗時，名稱寫入已把 `UpdatedAt` 推新、舊圖仍在 → 判定不過期 → 舊圖最長卡滿 `RefreshAfter`（7 天） | `DbProfileStore.IsStale` |
| A6 | 兩支 upsert 只在 `PictureBytes != null` 時動圖；`PictureUrl` 變 null（LINE 端移除頭貼）沒有刪圖分支 | `DbProfileStore.UpsertGroupAsync`／`UpsertMemberAsync` |
| A7 | 同兩支 upsert 無條件 `existing.GroupName = summary.GroupName` / `existing.DisplayName = profile.DisplayName`；LINE 回應缺欄位時會把既有好名字覆寫成 null | 同上 |
| A8 | 圖片下載的 HttpClient `MaxResponseContentBufferSize` **等於** `MaxImageSize`：超過上限時在 `ReadAsByteArrayAsync` 就丟例外，被歸類為 Transient（非 404/410）→ 每 10 分鐘重抓、永遠拿不到；「讀取後判超限」那段因此是死碼。`Content-Length` 有帶時才會走到 Permanent | `LineProfileClient`／HttpClient 註冊 |
| A9 | 頭貼 API：304 路徑不帶 ETag／Cache-Control（標頭在 200 路徑才設）、不驗圖片可用性；`content is null` 有擋但長度 0 的 byte[] 會回 200＋0 bytes | `AvatarsController` |
| A10 | `memberCount` 對 `GroupMembers` 表 COUNT，而該表只有「發過言且刷新成功」的人，不是群組真實人數 | `GroupsController` |
| B1 | Edge 推送模式的 `UpsertGroupAsync`／`UpsertMemberAsync` 只 `EnsureSuccessStatusCode()`，沒有 outbox 也沒有重試；Core 暫時不通時**已下載的圖直接丟棄**，下一則訊息整套重打 LINE | `ApiProfileStore` |
| C1 | 側欄十秒 `innerHTML=''` 全量重建：會觸發捲動事件關掉開著的右鍵選單；長按進行中節點被移除且無 pointer capture，`pointerup` 不會派送 → 500ms 後對已消失的項目彈選單 | `renderGroupList`／`attachContextMenuTriggers` |
| C2 | profile 佇列 `CreateUnbounded` 且逐則訊息入列、去重在消費端；`EdgeProfileStaging` 的結果佇列與 staleness 字典同樣無界 | `ProfileRefreshQueue`／`EdgeProfileStaging` |
| C3 | 訊息 DTO 沒有「名稱是否已解析」旗標；遮蔽模式下 `ResolveDisplayName` 會把 userId 也遮成像真名的字串，前端無法區分 | `MessageDto`／`MaskingRuleSet` |

**已檢查、確認無問題**：Edge→Core 的 profile 轉送欄位（群組圖與成員圖逐行鏡像，沒有漏送）、
ingest profile 端點的授權與其他 ingest 端點一致、兩支 upsert 是單次 `SaveChangesAsync`（不存在
「名稱進了圖沒進但 UpdatedAt 已推進」的中間狀態）、主鍵競態有重試、匿名代號首次指派的競態有處理、
連續訊息合併判定用的是 `userId`（名稱變動不會打亂合併）、側欄重建有保留搜尋過濾與捲動位置、
`HasPicture` 投影不會撈出 blob。

## 症狀對應的根因

- **症狀 1** ＝ A1 ＋ A2。資料有進 DB，是畫面不回頭修補。
- **症狀 2** ＝ A3（名稱顯示模式非「原始」時必定 404）與 A8（過大圖片永遠拿不到）兩者之一或並存；
  另有 A5／A6 讓它更難自癒。實機要看 `Groups.PictureUrl`／`PictureFetchedUrl` 與 `GroupPictures` 筆數才能分辨。

## 定案

| 待決 | 定案 |
|---|---|
| 本輪範圍 | 作業 A、B、C（含 B1）。作業 D（C1／C2／A10 的效能與互動）延後，寫進「明確不做」 |
| 群組照片與個資閘門 | **群組照片在所有名稱顯示模式下都顯示**——它是群組的識別，不是個人資料；成員頭貼維持受閘門限制。因此改 `AvatarsController` 拿掉群組那支的閘門，`GroupsController` 維持現狀 |
| 背景掃描節奏 | 每 15 分鐘一次、每次最多 50 筆，兩者皆為設定項（暫定值） |
| memberCount | 改抓 LINE 的群組成員數 API，存成 `Groups.MemberCount`；抓不到時回退為目前的已快取成員數 |
| 實作方式 | agy，模型 `gemini-3.8-flash-high` |

## 作業總覽

| 作業 | 內容 | 規模 | 相依 | 順序 |
|---|---|---|---|---|
| A | 資料層自癒與正確性：過期判定、null 分支、名稱保護、背景掃描、Edge 推送失敗記冷卻 | 中 | — | 1 |
| B | 頭貼 API 與圖片下載：群組閘門、過大圖片分類、空內容、304 標頭；成員數 | 中 | A（同動 `DbProfileStore`／`LineProfileClient`） | 2 |
| C | 檢視端就地更新：解析旗標、成員解析端點、前端就地更新、標頭同步 | 中 | A、B 的 DTO 欄位 | 3 |

---

## 作業 A：資料層自癒與正確性

### 定案

1. **過期判定**（A5）：缺圖子句改為「`PictureUrl` 與 `PictureFetchedUrl` 不同就算過期」，不再要求
   `!hasPicture`。永久失敗仍由 `PictureFetchedUrl == PictureUrl` 的閂鎖擋住，行為不變。
2. **移除頭貼**（A6）：兩支 upsert 加分支——`PictureUrl` 為 null／空白時，刪掉對應的圖片子列並清
   `PictureContentType`／`PictureFetchedUrl`／`PictureUpdatedAt`。
3. **名稱保護**（A7）：`GroupName`／`DisplayName` 為 null 或空白時**保留既有值**，不覆寫。
4. **背景掃描**（A4）：新增 `ProfileBackfillService`（`BackgroundService`），每
   `ProfileCache:BackfillIntervalMinutes`（預設 15）跑一次，查「`UpdatedAt` 已過期，或有 `PictureUrl`
   但缺圖且 `PictureFetchedUrl` 不等於 `PictureUrl`」的群組與成員，最多 `ProfileCache:BackfillMaxPerScan`
   （預設 50）筆，丟進既有的 `ProfileRefreshQueue`。
   **只在有資料庫存取能力的角色跑**（AllInOne／Core／Viewer 之中，僅 AllInOne 與 Core——比照
   `RetentionCleanupService` 的能力條件），避免多台同時掃。入列後由既有佇列與抑制機制節流。
5. **Edge 推送失敗**（B1）：`ApiProfileStore` 的兩支 upsert 失敗時，讓呼叫端記一次失敗冷卻
   （沿用既有的 `RecordFailure` 路徑），避免下一則訊息立刻重打 LINE。**不做 outbox**（規模過大）。

### 測試／驗收
- 過期判定：換網址但沒圖 → 過期；網址等於已抓網址 → 不過期；`UpdatedAt` 未到期且網址相同且有圖 → 不過期。
- 移除頭貼：既有有圖的群組／成員，upsert 帶 `PictureUrl = null` → 圖片子列被刪、三個中繼欄位清空。
- 名稱保護：既有名稱為「甲」，upsert 帶 `GroupName = null` → 仍為「甲」；帶「乙」→ 變「乙」。
- 背景掃描：種一筆過期群組與一筆缺圖成員，跑一次掃描 → 兩者都被入列（用假的佇列驗證）；
  超過上限時只入列上限筆數；角色不符時完全不跑。
- Edge 推送失敗記冷卻：upsert 拋例外後，同一 (groupId,userId) 在冷卻期內不再打 LINE。

## 作業 B：頭貼 API 與圖片下載

### 定案

1. **群組頭貼閘門**（A3）：`AvatarsController` 的群組那支**移除** `RevealsOriginalProfile` 檢查。
   成員那支維持。並在該處加註解說明「群組照片是群組識別、不是個人資料」。
2. **過大圖片**（A8）：HttpClient 的 `MaxResponseContentBufferSize` 改為 `MaxImageSize + 1`（留餘量，
   讓讀取後的超限判定真的能執行並記成 Permanent）。**同時**把「緩衝溢位」這類例外也歸為 Permanent
   ——判定依據不能只看例外型別，要能區分「緩衝上限造成的失敗」與一般網路錯誤，寫法自訂但要有註解說明。
3. **空內容**（A9）：`content is null or { Length: 0 }` 一律回 404（群組與成員兩支都要）。
4. **304 標頭**（A9）：ETag 與 Cache-Control 抽到兩條路徑共用的位置，304 也要帶。
5. **成員數**（A10）：`LineProfileClient` 加一支群組成員數呼叫（LINE 的 group members count API），
   結果存進 `Groups.MemberCount`（新欄位，兩套 migration）；`GroupsController` 優先用它，為 null 時
   回退為目前的已快取成員數。成員數與群組 summary 同一次刷新取得，不另外排程。

### 測試／驗收
- 群組頭貼在四種名稱顯示模式下都回 200（有圖時）；成員頭貼在非 Original 模式回 404。
- 空 byte[] → 404（兩支）。
- 304 回應帶 ETag 與 Cache-Control。
- migration 一致性測試綠；`MemberCount` 為 null 時側欄回退到已快取成員數。

## 作業 C：檢視端就地更新

### 定案

1. **解析旗標**（C3）：`MessageDto` 加 `nameResolved`（成員列存在且 `DisplayName` 非空才為 true）
   與 `hasAvatar`；`GroupDto` 加 `nameResolved`（`GroupName` 非空）。
2. **成員解析端點**：`GET api/groups/{groupId}/members/resolved?ids=<逗號分隔>`，回
   `[{ userId, displayName, pictureUrl, avatarIcon, nameResolved }]`，解析規則與 `MessagesController`
   **共用同一段邏輯**（抽出共用方法，不可複製一份）。上限 200 個 id，超過回 400。
3. **前端就地更新**：訊息列與其頭貼容器加 `data-user-id`；比照既有「待下載內容狀態輪詢」的模式，
   只在畫面上還有 `nameResolved=false` 的列時才發請求，節奏 30 秒；回來後用選擇器就地更新名稱文字、
   補建頭貼節點；全部解析完就停止輪詢。**不重抓訊息、不重繪整個視窗。**
4. **聊天標頭同步**（A2）：`refreshGroupList` 成功後，若目前群組仍在清單中就重呼叫
   `updateChatHeader`，讓名稱、頭貼與成員數跟著十秒刷新更新。

### 測試／驗收
- 後端：解析端點的回應形狀、上限 400、匿名模式回代號不洩真名、與訊息 DTO 的解析結果一致。
- 前端人工：新群組進第一批訊息時名稱是 userId ＋代號圖示，30 秒內自動變成真名與頭貼且**不重繪視窗**
  （捲動位置不動）；標頭名稱在十秒內跟上；全部解析完後不再發解析請求（看網路面板）。

## 明確不做（本輪定案）

- 側欄改 keyed diff（連帶解 C1 的選單被關與長按失控）。
- profile 佇列與 Edge staging 的有界化、入列去重（C2）。
- Edge 推送 profile 走 outbox。
- 名稱／頭貼的即時推播（WebSocket／SSE）。
- 圖片降級（縮圖）以繞過 2MB 上限。

## 複檢

- 與既有設計衝突：作業 B 拿掉群組頭貼閘門會改變匿名模式下的行為——匿名模式的目的是隱藏「人」，
  群組識別不在保護範圍，README 的匿名說明要同步一句；`MemberCount` 新欄位需兩套 migration。
- 批次間衝突：A 與 B 都動 `DbProfileStore`／`LineProfileClient`（順序 A→B）；B 的 DTO 欄位是 C 的輸入。
- 四個坑：什麼算「已解析」（成員列存在且名稱非空）已寫；零未解析時不發請求已寫；
  破壞性判準只有「移除頭貼時刪圖」，反例（`PictureUrl` 有值時不可刪）列進驗收；無單向閘門；
  移除類只有群組頭貼的閘門檢查，呼叫端唯一。
- 複檢完成，無新增事項。

## 執行紀錄

| 作業 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A | agy 3.8 ＋ 一次回饋輪 | 1308→1336 綠 | 過期判定、刪圖（含反例）、名稱保護、Edge 推送記冷卻皆有測試；突變測試釘住刪圖分支與 IsStale | agy 第一版把 `ProfileBackfillService` 註冊在 `RunsRetention`（Core），而 Core 在拆機拓撲是 `NullProfileRefreshQueue`，掃到的候選全被丟掉——正是使用者的拓撲。回饋輪改為：`IProfileStore` 新增 `GetStaleProfilesAsync`、Core 新增 `GET api/ingest/profiles/stale`、服務註冊在 `OutboundHere`（與 PLAN 原文「有資料庫的角色」不同，PLAN 原文是錯的） |
| B | agy 3.8 | 1336→1352 綠 | 四模式群組頭貼 200／成員 404、空內容 404、304 帶 ETag、migration 一致性、成員數回退 | agy 寫成無條件 `catch (OperationCanceledException) { throw; }`，HttpClient 逾時會中止整個群組刷新，其逾時測試丟的還是錯的例外型別（假陽性）——Claude 改成看 token 並補真正的逾時測試。緩衝餘量 `+1` 改為 `+1024`（效果同） |
| C | agy 3.8 | 1352→1359 綠 | 解析端點形狀／400／匿名不洩真名／與訊息 DTO 一致；瀏覽器實測就地更新（列上記號存活、捲動不動） | `GroupDto.nameResolved` 沒有消費端（側欄本來就十秒全量重建），終檢移除。`hasAvatar` 沒做，體檢輪併入旗標（見下） |

實作輪終檢（Opus 5，兩個獨立審查代理）另修：匿名／別名模式下旗標永遠為假造成無限輪詢；解析端點對外部輸入的 id 指派代號（違反「代號只讀不指派」約定）；補刷候選查詢仍要求「沒有圖」與 IsStale 分岔；殭屍群組吃光補刷名額；補刷無啟動掃描且間隔 0 語意與既有服務相反；例外訊息字串比對的死碼。1359→1362 綠。

## 體檢交接

- 實作：Claude Opus 5 規劃／驗收，agy `gemini-3.8-flash-high` 產出，實作輪終檢由 Opus 5 手改（commit `6b00ff5`）。
- 體檢：Claude Fable 5.1（使用者 `/model` 切換後下收尾指令）。
- 實作方最沒把握的地方：`6b00ff5` 的手改沒有任何獨立審查看過；拉取拓撲沒有背景補刷；`NameResolved` 語意剛改過。

## 體檢輪修正（Fable 5.1）

對 `dev..feature/profile-1` 全 diff 與 PLAN 逐條比對，重點掃 `6b00ff5`。

1. **規劃落差：作業 C 定案的 `hasAvatar` 沒有實作**（前兩次審查都判 C-1 ✅ 而漏掉）。症狀：`Original` 模式下名稱先到、圖檔下載暫時失敗的人被判「已解析」，前端停止輪詢，之後補齊的頭貼要重新整理才看得到——正是使用者回報的「大頭照要刷新」。處置：不另加欄位，把頭貼併進同一個旗標並更名 `ProfileResolved`（名稱與頭貼都是最終值才為 true；非 `Original` 模式不看頭貼；LINE 端沒頭貼的人不用等）。兩個 controller 投影多帶 `PictureUrl`。迴歸測試：`GetMessages_OriginalMode_ProfileResolvedWaitsForPictureDownload`、`GetMessages_MaskMiddleMode_PendingPictureDoesNotBlockResolution`，突變（拿掉頭貼條件）確認紅燈。
2. **升級順序的 log 洗版**：新版 Edge 打舊版 Core 的 `profiles/stale` 收 404 → `GetFromJsonAsync` 拋例外 → 每 15 分鐘一筆 error。處置：比照 `HttpIngestSink` 批次端點，404 只警告一次、回空清單；其他狀態碼維持拋出。測試：`GetStaleProfilesAsync_OldCoreWithoutEndpoint_ReturnsEmptyInsteadOfThrowing`、`_ServerError_StillThrows`。
3. **log 等級與既有服務不一致**：補刷啟動掃描失敗記 Error（`ContentDownloadService` 同情境是 Warning，啟動時 DB／Core 未就緒是常態）；零筆的輪次記 Information（穩態每 15 分鐘一行，拉取方向的 Edge 永遠 0）。處置：啟動失敗降 Warning、零筆降 Debug。
4. **304 路徑用全域加密開關決定 Cache-Control，200 路徑看 blob 表頭**：只在「加密開啟前寫入的舊圖」不一致，且方向是 304 更嚴（no-store），無外洩風險。處置：補註解說明，不改行為。
5. 已確認無問題：拉取方向可在執行期切換（`EdgeChannelState.UsePullResources`），補刷服務必須維持註冊；`cutoff` 有 `Uri.EscapeDataString`；`LastMessageId != null` 與側欄可見性查詢同一條件；`showAvatarContextMenu` 只用 `userId`；本輪改動的 BOM 只在 EF migration（慣例允許）；`GetGroupMemberCountAsync` 每次群組刷新多打一次 LINE，但群組刷新本身有 7 天 TTL 與入列抑制，維持 PLAN 定案。

測試：1362→1366 綠。

## 終檢輪

併 dev 後全量 1366 綠。體檢修正 commit（`705b26c`）複讀、文件 diff 敘事字眼掃描、舊名稱殘留掃描皆無新發現。終檢無新發現。
