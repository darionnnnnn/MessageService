# VIEWER-1 規劃：群組刪除＋訊息高亮

> 狀態：全案完成已併 dev（兩段共 A～H 八個作業；實作 Claude Opus 5，體檢 Claude Fable 5.1）
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

---

# 追加作業（第二段，2026-09-02 定案）

> 來源：使用者實測前追加七項需求。同一分支 `feature/viewer-1` 續做，作業編號 E～H；仍委派 agy。
> 起點：feature/viewer-1@c45b300（1308 綠）。

## 追加需求原文

1. 所有顏色或畫面上的設定只保留在目前瀏覽器，不影響別的使用者。
2. 最小字體從 12px 改為 8px，確保所有樣式比例正確（大螢幕低解析度下 12px 偏大）。
3. 關鍵字高亮除外框外，關鍵字本身粗體且加大 1px。
4. 外框高亮強弱可調（透明度，預設 50%）。
5. 全螢幕模式：對話視窗右鍵選全螢幕／關閉全螢幕，隱藏 footer、header 縮成群組 icon 列＋未讀數可切換。
   （使用者補充：不是瀏覽器全螢幕，是頁面內滿版；並希望視窗寬度縮到 450px 仍能正常顯示。）
6. 「載入更早 7 天」只在頂部時顯示。
7. 新訊息進來時若原本在底部就自動置底，否則提示並可一鍵到底。（使用者補充：一次轉傳 10 筆沒有置底。）

## 追加核對結果

| 項目 | 結果 |
|---|---|
| 需求 1 | **已成立**：字級、全版面、側欄、高亮流動、高亮顏色、已讀基準全在 localStorage。進 DB 的是功能性設定（名稱模式、個資遮蔽、保留天數、遮蔽關鍵字、別名）與高亮**規則**。定案：規則維持 DB 全站共用（團隊約定），本輪新增的強度、粗體效果同屬畫面設定→本機 |
| 字級系統 | 下限 12 寫在三處（cshtml min、settings.js、chat.js）；28 個元素用 `--chat-base-current` 換算。**不跟字級縮放**的：訊息頭貼（2.1rem）、側欄頭貼（48px）、標頭頭貼（36px）、貼圖（7.5rem）、圖片佔位（8rem）、泡泡尾巴（em，隨泡泡）、訊息列頂端留白（只看檔位不看 px）。設定視窗文字＝px 值不乘檔位 |
| 關鍵字文字標記 | `isHighlighted` 只回布林；文字泡泡結構固定為文字節點與 `<a>` 交錯，全走 DOM API。**既有落差**：搜尋標記函式註解寫「跳過連結、每節點只標第一處」，實作是走進連結、標全部 |
| 高亮強度 | 漸層用不透明色碼；光暈透明度聊天頁 0.35、預覽 0.45 不一致；組漸層函式只有一份且預覽共用 |
| 全螢幕 | 面板高度全靠 flex，藏 footer／縮 header 不必改高度計算。側欄 rail 是同一份 DOM 靠 CSS 隱藏，選擇器掛在側欄與桌面斷點下，**不能直接套到 header 橫排**。訊息區沒有右鍵監聽；頭貼右鍵沒阻止冒泡。專案未用 Fullscreen API。**450px 實測**：無橫向溢位，≤768 自動切單欄模式正常；CSS 沒有任何 min-width，「最小寬度被鎖」是瀏覽器視窗本身的下限，頁面無法改變 |
| 載入更早 | 「載入更早」常駐、「沒有更早的訊息」才只在頂部顯示；頂部門檻 8px |
| 新訊息置底 | 距底 80px 內視為跟隨→新訊息平滑捲底；否則累計未讀＋右下角箭頭鈕。**實機重現**：一次注入 10 筆純文字訊息會正確置底（距底 0、箭頭隱藏）。**未置底的最可能原因**：轉傳內容含圖片／貼圖，`<img>` 在 append 後才載入撐高列表，捲底發生在載入前，且跟隨狀態仍為 true 所以箭頭也不出現。次要原因：跟隨判定由 scroll 事件維護，平滑捲動過程中的中間位置會暫時把跟隨切成 false |

## 追加定案

