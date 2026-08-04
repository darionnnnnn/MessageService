# Web UI 回饋修正規劃（第三輪）

> 狀態：**已實作完成**（2026-08-04，分支 `feature/web-ui-feedback-3`）。152 測試綠（35+117）。
> 體檢過程中修掉兩個真的會影響使用者的既有 bug（見 P4 commit）：燈箱縮放判斷量到隱藏 modal
> 的 0×0 尺寸、連續開同一張圖片時單靠 onload 事件不可靠（已改用 onload+decode()+rAF 三重保險）。
> 已用瀏覽器端到端驗證五項全部功能＋跟既有搜尋/匿名模式/遮蔽機制的交互作用。
> 影響範圍：①⑤純前端小改；③④合併為一個工作項（設定改寬版 modal＋頁籤，中型前端重構，退場一個頁面路由）；
> ②**唯一動到收錄端與 DB schema 的項目**（webhook 模型、GroupMessages 加欄位、migration），本輪最大項。

---

## ① 照片燈箱：外框＋關閉的 X

### 現況

燈箱是全螢幕深色 modal，圖片直接貼在深色背景上沒有邊界感；關閉只能按 Esc——
`modal-fullscreen` 的內容蓋滿整個視窗，Bootstrap 的「點背景關閉」根本點不到背景，
等於沒有滑鼠可用的關閉方式（上一輪的疏漏，這次一併補上）。

### 方案（純前端）

1. **外框**：圖片加白色外框（約 6px）＋小圓角＋陰影，類似相紙的邊界感，在深色背景上輪廓清楚。
2. **X 按鈕**：固定在燈箱右上角，半透明深色圓底＋白色 ✕，`data-bs-dismiss="modal"`，
   z-index 高於圖片（原尺寸捲動模式下也搆得到）。
3. **點空白處關閉**：modal-body 的 click 事件判斷 `target === modal-body`（點到圖片外的深色區）
   就關閉——補回全螢幕 modal 損失的「點外面關掉」直覺；點圖片本身仍是縮放切換，兩者不衝突。
4. Esc 關閉維持不變。

---

## ② 貼圖顯示真正的圖，不是「(貼圖)」文字

### 現況（已查證）

- 收錄端 `LineMessage`（webhook 反序列化模型）只有 `id/type/text/fileName`，
  **stickerId/packageId 進來就被丟掉**；`WebhookEventHandler` 對貼圖只寫 `Text = "(貼圖)"`。
- 所以這不是前端樣式問題——資料根本沒收，要從收錄端補起。

### 方案（收錄端 → DB → Web → 前端，全鏈路）

1. **webhook 模型**：`LineMessage` 加 `stickerId`、`packageId`（LINE webhook 貼圖訊息原生就帶，
   之前沒解析而已）。
2. **DB schema**：`GroupMessage` 加兩個 nullable 欄位 `StickerId`、`PackageId`（`packageId` 目前
   渲染用不到，但貼圖的完整識別是兩者一組，一次補齊免得日後再動 schema）→ 新 migration。
   - **Sqlite 開發庫處理**：`EnsureCreated()` 不會給既有 db 補欄位，而使用者的 `messages.db`
     裡有真實測試資料不能重建——SQLite 支援 `ALTER TABLE ... ADD COLUMN`，實作時直接
     對 messages.db 跑兩句 ALTER（nullable 欄位、零資料損失），README 補充說明。
   - SqlServer 生產照常走 migration。
3. **收錄端寫入**：`WebhookEventHandler` 貼圖訊息存入 stickerId/packageId（`Text` 維持 "(貼圖)"
   當 fallback 顯示用）。**只有改版後收到的貼圖有 ID**；歷史貼圖維持文字顯示。
4. **Web API**：`MessageDto` 尾端加 `string? StickerId`（named-argument 呼叫端不受影響）。
   貼圖與遮蔽/匿名機制無交集（無文字內容、非個人身分資料），不需要伺服器端裁決。
5. **前端渲染**：sticker 訊息改渲染
   `<img src="https://stickershop.line-scdn.net/stickershop/v1/sticker/{stickerId}/android/sticker.png">`
   （LINE 貼圖公開 CDN，靜態圖），約 120px 高、**無白色泡泡**（LINE 的貼圖是浮貼在背景上，
   不裝進對話泡泡——「放到對話框中」解讀為「顯示在對話串裡」；若你要白色泡泡包起來跟我說）。
   - `referrerpolicy="no-referrer"`（比照頭貼）。
   - `onerror` fallback 回現行「(貼圖)」文字樣式（部分貼圖 CDN 可能 404，歷史貼圖無 ID 也走這條）。
   - 動態貼圖以靜態圖呈現（CDN 的 sticker.png 就是靜態版），不做動畫。
6. **測試**：收錄端 `WebhookEventHandler` 貼圖存 ID ×2（有 ID／無 ID payload）、
   Web 端 DTO 帶出 StickerId ×1~2。預估 148 → 152+。

---

## ③＋④ 設定改為寬版 Modal＋上方頁籤（合併實作）

### 現況

獨立頁面 `/Home/Settings`，三張卡片直向堆疊；字級調大後頁面被拉很長（回饋③的痛點），
而且來回要整頁導航。

### 方案（中型前端重構）

