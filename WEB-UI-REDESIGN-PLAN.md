# MessageService.Web LINE 風格改版規劃（feature/line-style-web-ui）

> 狀態：規劃完成（2026-07-30 第三版：依 LINE 實機截圖修訂視覺規格＋匿名代號系統）、尚未實作。
> 已確認決策：①頭貼與名稱顯示模式連動 ②配色以 LINE 標準配色為準
> ③對話樣式以截圖為視覺基準、底部列仿 LINE 但改為唯讀用途、泡泡加小尾巴 ④不做深色模式
> ⑤匿名要能分辨不同發言者、圖示庫擴為 24 款動植物（排除口語帶貶義者）、
> 匿名模式名稱直接顯示動植物代號（如「小熊」）。
> 目標：把目前「學生作業感」的檢視介面，改成一眼看得出在模擬 LINE 的專業聊天檢視器，
> 並加入頭貼系統（真實頭貼 / LINE 風格預設圖示）。

---

## 1. 現況體檢：為什麼看起來像學生作業

| 問題 | 現況 |
|---|---|
| 導覽列是 Bootstrap 預設樣板 | `_Layout.cshtml` 白底 navbar + 「MessageService.Web」文字 brand + footer 版權列，和聊天內容完全不搭 |
| 群組切換用 `<select>` 下拉 | LINE 的核心心智模型是「左側聊天列表」，下拉選單完全沒有聊天軟體的感覺 |
| 沒有頭貼 | 目前頭貼是「名字第一個字 + 雜湊色塊」，DB 裡其實已存有 `GroupMember.PictureUrl` / `Group.PictureUrl` 卻沒用上 |
| 工具列元素散落 | 字級切換鈕、載入更早按鈕、連線橫幅各自佔一排，垂直空間浪費、視覺雜亂 |
| 聊天區只是個圓角矩形 | 藍灰底色方向對了，但包在 Bootstrap `.container` 裡，左右留白大、像個表單控件 |
| 設定頁是原生表格+radio | 功能齊全但零視覺層級，表單直接攤在白底上 |

已經做對、要保留的部分：時間戳貼泡泡外側、同傳送者省略頭貼/名字、日期分隔 pill、
置底跟隨+未讀計數、輪詢去重 token、載入更早的游標/開窗邏輯。**這些行為邏輯全部不動，只換渲染層。**

---

## 2. 目標版面：LINE 桌面版雙欄式

```
┌────────┬──────────────────────────────────────────┐
│ 側欄    │  聊天面板                                  │
│┌──────┐│ ┌──────────────────────────────────────┐ │
││ 標題列 ││ │ 群組頭貼  群組名稱 (成員 N)   [Aa▾] [⚙] │ │  ← 聊天標頭
│├──────┤│ ├──────────────────────────────────────┤ │
││🔍搜尋 ││ │        ── 7/29（二）──                 │ │
│├──────┤│ │ ◯ 名字                                │ │
││◯ 群組A││ │    ┌─────────┐ 14:32                 │ │
││  最後訊││ │    │ 訊息泡泡  │                      │ │
││◯ 群組B││ │    └─────────┘                      │ │
││  最後訊││ │                          (回到最新 ⬇)│ │
│├──────┤│ ├──────────────────────────────────────┤ │
││⚙ 設定 ││ │ ＋ 📷 🖼 (唯讀檢視模式·同步中●) ☺ 🎤    │ │  ← 仿 LINE 底部列（唯讀）
│└──────┘│ └──────────────────────────────────────┘ │
└────────┴──────────────────────────────────────────┘
```

### 2.1 左側欄（新增，取代 `<select>`）
- 寬約 320px，淺色（白底、淺灰分隔線），與 LINE 標準亮色系一致（已確認不做深色）。
- 標題列：App 名稱改為「訊息紀錄」+ LINE 綠圓點 logo 感的小識別。
- 搜尋框：前端過濾群組名稱（純 client-side，不加 API）。
- 群組列表項目：群組頭貼（`Group.PictureUrl`，抓不到用「多人剪影」預設圖）+ 群組名
  + **最後一則訊息預覽 + 時間**（新 API 欄位，見 §4.2），依最後訊息時間倒序排列（LINE 行為）。