| 待決 | 定案 |
|---|---|
| 高亮規則儲存 | 維持 DB；粗體、強度為畫面設定→localStorage |
| 字級縮放範圍 | 見作業 E 的評估 |
| 搜尋標記行為 | 標全部出現處、**跳過連結內部**，修正註解 |
| 全螢幕定義 | 頁面內版面模式，不呼叫 Fullscreen API；不記憶（重整後回一般模式） |
| 右鍵範圍 | 訊息區空白處與泡泡以外區域；泡泡文字保留原生選單 |
| 實作方式 | agy |

## 作業 E：字級下限 8px 與比例縮放

### 評估：哪些元素要跟著字級縮

字級縮小的目的是「大螢幕低解析度下塞進更多內容」，判準是**元素是內文的一部分、還是導航／控制面**。

- **跟著縮**（內文）：訊息頭貼（改為 `--chat-base-current` 的固定倍數，20px 時等於現在的 2.1rem）、泡泡尾巴（已是 em，會跟）、訊息列頂端留白（改吃 `--chat-base-current`）、側欄未讀 badge（已是 em）、日期分隔線與時間戳（已跟）。
- **固定**（導航與圖形）：側欄群組頭貼 48px、標頭頭貼 36px、貼圖 7.5rem、圖片佔位 8rem、右鍵選單、燈箱。理由：縮小字是為了看更多訊息，不是把導航縮到點不到。
- **設定視窗另設下限 12px**：它是控制面板，8px 會不可讀；`.settings-app` 的字級改為 `max(12px, var(--font-base-px))`。

### 改動

1. 下限常數收斂到聊天頁共享出口（`window.messageServiceHighlight` 或另立 `messageServiceFont`）一份，值改為 8；cshtml 的 `min` 屬性由 JS 在初始化時設定，不再手寫。
2. 訊息頭貼尺寸與訊息列頂端留白改吃 `--chat-base-current`。
3. 設定視窗字級套下限 12px。
4. 8px、12px、20px、28px 四檔各做一次視覺核對：泡泡與頭貼對齊、尾巴指向頭貼、時間戳不重疊、日期分隔線可讀。

### 驗收
- 設定頁可輸入 8，重整後保留；輸入 7 被夾回 8。
- 三處常數只剩一份定義（grep）。
- 8px 下頭貼高度不超過單行泡泡高度的 1.5 倍（人工量測）。

## 作業 F：高亮強度與關鍵字文字標記

### 定案
1. 新增 localStorage `chat-highlight-opacity`，範圍 0.1～1.0、步進 0.05、預設 0.5。設定頁顯示效果區加滑桿（顯示百分比）並即時預覽。
2. 邊框漸層每個顏色套此透明度；光暈透明度＝此值 × 0.7（暫定）。聊天頁與預覽統一走同一支函式，消除 0.35／0.45 不一致。透明色與聊天背景混色是預期效果。
3. `isHighlighted` 拆成兩層：回傳命中的關鍵字清單（供文字標記），布林版包在其上。人員命中不產生文字標記。
4. 文字泡泡渲染時，把命中的關鍵字片段包進 `<span class="highlight-keyword">`：粗體、`font-size: calc(1em + 1px)`，不分大小寫、標全部出現處、**不進入 `<a>` 內部**。多個關鍵字重疊時以先命中者為準、不巢狀。
5. 搜尋標記函式改成與註解一致：跳過 `<a>` 內部、標全部出現處；允許進入關鍵字 span 內部（順序天然為 span 先、mark 後）。
6. 規則變更後的重繪：設定視窗關閉走既有整組重載，已涵蓋；頭貼右鍵只改人員規則，不涉及文字標記，維持只 toggle class。

### 驗收
- 滑桿拉到 0.1 與 1.0，computed style 的邊框顏色 alpha 隨之改變；預覽與聊天頁一致。
- 含關鍵字的訊息中，關鍵字片段為粗體且 font-size 比同泡泡文字大 1px；連結內的關鍵字不被包 span；連結仍可點。
- 搜尋跳轉後，`<mark>` 不會切開 `<a>`。
- 無規則時文字泡泡 DOM 與現在完全相同。

## 作業 G：全螢幕模式

