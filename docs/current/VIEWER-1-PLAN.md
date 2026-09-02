# VIEWER-1 規劃：群組刪除＋訊息高亮

> 狀態：規劃中（使用者確認後才開分支）
> 基準：dev@f94aa41（1284 綠，依 EDGEOPS-3 收尾紀錄）
> 來源：新功能需求三項（群組右鍵刪群組／刪歷史訊息；關鍵字與人員高亮）
> 實作方式：整輪委派 **agy**（`gemini-delegate`），Claude 負責規格、逐段驗收、終檢
> 分支：`feature/viewer-1`（從 dev 開）

## 需求原文

1. 檢視頁側欄群組右鍵 → 刪除群組，提示會同時刪除所有訊息，使用者確認後執行。
2. 側欄群組右鍵 → 刪除該群組歷史訊息（群組保留），提示刪除後無法復原。
3. 設定頁加入「訊息高亮」：關鍵字清單＋特定人員（在訊息頭貼右鍵加入／移除），命中的訊息以流動發光邊框顯示；
   設定可開關流動效果、可選一或多個邊框顏色。

## 核對結果摘要（細節見對話紀錄，此處只留影響定案的事實）

| 項目 | 結果 |
|---|---|
| 刪群組能否靠 DB CASCADE | 不能。掛在 `Groups` 上的 FK 只有 `GroupPictures`；`GroupMessages`、`GroupMembers`（→`GroupMemberPictures`）、`AnonymousIdentities`、`MaskKeywordGroups` 都是裸 GroupId，必須手動逐表刪 |
| 分批刪除範本 | `RetentionCleanupService`：先撈 1000 個 Id 再 `ExecuteDeleteAsync`，SQLite 刪後記「未回收空間」警告；`RefreshStaleGroupPointersAsync` 負責把 `LastMessageId/At` 收斂 |
| 側欄可見性 | `GroupsController` 列表只列 `LastMessageId != null` 的群組 → 訊息清空後群組自動從側欄消失 |
| 刪除後會不會回來 | 會。bot 仍在群組時下一則訊息經 `GroupLastMessageTracker` 重建 Groups stub 並重抓名稱頭貼；`WebhookEventId` 防重靠列存在，重送會讓已刪訊息復活（僅限刪除當下在途的訊息） |
| 前端元件 | chat.js 沒有 contextmenu、confirm、toast；toast 只在 settings.js，容器 `#toast-container` 在 modal 內部 |
| 高亮比對位置 | 必須前端：加密開啟時後端只能比對最近 `SearchWindowDays`／300 則；前端拿到的 `text` 已解密。遮蔽在後端先套用，被遮成 `***` 的關鍵字前端比不到 |
| 遮蔽關鍵字範本 | `MaskKeyword` + `MaskKeywordGroup`（ApplyToAllGroups／逐群組範圍）、`SettingsController` 四支 API、settings.js 列表與表單、`MaskingService` 30 秒快取 |
| 人員識別前例 | `UserAlias` 全站 UserId；`AnonymousIdentity`／`GroupMember` 為 (GroupId, UserId) |
| localStorage／DB 分界 | 全站一致或影響外流的進 DB；個人呈現偏好（字級、全版面、側欄）進 localStorage |
| 測試 | 控制器測試走 `WebAppFactoryFixture`（真 SQLite 檔）；前端零 JS 測試框架，前端行為靠人工驗收 |
| ui-ux-pro-max | 現有聊天頁**未套用**（chat.css token 取樣自 LINE 實機截圖）。本輪沿用：不套，一切視覺以既有 `--line-*` token 為基準 |

## 定案（與使用者討論後）

| 待決 | 定案 | 不選的方案與理由 |
|---|---|---|
| 刪群組後的語意 | **刪除＝重置**。確認框明講「bot 仍在群組時，之後的新訊息會讓群組重新出現；要永久停止收錄請將 bot 退出群組」 | 封鎖清單：要新表＋ingest 過濾＋解封 UI，規模過大 |
| 刪除執行方式 | **同步分批**在單一請求內完成，前端顯示處理中並鎖住操作 | 背景工作＋狀態輪詢：操作頻率極低，不值得多一套狀態機 |
| 人員高亮範圍 | **兩種並存**：「全部群組」與「目前群組」，右鍵選單分開列；已高亮者右鍵可取消 | 單一全站鍵：使用者要求可依群組 |
| 流動開關與顏色 | **localStorage**（個人呈現偏好，與字級同類）；關鍵字與人員清單進 DB | 進 ViewerSettings：多裝置一致但沒這個需求 |
| 關鍵字高亮範圍 | **與遮蔽關鍵字同形**：全部群組或逐群組勾選 | 純全站：使用者要求對齊遮蔽 |
| 手機版觸發 | 長按 500ms 等同右鍵，同一個 context menu 元件 | 桌面限定 |
| ui-ux-pro-max | 不套（沿用現況） | — |