- 目前選取的群組項目高亮（LINE 是淺灰底）。
- 側欄底部：設定入口（齒輪 icon → `/Home/Settings`）。

### 2.2 聊天標頭（新增；樣式依截圖）
- **關鍵修正**：LINE 手機版的標頭不是白色橫條，而是**與聊天背景同色（透在藍底上）**、
  文字與圖示用深色——照這個做，白色橫條會立刻露餡。
- 內容：（手機版）「‹」返回鈕 → 群組名稱（過長截斷成 `我不綠…表弟` 樣式）+ 成員數 `(5)`。
  桌面版左側已有側欄，改放群組頭貼 + 名稱 + 成員數。
- 右側操作區仿截圖的圖示排（截圖是搜尋/通話/行事曆/選單）：我們只放用得到的——
  「Aa」字級下拉（小/中/大，取代現在的三顆按鈕組）。圖示風格：深色細線條。
- 連線異常時，標頭下緣掛一條窄的琥珀色 pill（沿用現有 `connection-banner` 邏輯，只改樣式與位置）。

### 2.3 訊息區（視覺規格全面以截圖為準）
- 背景色：LINE 標準的藍紫 `#8CABDC`（截圖取樣；現在的 `#8db2c4` 偏灰綠，一眼就不像）。
- **頭貼對齊修正**：截圖中頭貼是對齊該組訊息的「頂端」（名字那一行），
  現在的 CSS 是 `align-items: flex-end` 貼底——要改成頂端對齊。頭貼尺寸 ~40px 圓形。
- **傳送者名字**：小字、**深灰色**（截圖為深灰，不是現在用的近白色 `#f1f1f1`），位於泡泡上方。
- 泡泡：白底、圓角 ~18px，**每組第一顆泡泡帶指向頭貼的小尾巴**（CSS 偽元素畫，已確認要做）；
  同組後續泡泡無尾巴、無名字、無頭貼（沿用現有省略邏輯）。
- **時間戳**：泡泡右外側貼底，小字**深灰藍色**（截圖為深色，不是現在的近白色）。
- 「載入更早 7 天」改成訊息區頂端內嵌的膠囊按鈕（半透明白），不再獨佔一排；
  邏輯完全沿用 `loadOlder()`。
- 「回到最新」改成截圖右下的樣式：**白底圓角方形浮動鈕 + 深色下箭頭**，
  紅色未讀 badge 疊在角上。
- **文字連結化**：截圖中網址是可點的藍色底線連結——新增前端 linkify
  （偵測 http/https URL 轉 `<a target="_blank" rel="noopener noreferrer">`，其餘維持純文字、不解析 HTML）。
- 日期分隔 pill、逐則動畫、`prefers-reduced-motion` 全部保留。
- 截圖中出現但**不在本次範圍**（收錄端沒有對應資料，樣式先不做）：
  回覆引用區塊（quoted message）、連結預覽卡片（Facebook 卡）、頂部公告列（📢）。

### 2.4 底部列（依截圖仿 LINE 輸入列，改為唯讀用途）
- 已確認方向：**以相似度優先**，仿截圖的白底輸入列版面——
  左側「+」「相機」「相簿」圖示、中間圓角灰底輸入膠囊、右側「表情」「麥克風」圖示。
- 唯讀化處理：圖示以半透明灰渲染、不可點（`aria-hidden`、無 hover 效果）；
  中間膠囊不是真輸入框，改顯示「唯讀檢視模式 · 同步中」+ 狀態小點
  （綠=輪詢正常、灰=連線中斷，接現有 `setConnectionOk()`）。
- 這樣遠看是 LINE、近看不會誤導使用者以為能發訊息。

### 2.5 響應式（<768px）
- LINE 手機版模式：預設顯示群組列表全螢幕 → 點群組進入聊天 → 標頭左側出現「‹」返回鈕回列表。
- 用 CSS class 切換（`.mobile-chat-open`），不引入 router。