1. **設定搬進聊天頁的 modal**：`modal-xl`（寬版）＋`modal-fullscreen-md-down`（手機全螢幕），
   側欄的「⚙️ 設定」從連結改成開 modal 的按鈕。
2. **上方頁籤**：modal 內用 Bootstrap nav-tabs 分三頁——「介面顯示」「隱私與匿名」「關鍵字遮蔽」，
   內容高度固定（`modal-body` 設 max-height + overflow-y:auto），字級再大也不會把版面拉長，
   換分頁不捲動。
3. **表單控件 id 全部不變**：settings.js 的綁定邏輯原樣沿用，只改初始化時機——
   從 DOMContentLoaded 改成**首次 `shown.bs.modal` 才 lazy init**（載入群組清單、規則、別名），
   避免聊天頁一開就多打一輪 settings 用的 API。
4. **字級變數改掛在 `document.documentElement`**：目前 chat.js 掛在 `#chat-app`、settings.js
   掛在 `.settings-app`，兩個頁面各自為政；併到同一頁後改成統一掛在根元素，
   聊天畫面與 modal 內文字同時生效——在 modal 裡調字級，背後的聊天畫面**即時預覽**，體驗更好。
5. **設定變更即時反映到聊天畫面**：任何成功的設定寫入（顯示模式/關鍵字/別名）標記 dirty，
   modal 關閉時（`hidden.bs.modal`）呼叫既有 `selectGroup(state.groupId)` 重載訊息＋
   `pollGroups()` 刷新側欄——改完名稱模式不用手動重新整理。
6. **`/Home/Settings` 頁面退場**：路由與 `Settings.cshtml` 刪除（`HomeController` 只剩 Index/Error；
   simple-header 版型保留給 Error 頁）。設定內容抽成 partial `_SettingsModal.cshtml` 由 Index 引入，
   避免同一份表單存在兩個渲染路徑各自漂移。
7. Toast 容器移入聊天頁共用（Bootstrap toast z-index 高於 modal，疊層沒問題）。

---

## ⑤ 「沒有更早的訊息」只在捲到最頂部時顯示

### 現況

載入更早膠囊固定浮在訊息區頂端；沒有更早歷史時變成 disabled 的「沒有更早的訊息」，
但**捲到哪裡都看得到**，在畫面中間看到這句話很突兀。

### 方案（純前端）

- `updateLoadMoreButton()` 加可見性規則：
  - `hasMoreOlder === true`：維持常駐（它是往前翻頁的操作入口，藏起來會影響可發現性——
    只有「沒有更早的訊息」這個純告知狀態受頂部規則約束；若你要連「載入更早」也只在頂部出現跟我說）。
  - `hasMoreOlder === false`：只有 `message-list.scrollTop` 貼近 0（門檻約 8px）才顯示。
- 掛在既有的 scroll listener（跟隨模式那顆）一起判斷，不另開 listener；
  訊息渲染完（renderWindow/prepend/append）後也補一次判斷。
- 邊界：內容不滿一屏（沒有捲軸）時 scrollTop 恆為 0 → 顯示，合理（頂部本來就看得到）。

---

## 影響評估

| 面向 | 影響 |
|---|---|
| 收錄端 | ②動 `LineMessage`＋`WebhookEventHandler`（本輪唯一，前兩輪都是零改動） |
| DB / migration | ②`GroupMessages` 加 2 個 nullable 欄位＋SqlServer migration；使用者的 Sqlite 實資料庫用 `ALTER TABLE ADD COLUMN` 原地升級（零資料損失） |
| Web 後端 | ②`MessageDto` 加 `StickerId`；③④刪 `HomeController.Settings` action |
| 前端 | ①燈箱框+X；②sticker 渲染；③④設定 modal 化＋頁籤＋lazy init＋字級變數搬根元素；⑤膠囊可見性 |
| 測試 | ②約 +4；③④若有引用 Settings 路由的測試需同步（目前應無，API 級測試不經 MVC 頁面）。預估 148 → 152+ |
| 相容性 | 歷史貼圖（無 StickerId）自動 fallback 文字樣式；`chat-font-base-px`/`chat-font-size` localStorage key 不變 |

## 實作階段（建議順序）

- **P1**：②收錄端＋DB＋migration＋Sqlite ALTER＋收錄端測試（全綠才往下）。
- **P2**：②Web DTO＋前端 sticker 渲染＋Web 測試。
- **P3**：③④設定 modal 化（partial、頁籤、lazy init、字級根元素化、dirty-refresh、路由退場）。
- **P4**：①燈箱框＋X＋點空白關閉；⑤膠囊可見性。
- **P5**：全面體檢：152+ 測試綠＋瀏覽器回歸（六大既有功能＋本輪五項＋手機版）＋文件更新，推 dev。

## 決策點（預設採建議值，有意見再說）

1. **貼圖要不要白色泡泡**：建議照 LINE——無泡泡浮貼；要泡泡包起來成本相同。
2. **歷史貼圖**：改版前收的沒有 ID，維持「(貼圖)」文字樣式（無法回溯，LINE API 不提供舊訊息查詢）。
3. **`/Home/Settings` 直接刪除**：建議刪（設定入口只剩 modal）；若要保留網址相容可改 302 導回 `/`。
4. **「載入更早」本身維持常駐**：只有「沒有更早的訊息」受頂部規則約束。