## 作業總覽

| 作業 | 內容 | 規模 | 相依 | 建議順序 |
|---|---|---|---|---|
| A | 群組刪除：service＋API＋前端 context menu／確認框／toast 搬遷 | 中 | — | 1 |
| B | 高亮規則資料層與設定頁：兩張新表＋API＋第五頁籤＋localStorage 偏好 | 中 | — | 2 |
| C | 訊息高亮渲染＋頭貼右鍵 | 中 | A 的 context menu 元件、B 的 API | 3 |
| D | 順手修正：`message-highlight-flash` 補進 reduced-motion | 小（Claude） | 併入 C | — |

委派模型：整輪 agy。每階段一份規格檔、實作與測試同一階段。

---

## 作業 A：群組刪除

### 現況與核對結果

- 刪除相關 FK 與分批範本見上表。`RetentionCleanupService` 的收斂與孤兒 blob 補刀邏輯是私有方法，需抽成可共用的服務。
- `GroupLastMessageTracker` 更新既有群組走「Attach 空殼＋標記已修改」（`GroupLastMessageTracker.cs:69-73`）。若群組列在 tracker 查到之後、SaveChanges 之前被刪，UPDATE 影響 0 列會丟 `DbUpdateConcurrencyException`，整筆 ingest 回滾、由 outbox 重試，下一次會走 Add stub 路徑成功。**接受此行為**（自癒、只多一則錯誤 log），不在 tracker 加保護；A-1 的測試要涵蓋「刪除中同時 ingest 新訊息，最終資料一致」。
- 多台 Viewer 同時刪同一群組：`ExecuteDelete` 冪等，第二台拿到 404 或 204 皆可接受。
- 沒有登入機制，刪除操作只有 IP 白名單守衛。

### 定案

1. 刪歷史訊息＝刪該群組全部 `GroupMessages`（DB CASCADE 帶走 Contents／Blobs）→ 孤兒 blob 補刀 → `LastMessageId/At` 設 null。群組名稱、頭貼、成員、匿名代號、遮蔽範圍全部保留；群組因無訊息從側欄消失，下一則訊息進來即重新出現。
2. 刪群組＝上述 1 之後，依序刪 `GroupMembers`（CASCADE 帶走 `GroupMemberPictures`）、`AnonymousIdentities`、`MaskKeywordGroups` 中該群組的範圍列、最後 `Groups`（CASCADE 帶走 `GroupPictures`）。**`UserAlias` 不動**（全站資料）。
3. 遮蔽關鍵字若因範圍列被刪而變成「非全部群組且範圍為空」，**接受**它成為不生效規則，仍留在設定頁供使用者處理；本輪不自動刪關鍵字。刪除後呼叫 `MaskingService.InvalidateCache()`。
4. 兩個入口都記一則 Warning log：操作類型、groupId、來源 IP、各表刪除筆數。SQLite 下沿用「未回收磁碟空間，請人工 VACUUM」警告。
5. 前端右鍵（或長按）群組項目彈出 context menu：「刪除歷史訊息」「刪除群組」。確認用 Bootstrap modal，兩種文案分別說明範圍與不可復原，刪群組再多一句「bot 仍在群組時新訊息會讓群組重新出現」。確認鈕為危險色，執行期間鈕 disabled 並顯示處理中。
6. 成功後：從側欄狀態移除該群組並立即重載清單；若刪的是目前開啟的群組，聊天面板回到未選群組的空狀態、停止該群組的訊息輪詢。以 toast 顯示結果（含刪除訊息筆數）。
7. 另一台裝置正開著被刪的群組：側欄 10 秒輪詢後選中群組不在清單 → 面板收斂成空狀態並 toast 提示「此群組已被移除」，不停在舊畫面。
8. toast 容器從 `_SettingsModal.cshtml` 搬到 `_Layout.cshtml`（或 Index.cshtml modal 之外），`showToast` 抽到 chat.js／settings.js 共用的位置，設定頁既有行為不變。
9. context menu 元件為本輪新建的共用元件（作業 C 沿用）：右鍵／長按開啟、Esc／點擊他處／捲動關閉、鍵盤上下與 Enter 可用、`role="menu"`、定位不可超出視窗、手機長按時抑制原生選單。