### 定案
1. 訊息區（`#message-area` 與 header、footer 的空白處）右鍵／長按彈出選單：「全螢幕」或「關閉全螢幕」（依目前狀態二選一）。事件來源在 `.avatar`、`.bubble`、`.group-item`、按鈕與輸入框上時不觸發，泡泡保留原生選單。頭貼與群組項目的觸發器改為阻止冒泡。
2. 全螢幕＝`chat-app` 加 class：側欄無論何種狀態一律隱藏、footer 隱藏、header 換成**橫向可捲動的群組頭貼列**：每個頭貼右上角疊未讀數（沿用側欄同一份計算與 99+ 截斷）、目前群組有選取樣式、點擊切換群組、原生 title 顯示群組名。列尾保留 🔍 搜尋與「Aa」字級兩顆既有按鈕。
3. 群組列與側欄共用同一份 `state.groups` 與 badge 建構邏輯（抽出共用函式），10 秒輪詢時一起更新，並保留橫向捲動位置。
4. Esc 也可離開；離開時側欄回到進入前的狀態。狀態不記憶。
5. 手機版（≤768）同樣適用：全螢幕時群組列取代返回鈕的切換功能。
6. 450px 寬度：全螢幕模式下 header 群組列橫向捲動、訊息區滿版，人工核對無溢位。

### 驗收
- 進入後 `.sidebar`、`.composer-bar` 不可見，header 高度小於一般模式。
- 群組列的未讀數與側欄一致；點擊切換後訊息載入、選取樣式移動。
- 在泡泡文字上右鍵出現瀏覽器原生選單；在頭貼上右鍵只出現人員高亮選單（不出現全螢幕選單）。
- Esc 離開；重整後回一般模式。
- 450×800 與 1200×800 兩種尺寸截圖核對。

## 作業 H：載入更早只在頂部、新訊息置底修正

### 定案
1. 「載入更早 7 天」與「沒有更早的訊息」統一只在接近頂部時顯示；門檻由 8px 放寬到 40px（暫定），避免臨界抖動。
2. 跟隨模式下把底部「釘住」：對 `#message-list` 的內容掛 `ResizeObserver`（或監聽訊息內 `img`／`video` 的 `load`／`loadedmetadata`），只要 `state.following` 為 true 且內容高度增加，就以非平滑方式再捲到底。這同時涵蓋媒體載入後撐高與多筆連續 append。
3. 平滑捲動期間不讓 scroll 事件把跟隨切成 false：程式觸發的捲底期間設旗標，抵達底部（或逾時 1 秒）才恢復由 scroll 事件維護。
4. 使用者主動往上捲時行為不變：跟隨關閉、累計未讀、箭頭鈕顯示。

### 驗收
- 注入 10 筆含圖片的訊息（圖片高度大於 8rem 佔位），載入完成後距底為 0、箭頭鈕隱藏。
- 使用者捲到中間後注入訊息：不捲動、未讀數＝注入筆數、箭頭鈕出現，點擊後到底且未讀歸零。
- 捲到頂部附近才看到「載入更早 7 天」，捲離頂部後隱藏。

## 追加作業執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| E | agy | 通過 | 1308 綠（不動後端）；Claude 實機量測 8／12／20／28 四檔：頭貼與泡泡高度比 0.52～0.88（遠低於 1.5 倍上限）、時間戳不重疊、膠囊淨空 34～60px；輸入 7 夾回 8；設定 modal 維持 12px | agy 誠實回報無法開瀏覽器、只給幾何計算（符合規格要求），Claude 補做實測且結論一致。上下限已收斂但 localStorage 鍵仍兩處各一份→Claude 一併收進共享出口 |
| F | agy | 通過 | 1308 綠；實機驗五種情境：長關鍵字優先不巢狀、連結內不標記且連結完整、大小寫皆命中、未命中零 span、粗體且 +1px；強度滑桿與光暈公式正確 | 滑桿原本只更新 modal 內的預覽泡泡，背後聊天頁要關窗才變。強度是要對著真實訊息串邊調邊看的設定→Claude 改為即時套用，比照字體大小的既有作法 |
| G | agy | 通過 | 1308 綠；實機驗側欄與 footer 隱藏、群組列未讀數與側欄一致、點擊切換、三種右鍵分流正確、Esc 離開、重整不記憶、450px 無溢位 | 標頭原本只矮 3px（群組列為了不裁切 badge 留了內距）→Claude 把 badge 改貼齊角落、內距歸零，標頭 54.6→46.6 |
| H | agy | 通過 | 1308 綠；實機驗 40px 門檻精準（39 顯示／41 隱藏）、十筆圖片訊息維持底部；以合成載入事件驗釘底：撐高後距底 472→事件到達回 0，不跟隨時停在原位 | agy 只用 ResizeObserver，但它的回呼要等一個繪製框架，**實測確認驗證環境中觀察器與動畫框架都被節流、圖片載入事件卻照常觸發**→Claude 補上媒體 load／error／loadedmetadata 互補路徑，並讓待下載內容被替換時也補捲 |
| 終檢 | Claude | 通過 | 1308 綠；實機複驗全部修正 | 見下節 |