### 2.6 色彩 token（自截圖取樣，實作時定義為 CSS 變數）

| Token | 值 | 用途 |
|---|---|---|
| `--line-chat-bg` | `#8CABDC` | 聊天區與標頭背景（LINE 標誌藍紫） |
| `--line-bubble-bg` | `#FFFFFF` | 訊息泡泡 |
| `--line-text` | `#111111` | 泡泡內文字 |
| `--line-sender-name` | `#3D4A5C`（深灰藍） | 泡泡上方傳送者名字 |
| `--line-timestamp` | `#4A5A73`（深灰藍、略淡） | 泡泡外側時間戳 |
| `--line-green` | `#06C755` | LINE 品牌綠（logo 點綴、選取態、badge 以外的強調） |
| `--line-composer-bg` | `#FFFFFF` | 底部列背景 |
| `--line-composer-pill` | `#F0F1F3` | 底部列中央膠囊 |
| `--line-date-pill` | `rgba(0,0,0,.18)` 白字 | 日期分隔 |
| `--line-link` | `#2C64C6` 底線 | 訊息內連結 |

實作時以瀏覽器實測截圖再微調（螢幕截圖有色偏可能），但以上為基準值。

### 2.7 設定頁改版
- 保留獨立頁面（不做 modal，規則表格內容量大）。
- 版面改成置中窄欄（max-width ~720px）+ 卡片式區塊，仿 LINE 設定頁的「白卡片 + 分區標題」。
- 區塊重組：
  1. **隱私與匿名**：名稱顯示模式三選一（照舊），但加上說明文字：
     「選擇遮蔽或別名時，頭貼會同步換成預設圖示，避免從照片認出成員」（見 §3）。
  2. **關鍵字遮蔽**：表格改卡片內嵌，操作照舊。
  3. 別名編輯器照舊，只改樣式。
- 左上加返回聊天的連結。

---

## 3. 頭貼系統（本次核心需求）

### 3.1 資料現況
- `GroupMember.PictureUrl` / `Group.PictureUrl` 已由收錄端 `ProfileRefreshService` 維護，
  存的是 LINE CDN 網址（`profile.line-scdn.net`），**Web 端目前完全沒用到成員頭貼**。
- `GroupDto` 已有 `PictureUrl`；`MessageDto`、`GroupMemberDto` 沒有 → 要加欄位。

### 3.2 顯示規則（跟名稱顯示模式連動；新增「完全匿名」模式）

`NameDisplayMode` 增加第 4 個值 `Anonymous`（enum 尾端追加、DB 存 int，**不需 schema 變更**）：

| NameDisplayMode | 名稱 | 頭貼 |
|---|---|---|
| `Original` | LINE 原名 | 真實頭貼；抓不到（URL 為 null 或圖片載入失敗）→ 預設圖示 |
| `MaskMiddle` | 首尾保留中間 * | 動植物代號圖示（照片會直接破功名稱遮蔽） |
| `CustomAlias` | 別名（無別名 fallback 遮蔽） | 動植物代號圖示 |
| `Anonymous`（新增） | **動植物代號名**（如「小熊」「櫻花」「小熊 2」） | 同名的動植物代號圖示 |

核心需求：匿名下**仍要能分辨是不同人在發言**（同一人永遠是同一隻小熊），
只是認不出真實身分。名稱與圖示一定同組（叫小熊就長小熊臉）。

伺服器端裁決：模式非 `Original` 時 `PictureUrl` 一律回 null（不能只靠前端不渲染——
URL 本身就是身分線索）；同時 `MessageDto` 新增 `AvatarIcon` 欄位（如 `"bear"`），
由伺服器統一指派、前端照 key 渲染圖示，**代號的唯一事實來源在伺服器**，
名稱與圖示才不會對不上。群組頭貼不涉及個人身分，任何模式照常顯示。

### 3.3 動植物代號圖示庫（自由發揮版）