### 改動

- A-1（後端）：新 `GroupDeletionService`（兩個公開方法：`DeleteMessagesAsync(groupId)`、`DeleteGroupAsync(groupId)`，回傳各表筆數），`RetentionCleanupService` 的分批刪除、孤兒 blob 補刀、指標收斂改為呼叫共用實作（行為不變）。`GroupsController` 新增 `DELETE api/groups/{groupId}/messages`、`DELETE api/groups/{groupId}`：群組不存在回 404，成功回 200 帶筆數 DTO（前端 toast 要用）。掛既有 `[RequiresCapability(Capability.Viewer)]`。
- A-2（前端）：context menu 共用元件、確認 modal、toast 搬遷、側欄右鍵與長按、刪除後狀態收斂（含另一裝置情境）。

### 測試／驗收

- A-1 測試（`WebAppFactoryFixture`）：
  - 刪訊息：目標群組 Messages／Contents／Blobs 歸零、Groups 列仍在且 `LastMessageId/At` 為 null、`GroupMembers`／`AnonymousIdentities`／`MaskKeywordGroups`／`UserAliases` 筆數不變；其他群組完全不受影響；側欄列表不再列出該群組。
  - 刪群組：上述再加 Groups／GroupPictures／GroupMembers／GroupMemberPictures／AnonymousIdentities／MaskKeywordGroups 該群組列歸零，`UserAliases` 與其他群組不變；跨群組共用的遮蔽關鍵字本體仍在。
  - 分批：超過 1000 則訊息（用小批次設定或注入批次大小）全部刪光。
  - 不存在的 groupId → 404；刪除後再刪一次 → 404（群組）或 200 零筆（訊息）擇一，規格寫死。
  - 刪除後再 ingest 同群組新訊息 → Groups stub 重建、側欄再次出現。
  - `RetentionCleanupService` 既有測試全綠（重構不改行為）。
- A-2 人工驗收（瀏覽器）：桌面右鍵、手機模擬長按、兩種確認文案、取消不動資料、刪目前群組後面板清空、第二個分頁開著被刪群組 10 秒內收斂、toast 在聊天頁可見、設定頁 toast 仍正常。

---

## 作業 B：高亮規則資料層與設定頁

### 現況與核對結果

- 遮蔽關鍵字為完整範本（實體、範圍表、四支 API、settings.js 列表＋範圍勾選）。
- `UsersController` 的成員列表在匿名模式只回代號、不指派；名稱解析走名稱顯示模式。
- 兩套 migration 由 `MessageDbMigrationsConsistencyTests` 把關。
- `docs/ENCRYPTION.md` 記載 `MaskKeywords` 刻意不加密；本輪新表同樣明文，文件要補一行。

### 定案

1. 新表 `HighlightKeywords`（Id、Keyword、ApplyToAllGroups）＋ `HighlightKeywordGroups`（HighlightKeywordId、GroupId，CASCADE）。形狀對齊 `MaskKeyword`／`MaskKeywordGroup`，沒有 Replacement。
2. 新表 `HighlightUsers`（Id、UserId、GroupId 可 null；null＝全部群組）。應用層保證 (UserId, GroupId) 不重複（SQLite 唯一索引把 null 視為相異，不能只靠索引），重複新增回 200 既有列（冪等）。
3. 命中規則（供作業 C 實作，此處定義契約）：
   - 關鍵字：訊息 `text` 不分大小寫含關鍵字，且規則範圍涵蓋該群組。只比對 `text`（文字訊息內文；貼圖／媒體的 fallback 文字不算命中）。
   - 人員：存在 (UserId, null) 或 (UserId, 該群組) 的列。
   - 任一命中即高亮；「一則訊息都沒命中」時畫面與現在完全相同。