## 追加作業終檢發現與處置

兩個獨立審查（程式碼、契約與文件）各跑一次 `git diff 0045a27..HEAD`。

**已修（本輪處理）**

1. **載入更早會把畫面拉到底**（最嚴重）：大小變化觀察掛在 `createMessageRow`，連 `prepend` 進來的舊訊息也被觀察。
   訊息少到撐不滿視窗時 `isNearBottom` 與 `following` 恆為真，一按「載入更早」就會被拉回底部，
   剛載入的舊訊息完全看不到。改成只觀察往下接進來的列。
2. **repin 打斷剛啟動的平滑捲動**：`observe()` 對新元素必定送出一次初始回呼，
   它會在下一幀呼叫非平滑捲底並清掉 1 秒保護旗標，讓旗標形同虛設。
   改為平滑捲動途中用平滑重新瞄準新的底部（內容在動畫途中長高時原目標值已過期），否則才瞬跳。
3. **離開全螢幕沒有還原手機版狀態**：進入時無條件加上 `mobile-chat-open`，離開時沒拿掉。
   手機版從群組列表進全螢幕再離開會停在沒有群組的空面板。改為記住進來前的狀態並還原。
4. **搜尋框的 Esc 一次跳兩級**：輸入框的既有處理關掉面板後事件繼續冒泡，
   全螢幕的處理看到面板已關就接著退出全螢幕。加上 `stopPropagation`。
5. **側欄狀態的保存還原是死邏輯**：全螢幕期間收合鈕與分隔線都不可見，`sidebarState` 不會被改動，
   還原只是用相同值再寫一次 localStorage。移除，隱藏交給 CSS。
6. 右鍵排除清單漏了搜尋面板與 `label`，在搜尋面板空白處右鍵會彈出全螢幕選單。
7. 高亮強度的合法區間散在四處（chat.js、settings.js 兩處、cshtml 的 min/max），
   與作業 E 收斂字級常數的作法不一致。抽出共用夾擠函式，滑桿的 min/max 由 JS 填入。
8. 全螢幕群組列的同步在 `renderGroupList` 內逐字重複三次，抽成一支函式。
9. 關鍵字片段組裝在 `text` 為 null 時會插入字面 `undefined`（目前呼叫端已擋掉，屬待踩地雷）。

**記錄但不在本輪處理**

- 字級讀到超出範圍的舊值時是退回預設 20 而非夾到邊界。這是既有行為，本段沒有改動它。
- 全螢幕群組列刻意不套側欄的搜尋過濾：全螢幕時側欄與搜尋框都看不到，
  過濾會變成使用者無從得知也無從解除的隱形狀態。已寫進註解。
- 共用右鍵觸發器新增的 `stopPropagation` 會影響既有兩個呼叫端。目前不會壞
  （選單的外點關閉走捕獲階段），但屬於行為擴散，日後若有冒泡階段的監聽器需留意。

## 第二段體檢交接

- 全量測試：**1308 綠**，失敗 0、略過 0。本段為純前端（js／css／cshtml），未動任何 `.cs` 或測試檔，測試數不變。
- 建置零新增警告。
- 前端行為以瀏覽器實機驗收；過程中建立的拋棄式群組、圖片內容與高亮規則已全數清除，
  使用者原有的兩個群組與 34 則訊息完好、無孤兒 blob。