- **24 個內嵌 SVG 圖示**（扁平線條、粉彩底色圓形，仿 LINE 預設頭貼的可愛感）：
  - 動物 16：小熊、小貓、小兔、小鳥、小鹿、企鵝、海豚、貓頭鷹、
    無尾熊、熊貓、綿羊、水獺、刺蝟、海豹、天鵝、鯨魚
  - 植物 8：小花、櫻花、楓葉、向日葵、鬱金香、三葉草、銀杏、蓮花
- **嚴肅環境用字審查（已確認需求）**：排除中文口語帶貶義或負面聯想的動物——
  豬（豬頭）、狗（狗腿）、老鼠（鼠輩）、蛇（蛇蠍）、雞（負面俚語）、
  烏龜（縮頭烏龜）、驢（蠢驢）、狐狸（狐狸精）、猴（潑猴）一律不用；
  含「鼠」字的松鼠也順手避開。上列 24 個都是中性偏正面的詞。
- 每個圖示有固定的 `IconKey`（`bear`/`cat`/…）與中文代號名（`小熊`/`小貓`/…），
  對照表放在共用常數（後端指派名稱、前端渲染圖示都查同一張表）。
- 全部放進 `wwwroot/img/default-avatars.svg`（SVG sprite，`<use>` 引用），
  零外部依賴（專案慣例：Bootstrap 都是本機 lib，維持可離線）。
- 底色沿用雜湊選色，同一人固定同色（同圖示不同人時多一層區辨）。
- 真實頭貼 `<img>` 掛 `onerror` fallback 到該使用者的代號圖示（LINE CDN URL 過期/被擋不破圖）；
  並加 `referrerpolicy="no-referrer"` 避免對 LINE CDN 洩漏來源站。
- 群組預設圖：另做一個「多人剪影」圖示，群組 `PictureUrl` 為 null 或載入失敗時使用。

### 3.4 代號指派：唯一性與持久性（新增設計）

**問題**：24 個圖示、群組動輒 5~20 人，單純 `hash(userId) % 24` 幾乎必撞名
（生日悖論），撞了就違反「看得出是不同人」的需求；而且代號必須**永久穩定**——
回頭翻舊訊息時，昨天的小熊今天還是小熊，嚴肅環境的紀錄才可追溯。

**方案（建議採用）：DB 持久化指派表**

- `MessageService.Data` 新增實體 `AnonymousIdentity`：
  `GroupId` + `UserId`（複合主鍵）、`IconKey`、`Label`（如「小熊 2」）、`AssignedAt`。
- 新增服務 `IAnonymousIdentityService.GetOrAssignAsync(groupId, userIds)`：
  首次遇到某成員時指派——起手式 `hash(userId) % 24` 選圖示；
  該群組此圖示已有人用 → `Label` 加序號（小熊 → 小熊 2 → 小熊 3，圖示同款、底色不同）。
  指派後寫入 DB，之後永遠讀同一筆。
- 併發防護：GET 訊息端點會觸發寫入（冪等指派），兩個請求同時指派同一人時
  靠複合主鍵擋下，撞鍵就重讀既有那筆（try/catch `DbUpdateException` 後 re-query）。
- 代號指派表是「檢視端的顯示設定」性質，跟別名表（UserAliases）同層級，
  放檢視端資料模型合理，收錄端完全不用知道它。

**放棄的替代案**：純雜湊+成員清單排序推序號（免 DB）——成員增減會讓既有人的
序號位移（小熊 2 變小熊 3），紀錄不可追溯，嚴肅環境不能接受，故不採。

**Schema 影響（誠實面對）**：這會動 `MessageService.Data`——新增一張表 + 一支 migration。
Sqlite 開發庫走 `EnsureCreated()`，既有 `messages.db` **不會**自動長出新表，
實作時需一併處理：開發環境重建 messages.db（表內都是測試資料，成本低），
並在 README 註記；SqlServer 生產走 migration 正常升級。

---

## 4. 後端改動清單

