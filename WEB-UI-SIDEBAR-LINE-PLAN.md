# Web UI 側欄拖曳/收合 + LINE 桌面版對齊規劃

> 狀態：**已實作完成**（2026-08-05，分支 `feature/sidebar-resize-line-ui`）。160 測試綠（35+125，含本輪新增 8 個未讀數測試）。
> 已用瀏覽器端到端驗證：三態收合循環、拖曳吸附遲滯、鍵盤調寬、localStorage 記憶、
> 未讀 badge 顯示/清除/首見群組防洪水、手機版三控件全隱藏且桌面狀態不外洩、無 console 錯誤。
> 本輪參考 [ui-ux-pro-max](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) 的通用設計原則
> 作為驗收標準（對比 4.5:1、過渡 150–300ms、cursor:pointer、鍵盤焦點可見、prefers-reduced-motion），
> **未安裝該套件**——本專案設計目標是複製 LINE，不需要它生成新設計系統。

## 全案體檢附記（2026-08-05，實作後複檢）

- **修正時間戳對齊回歸**：`.group-item-time` 包進新的 `.group-item-meta` 直欄時漏抄了原本的
  `align-self: flex-start`，時間戳從貼齊列頂端變成垂直置中——補回。
- **修正 read-state 無限累積**：規劃明定「`?read=` 只帶目前清單中的群組」，初版實作把 localStorage
  裡所有歷史條目全帶上，群組被保留期清除後查詢字串會無限長——`seedReadStateForNewGroups`
  補上「已消失群組的基準一併移除」。
- **文件同步**：README 左側欄/聊天面板描述、`GET /api/groups` API 表、測試清單均已更新。

---

## ① 側欄拖曳寬度 + 兩段式收合

三態狀態機（class 掛 `#chat-app`，僅桌面版 `min-width:769px` 生效）：

| 狀態 | class | 呈現 |
|---|---|---|
| expanded | （無） | `width: var(--sidebar-width, 320px)`（inline CSS 變數，拖曳範圍 200–480px） |
| rail | `.sidebar-rail` | 72px 窄欄只剩頭貼（原生 `title` 提示群組名、badge 疊頭貼右上、點頭貼切群組） |
| hidden | `.sidebar-hidden` | `width:0; visibility:hidden`（a11y 樹同步移除），聊天標頭出現「☰」展開鈕 |

- 收合鈕循環：expanded → rail → hidden；展開鈕：hidden → expanded。
- 拖曳（Pointer Events + `setPointerCapture`）：<140px 吸附 rail、rail 拖出 >180px 回展開
  （兩門檻錯開防臨界抖動）；拖曳中 `.resizing` 停用寬度過渡動畫防抖；雙擊分隔線重設 320px。
- 鍵盤：分隔線 `role="separator"` + `tabindex="0"`，←→ ±16px、Home/End 直達邊界、
  `aria-valuenow` 同步；鍵盤不觸發吸附窄欄（收合走收合鈕，避免箭頭鍵誤觸）。
- localStorage：`chat-sidebar-state`（白名單驗證）＋`chat-sidebar-width`（範圍驗證），
  照 `chat-font-size` 既有慣例 try/catch + fallback；窄欄的 72px 不覆蓋寬度記憶。
- 手機版（≤768px）：三個新控件 `display:none`，`.sidebar` 的 `position:absolute; width:100%`
  天然蓋掉桌面寬度，桌面存的 rail/hidden 狀態不生效、與 `.mobile-chat-open` 互不打架；
  resizer `pointerdown` 另有 `matchMedia` 保險。

## ② 側欄未讀數 badge

- **資料流**：已讀基準（每群組最後已讀訊息 Id）存前端 localStorage（`chat-read-state`），
  輪詢 `/api/groups?read=群組:Id,...` 帶上，後端 SQL 端計數並截斷在 100（前端顯示 99+）。
  唯讀檢視器沒有登入，已讀本來就是「每台裝置各自」的概念，數字計算則必須靠後端。
- **後端**：`GroupDto` 加 `LastMessageId`/`UnreadCount`；`ParseReadBaselines` 防禦性解析
  （壞 pair 直接略過）；`Take(100).Count()` 讓截斷發生在 SQL 端。
- **基準推進時機**：切入群組（立即，badge 不等下一輪輪詢）、`renderWindow` 拿到 latestId、
  `pollNewer` 接進新訊息——開著的群組視為已讀（LINE 慣例）。搜尋跳轉的歷史檢視**不**推進基準。
- **防洪水**：本裝置第一次看到的群組直接以 `lastMessageId` 為基準視為已讀。
- **樣式**：LINE 正宗綠底白粗體（`--unread-badge-bg:#06C755`）。白字對綠底約 2.3:1，
  是本輪唯一「品牌識別優先於 4.5:1」的登記例外（badge 另有位置/形狀冗餘）；
  若要嚴格達標改 `#0E873F`（4.6:1）一行即可。字級套聊天字級公式跟著小/中/大縮放。

## ③ LINE 桌面版對齊（樣式）

- **標頭白底**：`--line-header-bg` `#7B9BD2`→`#FFFFFF`＋細分隔線取代陰影
  （原本標頭藍與訊息區 `#8CABDC` 只差一階、幾乎沒分界）；標頭文字/按鈕重新配色全過 4.5:1。
- **選取色改灰**：`--sidebar-active` `#EAF3FF`→`#EDEDED`（LINE 不用藍當選取色）；
  焦點環拆出獨立 `--focus-ring` token（不拆的話輸入框焦點提示會跟著變灰）；
  `.group-item-preview` `#767676`→`#6B6B6B`（對灰底才過 4.5:1）。
- **訊息節奏**：同人連續訊息 `.125rem`、換人/換日首則 `.625rem`；泡泡圓角 `1.1rem`→`.9rem`；
  泡泡寬度加 `min(75%, 34rem)` 絕對上限（34rem 以根字級為基準、不隨聊天字級縮放——
  放大字級時上限不動、自然多換行，與 LINE 行為一致）。
- **頭貼 squircle**：側欄 44px 正圓→48px `border-radius:38%`、標頭同步 38%；
  訊息串 sender 頭貼維持正圓（LINE 如此）。

## ④ 通用 a11y／互動品質

- `.chat-app button:not(:disabled) { cursor:pointer }` 通則（涵蓋既有漏掉的原生 button）。
- `:focus-visible` 統一 `--focus-ring` 實心外框（群組項、標頭按鈕、收合/展開鈕、設定鈕、分隔線）。
- hover/寬度過渡統一 token 化（`--transition-fast` 150ms／`--transition-med` 200ms）。
- `prefers-reduced-motion: reduce` 擴充：訊息進場動畫與側欄/按鈕過渡全停用。

## 驗證

- `dotnet test`：160 綠（新增：未讀數依基準計數、上限 100、五種畸形 `?read=` 容錯、未帶參數為 0）。
- 瀏覽器（實際 DB 資料）：computed style 逐項核對（標頭白底/分隔線、選取灰、48px/38% 頭貼、
  `.9rem` 泡泡、`min(75%,544px)` 上限）；三態循環與 aria 同步；拖曳吸附兩方向；鍵盤 ←→/Home/End；
  badge 綠底白字、選取中抑制、點入即清、基準持久化、幽靈群組條目清除；
  375px 手機版三控件隱藏、桌面 rail 狀態不外洩。