- **驗證環境的限制**：本機瀏覽器面板即使切到前景仍被節流，`requestAnimationFrame` 與
  `ResizeObserver` 都不會觸發。這是作業 H 選擇加上媒體事件互補路徑、而非僅靠程式碼審查的原因；
  觀察器本身的行為在此環境無法直接驗證，已用合成載入事件驗證釘底邏輯。
- 現行文件已同步：README 補了字級範圍與縮放規則、高亮強度、關鍵字文字標記、
  全螢幕模式、載入更早的顯示時機、跟隨模式的釘底機制、搜尋標記跳過連結。
  部署文件與 `CLAUDE.md` 經確認不受本段影響（無設定項、無 API、無 migration，測試基線仍為 1308）。

## 體檢輪修正（換模型獨立體檢）

體檢方 Claude Fable 5.1 對 `dev..feature/viewer-1` 全 diff 各角度掃一次，並單獨重掃實作方
最後兩個手改 commit（`78f9ef0`、`a2791eb`，先前沒有任何獨立驗收看過）。

**修掉的**

| 哪裡 | 症狀 | 怎麼修 | 迴歸測試 |
|---|---|---|---|
| `chat.js` `repinToBottomIfFollowing` | 平滑捲動中補捲走 `scrollToBottom(true)` 會重設 1 秒保護期；多張圖片陸續載入時保護期被無限延長，使用者往上捲被忽略又被拉回底部，形同捲不上去 | 平滑捲動中只 `scrollTo` 重新瞄準新底部、不重設保護期；非平滑期間維持瞬跳 | 無 JS 測試框架；瀏覽器面板節流無法實測動畫，以程式碼路徑核對 |
| `chat.js` `hideDeleteModal` | 每次呼叫掛兩個 once 監聽器，`hide()` 走任何一條提早 return 路徑就殘留、下次開對話框在淡入完成瞬間自關；註解宣稱已排除但不成立（目前呼叫端不可達，屬未來地雷） | 改為初始化時掛常駐 `shown.bs.modal` 監聽＋`pendingDeleteModalHide` 旗標；`openDeleteModal` 開啟前清旗標，殘留不可能跨次 | 同上 |
| `GroupDeletionService` 指標回寫 | SELECT 最新一則與 UPDATE 之間若有新訊息落地、tracker 已寫好有效指標，無條件覆寫會蓋成舊值（比分批期間更窄但仍存在的窗口） | UPDATE 加條件：只在 `LastMessageId` 為 null 或指向已不存在的列時回寫 | 既有 `DeleteMessages_RemovesMessagesButKeepsGroup` 等 9 條照綠（EF 可翻譯該 NOT EXISTS 條件）；競態本身無法在測試中重現 |
| `GroupDeletionService` `Task.Delay` | 批次間讓路沒注入 `TimeProvider`，違反 `CLAUDE.md` 節流類邏輯的紀律 | 注入 `TimeProvider`，改用 `Task.Delay(delay, timeProvider, ct)` | 既有測試照綠 |
| `chat.js` / `settings.js` 死碼 | `savedSidebarStateBeforeFullscreen` 欄位（上一輪修正移除了讀寫點）、settings.js 未使用的 `hexToGlow`、`DEFAULT_HIGHLIGHT_OPACITY` 解構 | 刪除 | — |
| 格式 | `HighlightKeyword.cs` 檔尾缺換行、`chat.js` 一處雙空行 | 補齊 | — |
| 現行文件 | README 把「為什麼」寫進現行文件（配色理由、40px 門檻理由、ResizeObserver 節流細節、Fullscreen 不記憶的理由等）；設定頁籤數仍寫四個（實為五個）；刪訊息 API 寫成「回各表筆數」（實際其餘欄位固定 0）、「指標歸零」（實際是重算）；刪群組 API 與資料表段重複解釋裸 GroupId；既有的「不再是獨立頁面／已移除」敘事；DEPLOYMENT-MODES「已經不是純讀」對照式語氣 | 依 `CLAUDE.md` 寫作紀律刪理由、改直述、去重複、修數值 | — |
| `CLAUDE.md` | 本輪踩到且會再發生的兩顆地雷未入紀律 | 補「Bootstrap modal 淡入中 hide() 被忽略」與「ResizeObserver 初始回呼」兩條 | — |

**查證後判定不成立或屬既有設計，不修**