### 4.1 DTO
- `MessageDto`：加 `string? PictureUrl`、`string? AvatarIcon`（尾端加欄位，named-argument 呼叫端不受影響）。
  - `Original` 模式：`PictureUrl` = 真實頭貼、`AvatarIcon` = null（前端載圖失敗才 fallback 代號圖示，
    fallback 用的 IconKey 另以 `AvatarIcon` 一併帶出 → 兩欄都給，前端優先用 PictureUrl）。
  - 其他模式：`PictureUrl` = null、`AvatarIcon` = 指派的 IconKey。
- `GroupDto`：加 `string? LastMessagePreview`、`DateTimeOffset? LastMessageAt`、`int MemberCount`。

### 4.2 `GroupsController.GetGroups`
- 每群組多查「最後一則訊息」：`GroupMessages.GroupBy(m => m.GroupId).Select(g => g.OrderByDescending(m => m.Id).First())`
  （EF Core 10 可轉成 ROW_NUMBER，單一查詢，無 N+1）。
- 預覽文字：text → 套 `MaskText`（**遮蔽規則必須套用**，不然側欄變成遮蔽漏洞）截 30 字；
  非文字型別 → `[圖片]` `[影片]` `[語音訊息]` `[檔案]` `[貼圖]`。
- `MemberCount`：`GroupMembers` 依 GroupId 計數（一次 GroupBy 查詢）。
- 排序改為 `LastMessageAt` 倒序（原本是名稱排序）。

### 4.3 `MessagesController.GetMessages`
- 已經有 `members` 字典（含 `PictureUrl`），組 DTO 時帶出：
  `PictureUrl = 模式為 Original ? member?.PictureUrl : null`。
- 呼叫 `IAnonymousIdentityService.GetOrAssignAsync(groupId, userIds)` 取得代號對照
  （一個請求一次批次查詢/指派，不逐訊息打 DB），填 `AvatarIcon`。
- 需要知道目前模式 → `IMaskingRuleSet` 加唯讀屬性 `bool RevealsOriginalProfile`
  （`mode == NameDisplayMode.Original`），`MaskingRuleSet` 實作。

### 4.4 名稱解析（`MaskingRuleSet` / 新模式）
- `NameDisplayMode` enum 追加 `Anonymous`（`MessageService.Data`，值追加不改既有值）。
- `ResolveDisplayName` 擴充：`Anonymous` 模式回傳代號 `Label`（「小熊 2」）。
  代號對照由呼叫端傳入（`MaskingService.LoadRulesAsync` 維持全域規則、
  代號因為是 per-group 由 controller 併入），介面簽名微調時同步改測試。
- `SettingsController` 的 `Enum.TryParse` 自動吃新值，不用改；
  設定頁 radio 加第 4 個選項「完全匿名（動物/植物代號）」+ 說明文字。

### 4.5 新增：代號指派（見 §3.4）
- `MessageService.Data`：`AnonymousIdentity` 實體 + `MessageDbContext` DbSet + migration。
- `MessageService.Web/Services`：`IAnonymousIdentityService` / `AnonymousIdentityService`
  （含 IconKey↔中文代號對照常數表，前端 sprite 的 id 與之對齊）。

### 4.6 不動的部分
- 收錄端（MessageService 專案）：**零改動**。
- Settings API 端點、內容串流、中介層（IpAllowlist / CancelledRequest）：零改動。
- `ViewerSettings` 資料表：零改動（enum 存 int，加值免 migration）；
  DB 變更只有 §3.4 的新表一張。

---

## 5. 前端改動清單