4. API（`SettingsController`，形狀照抄 keywords 那組）：
   - `GET/POST/PUT/DELETE api/settings/highlight-keywords`
   - `GET api/settings/highlight-users`（回 userId、groupId、顯示名稱依名稱顯示模式解析，匿名模式回代號）、`POST`（body：userId、groupId 或 null）、`DELETE api/settings/highlight-users/{id}`
   - 另一支合併讀取 `GET api/settings/highlight-rules` 回關鍵字與人員兩份清單，供 chat.js 一次載入。
   - 寫入後派發既有 `messageservice:settings-changed` 事件（chat.js 據此重載規則）。
5. 個人偏好存 localStorage（try/catch，失敗只套用當次）：
   - `chat-highlight-flow`：布林，預設 `true`。
   - `chat-highlight-colors`：`#rrggbb` 陣列，至少 1、上限 8（暫定），順序即漸層順序。預設四色（暫定，agy 可依實機對比微調並回報）：`#06C755`（LINE 綠，既有 token）、`#FFC53D`（琥珀黃）、`#FF6B57`（珊瑚紅）、`#A66CFF`（紫）。取捨：聊天背景 `#8CABDC` 是中藍，藍系發光會沉進背景，故不用藍；四色在白泡泡與藍背景上都能辨識，且第一色沿用 LINE 綠維持品牌一致。
6. 設定 modal 第五頁籤「訊息高亮」，位置在「關鍵字遮蔽」之後：
   - 關鍵字區：與遮蔽頁同形（輸入＋全部群組／逐群組勾選＋列表刪除），頂端一行提示「與關鍵字遮蔽重疊的關鍵字不會高亮（遮蔽先套用）」。
   - 人員區：只列不新增（新增入口在訊息頭貼右鍵），每列顯示名稱＋範圍（全部群組／某群組名）＋移除鈕。
   - 效果區：「流動效果」開關；顏色選擇：預設色票（含上述四色與若干 LINE 風格色）可多選＋自訂顏色輸入（`<input type="color">`），顯示目前選色順序與一個即時預覽泡泡；至少保留一色，刪到最後一色時拒絕並提示。
   - 改動即時生效（與其他頁籤一致）；效果區改動只寫 localStorage 並派發同一事件。

### 改動

- B-1（資料層＋API）：實體、DbContext 設定、Sqlite／SqlServer migration、DTO、`SettingsController` 端點、`docs/ENCRYPTION.md` 補「HighlightKeywords／HighlightUsers 明文」一行。
- B-2（設定頁）：`_SettingsModal.cshtml` 第五頁籤、settings.js 的載入／新增／刪除／效果偏好、localStorage 讀寫。

### 測試／驗收

- B-1：migration 一致性測試綠；關鍵字 CRUD 與範圍更新；人員新增（全部／指定群組）、重複新增冪等、刪除、匿名模式下名稱回代號；`highlight-rules` 合併回應形狀；Keyword 空白回 400；userId 空白回 400。
- B-2 人工：頁籤可用、關鍵字增刪範圍勾選、人員列表移除、色票多選與自訂色、最後一色不可移除、重新整理後偏好保留、無痕模式不炸。

---

## 作業 C：訊息高亮渲染與頭貼右鍵

### 現況與核對結果

- `createMessageRow` 產出 `.message-row > .message-group > .bubble-row > .bubble`；`appendMessages`／`prependMessages` 兩條路徑都會建列。
- 既有動畫 `message-pop-in`、`message-highlight-flash`；`prefers-reduced-motion` block 漏了後者。
- 頭貼由 `buildAvatarElement` 建立，沒有任何互動監聽；訊息列的 `message.userId` 可取得。
- 貼圖泡泡（`.bubble.sticker`）不是白底。

### 定案