- 「`loadOlder` 兩條路徑捲動行為不一致」：`growWindow` 分支只在視窗一則訊息都沒有時成立，
  此時沒有可錨定的位置，重繪後捲到底是既有設計（README 已描述），不是本輪引入。
- 「頭貼與群組項目的 `stopPropagation` 冗餘」：面板層的排除清單確實已擋住，兩道防線並存；
  保留作為第二道，不擴散行為。
- 「字級讀取邏輯在兩檔各一份、越界策略與強度不一致」：既有重複，本輪作業 E 只定案收斂常數；
  屬順手重構範圍，記錄下一輪。
- 「`HighlightUsers` 缺唯一索引」：B-1 刻意選應用層去重（SQLite 與 SQL Server 對 NULL 在唯一索引
  的語意不同），已在定案寫明。

**新增的遺留（下一輪）**

- 全螢幕群組頭貼列沒有右鍵刪除選單，全螢幕時無法對群組執行刪除（側欄不可見）。功能缺口，非缺陷。
- `ApplyHighlightGroupSelection` 在 `ApplyToAllGroups=true` 同時帶 `GroupIds` 時靜默丟棄後者，未回 400。

體檢後全量測試：**1308 綠**，失敗 0。

## 終檢輪

併回 dev 後對合併結果再掃一次：規劃比對、體檢修正 commit 是否引入新問題、文件稽核。
（結果見下方「終檢輪結果」，併回時填寫。）

## 追加作業的明確不做
- 高亮規則改存本機。
- 呼叫瀏覽器 Fullscreen API。
- 全螢幕狀態跨重整記憶。
- 改變瀏覽器視窗本身的最小寬度（頁面做不到）。
- 側欄群組頭貼、標頭頭貼、貼圖、圖片佔位隨字級縮放。

## 追加作業複檢
- 與既有設計衝突：作業 E 改頭貼尺寸會影響 `.bubble.has-tail::before` 的對齊，驗收已列四檔核對；作業 F 改搜尋標記行為，README 的「訊息搜尋」段要同步。作業 G 的橫向群組列與側欄 rail 是兩套選擇器，不互相影響。
- 批次間衝突：E 與 F 都動 chat.css 與共享出口（順序 E→F）；G 與 H 都動 chat.js 的訊息區事件（順序 G→H）；F 的 span 與 H 的 ResizeObserver 無交集。
- 四個坑：什麼算命中（關鍵字片段、不進連結、不巢狀）已寫；零規則時 DOM 不變已寫；無破壞性判準；無單向閘門；無移除類。
- 複檢完成，無新增事項。

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
| A-1 | agy | 通過 | 1293 綠（+9）；突變測試（拿掉匿名代號刪除）確認斷言有效 | 三個新檔被加上 BOM，與專案手寫程式碼慣例不符→Claude 移除；孤兒 blob 清理沿用被 CLAUDE.md 禁止的 `when (ex is not OCE)` 過濾→Claude 改為先看取消權杖 |
| B-1 | agy | 通過 | 1308 綠（+15）；突變測試（移除匿名模式名稱保護）確認斷言有效 | **三個檔案被寫成 Big5 而非 UTF-8**，中文註解與測試字串在原始檔裡是亂碼→Claude 轉為 UTF-8，並把「一律 UTF-8」寫進後續階段規格；單筆與批次兩份人員名稱解析重複 45 行→Claude 改為單筆複用批次 |
| A-2 | agy | 通過 | 1308 綠（不動後端）；瀏覽器實測完整刪除流程 | 兩個真實缺陷：確認鈕在淡入轉場中被按下時 Bootstrap 丟棄關閉指令，對話框永久停在「處理中…」；刪除後的側欄刷新走背景輪詢，分頁被判定隱藏時整個跳過→Claude 均已修並以極短時序重現驗證。設定頁 toast 被加了靜默 fallback→Claude 改為直接呼叫 |
| B-2 | agy | 通過 | 1308 綠；瀏覽器實測色票上下限、重複、流動開關、關鍵字增刪 | 發光顏色寫死綠色不跟著選色走→Claude 改為取第一個選用色並抽出轉換函式 |
| C-1（含 D） | agy | 通過 | 1308 綠；瀏覽器實測命中判定、四種頭貼選單狀態、流動與減少動態效果 | **減少動態效果實際沒生效**：關閉動畫的選擇器特異性低於高亮規則被蓋過，規格的 grep 驗收通過但行為不成立→Claude 補齊選擇器並實測 computed style；長按手勢被複製成兩份→Claude 抽成共用觸發器 |
| 終檢 | Claude | 通過 | 1308 綠；瀏覽器複驗刪除＋高亮全流程 | 見下節 |