| 檔案 | 改動 |
|---|---|
| `Views/Shared/_Layout.cshtml` | 拆成兩種殼：chat-page 走全螢幕 app shell（無 navbar/footer）；其他頁（Settings/Error）保留簡化版頂列（返回鏈結+頁名）。`lang` 改 `zh-Hant`、`<title>` 改「訊息紀錄」 |
| `Views/Home/Index.cshtml` | 重寫為雙欄結構：側欄（標題/搜尋/群組列表/設定入口）+ 聊天面板（標頭/訊息區/狀態列/回到最新鈕）+ 圖片 modal 照舊 |
| `Views/Home/Settings.cshtml` | 結構重排為卡片式；名稱顯示區加第 4 個 radio「完全匿名（動物/植物代號）」+ 說明；其餘控件 id 不變 |
| `wwwroot/css/chat.css` | 大改：§2.6 色彩 token、側欄、透明標頭、仿 LINE 底部列、泡泡（圓角 18px + 首顆小尾巴偽元素）、**頭貼改頂端對齊**、名字/時間戳改深色、回到最新鈕改白底圓角方形、響應式；現有 keyframes / reduced-motion 保留 |
| `wwwroot/css/site.css` | 加設定頁卡片樣式；Bootstrap 保留（modal、toast、表單控件還在用） |
| `wwwroot/js/chat.js` | 中改：`loadGroups()` 改渲染側欄列表、新增 `renderGroupList()` / 搜尋過濾 / `updateChatHeader()`、`createMessageRow()` 頭貼改為 img+SVG fallback（圖示一律依伺服器給的 `AvatarIcon` key 渲染，前端不再自己雜湊選圖）、文字訊息 linkify（只轉 URL 為 `<a>`，其餘仍走 `textContent`，不引入 HTML 解析）、底部列同步點接上 `setConnectionOk()`、手機版返回鈕。**state 機制、輪詢、分頁、token 防競態全部不動** |
| `wwwroot/js/settings.js` | 小改：只補匿名說明文字的顯示邏輯（若有），其餘不動 |
| `wwwroot/img/default-avatars.svg` | 新增：8 成員圖示 + 1 群組剪影 sprite |

---

## 6. 影響評估

### 6.1 測試（MessageService.Web.Tests，現 95 綠）
- `MessagesControllerTests`：DTO 多欄位——現有斷言若用整物件比對需補 `PictureUrl`；
  **新增**：Original 模式回真實 URL、MaskMiddle/CustomAlias 模式強制 null（防洩漏回歸測試，最重要）。
- `GroupsControllerTests`：新欄位 + 排序改為時間倒序 → 現有排序斷言要改；
  **新增**：預覽有套關鍵字遮蔽、非文字型別預覽文案、空群組（無訊息）欄位為 null。
- `MaskingRuleSetTests`：補 `RevealsOriginalProfile` 四模式斷言、`Anonymous` 模式名稱解析。
- **新增 `AnonymousIdentityServiceTests`**：首次指派寫入 DB、二次呼叫讀同一筆（穩定性）、
  同群組同圖示自動加序號（小熊/小熊 2）且底色互異、跨群組同一人可為不同代號、
  併發撞複合主鍵時 fallback 重讀、24 款圖示 key 與中文代號對照表完整性。
- `MessagesControllerTests` 另補：`Anonymous` 模式 DisplayName=代號且與 `AvatarIcon` 同組、
  同一使用者跨兩次請求代號不變。
- 前端 JS 無測試框架（現況即如此），維持人工驗證。
- 預估 95 → 約 115+ 綠。

### 6.2 風險與緩解
| 風險 | 緩解 |
|---|---|
| LINE CDN 頭貼 URL 過期 / 拒絕 hotlink → 破圖 | `onerror` fallback 預設圖示；`referrerpolicy="no-referrer"`；收錄端 ProfileRefresh 本來就會更新 URL |
| 側欄最後訊息預覽繞過遮蔽 | 預覽在伺服器端套同一套 `MaskText`，並寫回歸測試 |
| `PictureUrl` 在匿名模式下經 API 洩漏 | 伺服器端裁決設 null（不是前端隱藏），並寫回歸測試 |
| chat.js 重寫弄壞既有行為（防競態、跟隨、pending 輪詢） | 只換渲染函式，state 機制原封不動；改版後逐項人工驗收清單（見 §7 Phase 8） |
| Bootstrap 移除造成連鎖破壞 | 不移除，只覆蓋樣式 |
| 群組列表查詢變重（last message + member count） | 各一次集合查詢；群組數量級小（個位數~數十），可接受 |
| 既有 Sqlite 開發庫沒有新表（`EnsureCreated()` 不補表） | 開發環境重建 messages.db（皆為測試資料）；SqlServer 生產走 migration；README 註記 |
| GET 訊息端點觸發代號指派寫入，併發重複指派 | (GroupId, UserId) 複合主鍵擋重複，撞鍵 catch 後重讀既有指派；指派冪等 |
| 代號一經指派永久有效，切回 Original 再切回匿名仍是同一組 | 這是特性不是 bug（紀錄可追溯）；如需重洗代號，日後可加「重設匿名代號」管理功能（本次不做） |