1. chat.js 啟動時載入 `highlight-rules` 一份快取；收到 `messageservice:settings-changed` 時重載規則與 localStorage 偏好，**不重抓訊息**，只重掃目前 DOM 的訊息列更新 class 與顏色變數。
2. 命中判定照作業 B 契約；命中的 `.bubble` 加 class `highlighted`。新接進來、往前翻頁、搜尋跳轉三條路徑都要套用（同一個判定函式）。
3. 樣式：`.bubble.highlighted` 以偽元素畫邊框光暈，顏色來自 CSS 變數（由 JS 依 localStorage 顏色陣列組成漸層字串設在 `document.documentElement`）。多色為環繞漸層並旋轉流動；單色為固定發光不流動。流動開關關閉、或 `prefers-reduced-motion` 時邊框靜止但仍發光。貼圖與媒體泡泡同樣套在 `.bubble` 上（貼圖外框發光可接受）。尾巴（`.has-tail::before`）不需跟著發光。
4. 順手修正（D）：`message-highlight-flash` 補進 reduced-motion block。
5. 訊息頭貼右鍵／長按彈出 context menu（沿用作業 A 元件），依目前規則狀態列出：
   - 未高亮：「高亮此人（全部群組）」「高亮此人（目前群組）」
   - 已有全部群組規則：「取消高亮（全部群組）」
   - 已有目前群組規則：「取消高亮（目前群組）」
   - 兩者皆有時兩個取消都列。
   - 匿名模式下同樣可用（規則以 UserId 存，畫面只看到代號）。
   - 操作後立即更新規則快取與畫面，toast 提示。
6. 側欄預覽、搜尋結果、群組項目**不做**高亮（明確不做）。

### 改動

- C-1：chat.js 規則載入與判定、DOM 套用、CSS 光暈與動畫、reduced-motion 補漏、頭貼 context menu 與 API 呼叫。

### 測試／驗收

- 人工（瀏覽器）：
  - 關鍵字命中／不命中、大小寫、逐群組範圍只在指定群組亮、被遮蔽的關鍵字不亮。
  - 人員全部群組／目前群組兩種範圍各自正確；取消後立即消失。
  - 多色流動、單色靜止、關閉流動靜止、系統 reduced-motion 靜止；顏色改動即時反映不重整。
  - 新訊息進場、載入更早、搜尋跳轉三條路徑皆套用。
  - 貼圖與圖片泡泡的外觀可接受。
  - `message-highlight-flash` 在 reduced-motion 下不再閃。
- 後端無新增（API 已在 B-1 測）。

---

## 明確不做（本輪定案）

- 封鎖清單（刪掉的群組永不再出現）。
- 背景刪除工作與進度輪詢。
- 刪除時自動清理範圍變空的遮蔽關鍵字。
- 高亮套用到側欄預覽、搜尋結果、群組項目。
- 關鍵字本身以 `<mark>` 標出（只做邊框光暈）。
- 高亮偏好（開關、顏色）進 DB 跨裝置同步。
- 前端 JS 測試框架導入。
- 套用 ui-ux-pro-max。

## 已知風險與接受事項

- 刪除與 ingest 競態：一次 `DbUpdateConcurrencyException`、outbox 重試自癒（見作業 A 現況）。
- 刪除當下在途的訊息（LINE redelivery、outbox 尚未送達）會在刪除後寫回，屬預期。
- SQLite 刪除不回收磁碟空間，沿用既有警告。
- 遮蔽與高亮關鍵字重疊時高亮失效，UI 有提示。

## 規劃完成後複檢

- 與既有設計衝突：`RetentionCleanupService` 重構為呼叫共用實作，行為契約不變，既有測試為證；toast 容器搬遷影響設定頁，驗收列入；側欄過濾 `LastMessageId != null` 是「刪訊息後消失」的依據，未改動。
- 批次間衝突：A 與 C 共用 context menu 元件，A 先做；B 的 `highlight-rules` 是 C 的輸入，欄位在 B-1 規格中定義並於 C-1 規格中引用同一份 DTO 名稱；A 與 B 都改 `_SettingsModal.cshtml`／settings.js（A 搬 toast、B 加頁籤），順序 A→B 無重疊區塊。
- 四個坑：什麼算命中（只比 `text`、不分大小寫、範圍涵蓋）已寫；零命中畫面不變已寫；破壞性刪除以 groupId 精確比對、無模糊判準、測試含「其他群組不受影響」反例；無單向閘門；無移除類改動（重構保留公開行為）。
- 升級路徑：兩張新表由 migration 建立，既有資料不需回填；localStorage 鍵不存在時用預設。
- 複檢完成，補入「已知風險」一節，無其他新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-1 | agy | | | |
| A-2 | agy | | | |
| B-1 | agy | | | |
| B-2 | agy | | | |
| C-1（含 D） | agy | | | |

## 體檢交接

（實作輪收官時填：測試總數、全綠與否、與基線 1284 的差）