## 終檢發現與處置

兩個獨立審查（程式碼、文件與契約）各跑一次全 diff。

**已修（本輪處理）**

1. **刪除群組漏清高亮規則**（最嚴重，跨批次斷點）：作業 A 的規格在作業 B 的資料表存在之前定稿，
   刪除清單只列了 `MaskKeywordGroups`，漏掉同形狀的 `HighlightKeywordGroups` 與 `HighlightUsers`。
   後果是留下指向不存在群組的殭屍規則，設定頁的「套用範圍」會退回顯示原始 LINE groupId，
   bot 重新加入同一群組時舊規則還會靜默復活。已補刪除＋測試，並以突變測試確認斷言有效。
2. **刪訊息後的指標無條件寫 null**：大群組刪除要跑數秒，期間落地的新訊息指標會被蓋掉，
   側欄從此看不到該群組，且漂移自癒只處理「指標有值但查不到」，null 時不會觸發。
   已改為比照 `RetentionCleanupService` 的重算語意。
3. **刪除稽核 log 缺來源 IP**（計畫明列）：已注入 `IHttpContextAccessor` 補上。本站沒有登入機制，
   來源 IP 是唯一的稽核線索。
4. `hideDeleteModal` 靠 class 探測 Bootstrap 內部狀態，其他提早 return 的路徑會留下一個
   永不觸發的監聽器，等下一次開啟對話框才引爆。已改為掛好守衛再關、關成功時拆除。
5. `refreshGroupList` 被進行中的輪詢擋掉時靜默丟棄，已刪除的群組會留在側欄最多 10 秒。已補排隊重跑。
6. `messagesCache` 存整包訊息物件，長時間停在同一群組會持續長大。已改為只留判定需要的兩個欄位。
7. 「刪除歷史訊息」的文案說「群組會保留」，但群組其實會從側欄消失。已改為說明會消失、有新訊息才回來。
8. 顏色偏好的讀取函式與上限常數在兩個檔案各一份。已收斂到共享出口。

**查證後判定為誤報**

- 審查指「長按後旗標卡住，下一次單點會被吞掉」。實測不成立：每次 `pointerdown` 都會重設旗標，
  長按叫出選單並以 Esc 關閉後，下一次單點正常開啟群組。

**記錄但不在本輪處理**

- `GroupDeletionService` 與 `RetentionCleanupService` 有三段近似重複（分批迴圈、孤兒 blob 回收、
  SQLite 空間警告）。計畫原本寫「重構為共用」，但 A-1 的委派規格刻意改成「不要動
  `RetentionCleanupService`」以免影響既有的保留期清除路徑。這是規劃與實作的已知落差，
  兩邊行為目前一致，抽取共用留待下一輪。
- `PUT /api/settings/highlight-keywords/{id}` 目前沒有前端呼叫端（設定頁只有新增與刪除）。
  形狀與遮蔽關鍵字對稱、有測試覆蓋，保留供日後補編輯 UI。
- `SettingsController` 與 `UsersController` 各有一份「同一人跨群組挑代表列」的判定。
- 高亮設定的變更沿用專案既有機制，在**設定視窗關閉時**才通知聊天頁，與名稱顯示模式等設定一致。

## 體檢交接

- 全量測試：`dotnet test MessageService.Web.Tests` → **1308 綠**，失敗 0、略過 0。
- 與上輪基線 1284 相比 **+24**（A-1 九筆、B-1 十五筆；A-2／B-2／C-1 為前端，本專案無 JS 測試框架）。
- 建置零新增警告（既有五則 xUnit 分析器警告來自 `EdgePullServiceTests`，與本輪無關）。
- 前端行為以瀏覽器實機驗收，過程中建立的拋棄式群組與高亮規則已全數清除，
  使用者原有的兩個群組與 34 則訊息完好、無孤兒 blob。
- `CLAUDE.md` 的測試基線已同步為 1308。