### 6.3 分支現況注意事項
建立分支時工作目錄帶著**一批與本案無關的未 commit 改動**
（`CancelledRequestMiddleware` 新檔與掛載、`ContentDownloadService` 重試調整、chat.css/js 部分增補等，
共 10 檔 +180/−18）。看起來是上一輪未收尾的工作。
**建議先把這批改動 commit 成獨立 commit（或回 master 處理完再 rebase）**，
不要和 UI 改版混在同一批 commit，否則 review 和回溯都會很痛。

---

## 7. 實作階段切分（實作時逐階段驗證）

- **Phase 1 後端**：`AnonymousIdentity` 實體 + migration + 指派服務 + `NameDisplayMode.Anonymous`
  + DTO 欄位 + `RevealsOriginalProfile` + 兩個 controller 查詢 + 測試（全綠才進下一階段）。
- **Phase 2 版面骨架**：_Layout 雙殼 + Index 雙欄結構 + 基礎 CSS token（此時功能照舊、只是長相變）。
- **Phase 3 側欄**：群組列表渲染、預覽、排序、搜尋、選取態。
- **Phase 4 頭貼**：SVG sprite、img+fallback、匿名連動。
- **Phase 5 聊天面板精修**：標頭、狀態列、泡泡/浮動鈕/載入更早膠囊、Aa 下拉。
- **Phase 6 設定頁**：卡片化 + 匿名說明。
- **Phase 7 響應式**：手機版列表↔聊天切換。
- **Phase 8 體檢**：`dotnet test` 全綠 + 人工驗收清單（切群組防競態、載入更早、置底跟隨/未讀、
  pending→完成替換、連線中斷恢復、三種名稱模式×頭貼、字級記憶、圖片 modal、手機版）。

---

## 8. 決策點（2026-07-30 已全部確認）

1. **頭貼匿名與名稱模式連動**：✅ 採連動方案（§3.2），`ViewerSettings` 不加欄位；
   另依補充需求新增 `Anonymous` 模式與代號指派表（§3.4，唯一的 DB 變更）。
2. **配色**：✅ 以 LINE 標準配色為主，依實機截圖取樣（§2.6 token 表）。
3. **對話樣式**：✅ 以截圖為視覺基準——透明標頭、頭貼頂端對齊、深色名字/時間戳、
   泡泡小尾巴要做；底部列仿 LINE 輸入列但唯讀化（§2.4）。
   回覆引用、連結預覽卡、公告列不做（無對應資料）。
4. **深色模式**：✅ 不做，只出預設（亮色）樣式。
5. **匿名代號系統**（2026-07-30 補充需求）：✅ 匿名下要能分辨不同發言者；
   圖示庫 24 款動植物自由發揮，排除豬/狗等口語帶貶義的動物（§3.3）；
   匿名模式名稱=動植物代號、同群組撞名自動加序號、指派持久化保證紀錄可追溯（§3.4）。

### 補充的實作備註
- linkify 一律用 `document.createElement('a')` + `textContent` 組 DOM，
  不走 innerHTML——訊息內容是外部輸入，不能開 HTML 注入面。
- 底部列圖示（＋/相機/相簿/表情/麥克風）以內嵌 SVG 畫，與頭貼 sprite 同檔管理或另立
  `wwwroot/img/ui-icons.svg`，維持零外部依賴。
