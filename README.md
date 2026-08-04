# MessageService

LINE 群組訊息收錄與檢視系統，兩個獨立部署的 ASP.NET Core 專案共用同一顆資料庫：

- **MessageService**（收錄端）：LINE bot webhook，收訊息、下載媒體、每日清除逾期資料。只寫不查。
- **MessageService.Web**（檢視端）：唯讀網頁，把收錄到的對話用類似 LINE 聊天視窗的介面呈現，並提供關鍵字/名稱遮蔽設定。

```
MessageService.sln
├── MessageService/          # 收錄端（webhook + 背景服務）
├── MessageService.Data/     # 共用：實體、DbContext、EF Core migrations
├── MessageService.Web/      # 檢視端（MVC + API + 前端）
├── MessageService.Tests/
└── MessageService.Web.Tests/
```

---

## MessageService（收錄端）

bot 加入群組後，透過 LINE webhook 接收群組內的訊息並寫入資料庫。職責只有三件事：**收 webhook → 落地資料庫 → 每日清除逾期資料**，不提供查詢 API。

### 架構

```
LINE Platform ──POST──▶ LineWebhookController
                            │ 1. 驗證 X-Line-Signature（HMAC-SHA256）
                            │ 2. 反序列化 webhook events
                            ▼
                     WebhookEventHandler
                            │ 過濾群組訊息、防重送、依型別分流
                            ▼
                ┌── GroupMessages / MessageContents(Pending) ──┐
                │                                              │
                ▼ 入列                                          │ 入列
     ContentDownloadQueue（Channel）              ProfileRefreshQueue（Channel）
                ▼                                              ▼
     ContentDownloadService（背景下載原檔）          ProfileRefreshService
       影片/語音先等 LINE 轉檔，失敗重試，              （背景快取群組名稱與成員顯示名稱，
        完成後寫回 MessageContents）                    7 天 TTL，呼叫 LINE profile API）

RetentionCleanupService（每日固定時間刪除超過保留年限的訊息，內容檔案靠 CASCADE 一併刪除）
```

### 訊息型別處理

| 型別 | Text 欄位 | 內容下載 |
|---|---|---|
| 文字 | 訊息內文 | — |
| 貼圖 | `(貼圖)`（fallback 顯示用） | —（不下載檔案；`StickerId`/`PackageId` 另存兩個欄位，檢視端據此顯示真實貼圖圖片） |
| 圖片 | null | 原圖（非縮圖），背景下載 |
| 影片 | null | 原檔，先輪詢 LINE transcoding 完成才下載 |
| 語音 | null | 原檔，同影片先等 transcoding 完成才下載 |
| 檔案 | null（檔名存於內容表） | 原檔，背景下載 |
| 其他（位置等） | 略過不存 | — |

### 設定

| 設定鍵 | 說明 |
|---|---|
| `Database:Provider` | `SqlServer`（正式，appsettings.json 預設）或 `Sqlite`（開發，appsettings.Development.json 預設） |
| `ConnectionStrings:SqlServer` / `Sqlite` | 連線字串 |
| `Line:ChannelSecret` | 簽章驗證用。**勿進版控**，開發用 user-secrets、正式用環境變數 |
| `Line:ChannelAccessToken` | 內容下載與 profile API 用，同上 |
| `Retention:Years` | 保留年限（預設 3） |
| `Retention:CleanupTimeOfDay` | 每日清除時間（本地時間，預設 03:00:00） |
| `ContentDownload:MaxRetries` | 下載重試次數（預設 3） |
| `ContentDownload:RetryDelayMilliseconds` | 重試間隔基數，逐次遞增（預設 2000） |
| `ContentDownload:TranscodingPollSeconds` / `TranscodingMaxPolls` | 影片/語音轉檔輪詢間隔與次數上限（預設 5 秒 × 24 次） |
| `ProfileCache:RefreshAfter` | 群組/成員名稱快取的過期時間（預設 7 天） |

開發環境設定機密（在 `MessageService/` 目錄下）：

```bash
dotnet user-secrets init
dotnet user-secrets set "Line:ChannelSecret" "<你的 channel secret>"
dotnet user-secrets set "Line:ChannelAccessToken" "<你的 access token>"
```

### 本機串接 LINE 測試

完整的逐步操作（建立 Bot、取得金鑰、Webhook 設定、測試驗收、疑難排解）see **[LINE-BOT-SETUP.md](LINE-BOT-SETUP.md)**。快速版：

1. LINE Developers Console 建立 Messaging API channel，**啟用 Allow bot to join group chats**（不開就完全收不到群組訊息）
2. 取得 Channel Secret（Basic settings 分頁）與 Channel Access Token（Messaging API 分頁），用 user-secrets 寫入
3. `dotnet run --project MessageService --urls http://localhost:5072` 啟動（刻意只掛 HTTP，避免 `UseHttpsRedirection` 把 LINE 的 webhook 轉址掉）
4. 用 dev tunnel 或 ngrok 將該 port 開成 HTTPS URL
5. 在 LINE console 設定 Webhook URL 為 `https://<tunnel>/api/line/webhook`、啟用 Use webhook，按 Verify 確認
6. 將 bot 拉進測試群組，發文字/貼圖/圖片/影片/語音/檔案各一，確認 SQLite 落地與 `DownloadStatus` 流轉

---

## MessageService.Web（檢視端）

唯讀網頁，值班/維運人員用來瀏覽 LINE 群組對話。除了讀取訊息，**設定會寫入自己的設定資料**（遮蔽規則、名稱顯示模式、別名），這是本專案唯一會寫資料庫的地方。

### 頁面與 API

**對話頁（`/`）**：原生 JS 模擬 LINE 的雙欄版面（Bootstrap 只保留 modal/toast/表單控件），所有資料透過 API 取得（不走 MVC Model 傳遞）。

- **左側欄**：群組列表（頭貼、名稱、最後訊息預覽、時間，依最後活動倒序），前端即時搜尋過濾，底部為設定入口（開啟設定 modal）。取代早期的下拉選單；側欄與聊天面板的所有區塊共用同一份 `--gutter`/`--radius-*` token（chat.css），群組項目與設定入口是內縮圓角卡而非通版直角色塊
- **聊天面板**：標頭有自己的底色（`--line-header-bg`，比訊息區深一階）＋陰影，跟訊息區明確分層（群組頭貼＋名稱＋成員數＋🔍搜尋＋「Aa」字級下拉）；訊息泡泡首顆帶指向頭貼的小尾巴（左上角、跟著字級用 em 縮放，避免大字級時圓角比尾巴大造成脫節）、時間戳貼泡泡外側；底部仿 LINE 輸入列但唯讀化（圖示灰化不可點、中央膠囊顯示同步狀態）
- **頭貼**：`Original` 模式顯示真實 LINE 頭貼（`referrerpolicy="no-referrer"`，載入失敗 fallback 代號圖示）；其他模式一律顯示伺服器指派的動植物代號圖示（emoji 渲染，前端 `ICON_EMOJI` 對照表需與後端 `AvatarIconCatalog` 的 IconKey 同步維護）
- **字級**：設定「字體大小」數值輸入（px）＝聊天頁「中」檔泡泡文字的實際大小，小／大依比例（.87×／1.13×）跟著調整；聊天頁全部文字（不含頭貼/圖示）與設定 modal 本身的文字都吃同一份 `--font-base-px`（localStorage key `chat-font-base-px`，設在 `document.documentElement` 上、透過 inline style 覆寫、不寫死在樣式表裡，才不會被 CSS cascade 蓋掉）。設定 modal 跟聊天頁是同一個頁面，調字級時背後的聊天畫面會即時跟著變
- 預設載入 3 天內對話，「載入更早 7 天」膠囊以最舊訊息 Id 當游標往前翻頁，沒有更早歷史時自動 disable。兩個「按了會沒反應」的空窗情況都有處理：畫面上一則訊息都沒有（沒有游標可用）時改成放大天數視窗重繪；群組沉寂比一個視窗還久時由 API 把視窗錨定到下一則更早訊息，保證每次點擊都會前進
- 「回到最新」浮動按鈕：使用者往上捲動時自動退出跟隨模式並顯示未讀數，點擊或捲回底部即恢復跟隨並自動捲到新訊息
- **訊息搜尋**：標頭 🔍 展開搜尋列（本群組／全部群組切換），比對訊息內容與發言者名稱，結果以 `<mark>` 高亮；點結果用 `aroundId` 跳轉到該訊息的上下文並閃爍定位，同時把視窗內符合的文字也標出來。跳轉後進入「歷史檢視」，此時 4 秒訊息輪詢**只更新 Pending 內容狀態、不把新訊息接到視窗尾端**（避免時間軸斷層），畫面改顯示「回到最新」常駐按鈕，點擊會呼叫既有的群組選取流程整個重置回即時畫面
- 每 3 秒輪詢新訊息與 Pending 內容的下載狀態，每 10 秒輪詢側欄群組列表（新群組/預覽/排序，歷史檢視期間依然照跑）；分頁隱藏（`document.hidden`）時皆暫停輪詢
- 新訊息進場有淡入＋位移動效（`prefers-reduced-motion` 使用者會停用）
- 圖片／影片／語音／檔案依 `DownloadStatus` 顯示 spinner／播放器／下載連結／失敗訊息；圖片點擊開全螢幕燈箱（白框＋右上角 ✕，點空白處或 Esc 皆可關閉），預設縮到剛好符合視窗（不放大本來就比較小的圖），再點一次切換原始尺寸並可捲動查看局部
- 貼圖顯示真實圖片（LINE 公開貼圖 CDN），浮貼在背景上不裝進白色泡泡；載入失敗或訊息本身沒有 `StickerId`（改版前收到的貼圖沒有這個欄位，LINE 不提供舊訊息回溯查詢）一律 fallback 回「(貼圖)」文字
- 文字訊息中的網址會轉成可點連結（`target="_blank"` + `rel="noopener noreferrer"`）；所有內容（含搜尋高亮）一律用 DOM 節點組裝（`textContent`／`createElement`），不用 `innerHTML`，避免訊息內容造成 XSS
- **手機版（<768px）**：群組列表與聊天面板全螢幕切換，標頭出現「‹」返回鈕，仿 LINE 手機版導覽

**設定**：聊天頁裡的寬版 modal（`modal-xl`，手機自動轉全螢幕），不再是獨立頁面——`/Home/Settings` 路由已移除。上方三個頁籤：介面顯示（字體大小 px 數值設定，見上方「字級」）、隱私與匿名（名稱顯示四模式，含完全匿名動植物代號；別名編輯器可依群組篩選成員）、關鍵字遮蔽規則（新增/刪除，預設等長 `*` 或自訂替換字串，全部群組或指定群組）。變更即存（字體大小為 localStorage、不進 DB，其餘 PUT 後顯示 toast）；資料只在**第一次**打開 modal 時才載入（`shown.bs.modal` 才打 API，不會讓聊天頁一開就多打一輪設定用的請求），成功寫入任何變更後關閉 modal 會自動重新整理目前的訊息視窗與側欄，不用手動重新整理頁面。

**API**（都在 `MessageService.Web/Controllers/Api/`）：

| 端點 | 用途 |
|---|---|
| `GET /api/groups` | 群組清單（僅列出有訊息的群組，名稱取自快取，無快取則顯示 GroupId；含最後訊息預覽〔已套遮蔽〕、最後訊息時間、成員數，依最後活動倒序） |
| `GET /api/groups/{groupId}/messages?days=` / `?beforeId=&days=` / `?afterId=` / `?aroundId=&days=` | 初載 / 往前加載 / 輪詢新訊息 / 以指定訊息為錨點開前後視窗（搜尋結果跳轉用），回應已套用遮蔽 |
| `GET /api/messages/{id}/content` | 內容串流，支援 HTTP Range（見下方實作說明） |
| `GET /api/messages/statuses?ids=` | 查詢多筆內容目前的 `DownloadStatus` |
| `GET /api/messages/search?q=&groupId=` | 訊息搜尋，比對文字訊息內容與解析後的發言者名稱，`groupId` 省略＝搜尋全部群組，上限 100 筆、新→舊排序（見下方「訊息搜尋」） |
| `GET/PUT /api/settings/display` | 名稱顯示模式 |
| `GET/POST/PUT/DELETE /api/settings/keywords[/{id}]` | 關鍵字遮蔽規則 CRUD |
| `GET/PUT/DELETE /api/settings/aliases[/{userId}]` | 使用者別名對照 |
| `GET /api/users?groupId=` | 別名編輯器用的成員清單（可選群組篩選） |

**內容串流的技術重點**：影片/語音的 Range 請求（拖拉進度）不能靠 ADO.NET 的 blob stream 做 `Seek`——SQL Server 與 SQLite 的 blob stream 都是 forward-only，不支援真正的隨機存取。因此 Range 切片直接在 SQL 端用 `SUBSTRING`/`substr` 完成，每個 Range 請求只讀取實際要傳送的位元組，不會把整個檔案載進應用程式記憶體（`MessageService.Web/Services/ContentStreamService.cs`）。

### 遮蔽機制

`IMaskingService.LoadRulesAsync()` 每個請求只呼叫一次，把當下的名稱顯示模式、關鍵字規則、別名對照載成一份 `IMaskingRuleSet` 快照，套用到該次回應的每則訊息時全是同步運算，避免每則訊息各打一次 DB。

- **關鍵字遮蔽**：不分大小寫的純字串比對（不用 regex），可設定全部群組或指定群組套用；預設遮蔽為與關鍵字等長的 `*`，也可設定自訂替換字串
- **名稱顯示四模式**：
  - `Original`：顯示原始快取的 LINE 顯示名稱，沒有快取則顯示 UserId；唯一會回傳真實頭貼 URL 的模式
  - `MaskMiddle`：首尾字保留、中間 `*`（1 字全遮；2 字只留首字，如「小明」→「小*」；3 字以上首尾各留一字，如「王小明」→「王*明」）
  - `CustomAlias`：依 `UserAliases` 對照表顯示别名；沒設定別名的人 fallback 為 `MaskMiddle`
  - `Anonymous`：名稱與頭貼一律替換為動植物代號（如「小熊」），由 `IAnonymousIdentityService` 依群組+使用者永久指派並存進 `AnonymousIdentities`，翻閱舊訊息時代號不會變、可分辨是否為同一人但認不出真實身分

以上四種模式（`Original` 除外）回應中一律不含真實 `PictureUrl`，即使前端不渲染也不外流，因為 URL 本身就是身分線索。

### 訊息搜尋

`GET /api/messages/search`（`MessagesController.Search`）比對文字訊息內容與解析後的發言者名稱，兩者符合其一即算命中；核心設計是不能讓搜尋變成遮蔽機制的後門：

- **內容比對**：SQL 端先用原文 `LIKE`（`EF.Functions.Like` 帶 `ESCAPE`，`%`/`_`/`\` 會被跳脫成字面）撈候選，於記憶體用 `MaskingRuleSet.MaskText` 套用後的文字**重新驗證**仍含關鍵字才算命中——被關鍵字規則遮掉的詞（如「密碼」）搜不到，摘要也只顯示遮蔽後的文字，不會洩漏原文。
- **名稱比對**：走當下顯示模式**解析後**的名稱（`Original` 比對原名、`MaskMiddle`/`CustomAlias` 比對遮蔽後名稱或別名、`Anonymous` 比對動植物代號），符合的成員底下所有訊息都算命中（不限訊息內容本身有沒有關鍵字）。`Anonymous` 模式下只讀 `AnonymousIdentities`、**不觸發指派**——沒被指派過代號的成員姓名比對就是找不到，指派只應該發生在使用者實際開啟訊息視窗時。
- **範圍與限制**：`groupId` 省略即搜尋全部群組；只搜文字訊息的 `Text` 欄（媒體訊息無文字可搜，檔名未經遮蔽管線、不搜）；結果上限 100 筆、依 `EventTimestamp` 新到舊排序，不做分頁；`Text` 欄無索引，全掃在目前資料量級（單機、萬則內）可接受，量大再評估 FTS。
- **跳轉上下文**：搜尋結果可用 `GET /api/groups/{groupId}/messages?aroundId={messageId}&days=` 取得以該訊息為錨點、前後各 `days` 天的視窗（含該訊息本身），供前端捲動並高亮；此模式回應不含 `latestId`（跳轉後屬歷史檢視，前端會暫停新訊息輪詢，不需要輪詢基準）。

### IP 白名單（沒有登入機制）

`IpAllowlistMiddleware` 掛在管線最前面，擋下所有請求。**空白名單視為全拒**（寧嚴勿鬆）。

```jsonc
// appsettings.json
"AllowedClientIps": [ "127.0.0.1", "::1", "10.1.0.0/24" ],  // 支援單一 IP 與 CIDR 網段
"UseForwardedHeaders": false  // 部署在反向代理（IIS/nginx）後面時才需要開啟
```

放行與拒絕的來源 IP 都會記 NLog。若部署在反向代理後面卻沒開 `UseForwardedHeaders`，中介層看到的會是代理的 IP 而非真實來源，白名單會失效——這是最容易忘記的坑。

### 資料庫存取

`MessageDbContext` 不設全域 `NoTracking`：查詢型端點（對話、群組、遮蔽規則載入）各自在查詢上加 `.AsNoTracking()`，設定的「讀取實體→改屬性→存檔」寫入流程才能正常被 change tracker 偵測到。這是實測踩到的坑——曾經設過全域 `NoTracking` 以為整個 Web 專案只讀，結果讓 `UpdateKeyword`/`UpsertAlias` 的更新靜默失敗（改了值但沒真的寫進 DB，因為沒有東西被追蹤）。

### 設定

| 設定鍵 | 說明 |
|---|---|
| `Database:Provider` / `ConnectionStrings:*` | 與收錄端指向同一顆資料庫 |
| `AllowedClientIps` | IP 白名單，見上 |
| `UseForwardedHeaders` | 反向代理後方時開啟 |

---

## 共用資料表（`MessageService.Data`）

**GroupMessages**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| WebhookEventId | nvarchar(450) | LINE 事件 ID，唯一索引（防 redelivery 重複寫入） |
| LineMessageId | nvarchar(max) | LINE 的 message id |
| GroupId | nvarchar(max) | 來源群組 |
| UserId | nvarchar(max), null | 發言者（未加 bot 好友時可能為 null） |
| MessageType | nvarchar(max) | text / sticker / image / video / audio / file |
| Text | nvarchar(max), null | 文字內容或 `(貼圖)`（顯示時會套遮蔽規則） |
| EventTimestamp | datetimeoffset | LINE 事件時間 |
| ReceivedAt | datetimeoffset | 收錄端收到時間 |

**MessageContents**（1:1 對 GroupMessages，ON DELETE CASCADE）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| GroupMessageId | bigint FK | |
| FileName | nvarchar(max), null | 檔案訊息的原始檔名 |
| ContentType | nvarchar(max), null | 下載完成後的 MIME type |
| DownloadStatus | nvarchar(max) | Pending / Completed / Failed |
| Content | varbinary(max), null | 原始檔案內容，Pending/Failed 時為 null |
| CompletedAt | datetimeoffset, null | 下載完成時間 |

**Groups** / **GroupMembers**：收錄端背景快取的群組名稱、成員顯示名稱與頭像 URL（7 天 TTL，來源是 LINE 的 group summary / member profile API），檢視端用來把 GroupId/UserId 轉成人看得懂的名稱。快取失敗時 fallback 顯示原始 ID。

**ViewerSettings**（單列，Id 固定為 1）／**MaskKeywords** + **MaskKeywordGroups**／**UserAliases**／**AnonymousIdentities**：檢視端寫入的顯示設定，只有這幾張表是 Web 專案會寫入的。`AnonymousIdentities`（GroupId+UserId 複合主鍵）是 `NameDisplayMode.Anonymous` 的代號永久指派表，跟其他幾張不同的地方是使用者不直接編輯——由 `GET /api/groups/{groupId}/messages` 第一次遇到某成員時自動指派並寫入。

這些表的欄位是兩個專案間（以及未來其他消費端）的共用契約，異動需評估相容性。

## 資料庫初始化

Migrations 統一放在 `MessageService.Data`，用 `MessageService` 當 startup project：

- **SQLite（開發）**：兩個專案啟動時都各自 `EnsureCreated()`，免手動（收錄端在 `Program.cs`；檢視端目前假設 schema 已由收錄端建立）。**注意**：`EnsureCreated()` 只在資料庫檔案完全不存在時依目前模型建表，對已存在的 SQLite 檔案不會補新表/新欄位——加了新表（例如 `AnonymousIdentities`）後，既有的本機 `messages.db` 要手動刪除讓它重建，或改走 migration。
- **SQL Server（正式）**：

```bash
ASPNETCORE_ENVIRONMENT=Production dotnet ef database update --project MessageService.Data --startup-project MessageService
```

> 注意：`dotnet ef` 指令預設會讀 `MessageService/Properties/launchSettings.json` 的 `ASPNETCORE_ENVIRONMENT=Development`，套到 SQLite 設定。操作 SQL Server migration（`migrations add`/`database update`）時必須顯式指定 `ASPNETCORE_ENVIRONMENT=Production`，否則會產生 SQLite 語法的 migration。

## 設計決策備忘

- **圖片/影片/語音/檔案存 DB（varbinary）而非磁碟**：檢視端只要連 DB 就能讀到；保留期清除靠 CASCADE 一次帶走，不會產生孤兒檔案。代價是 DB 容量成長快，若量大屆時再評估 FILESTREAM 或磁碟存放（內容獨立一表已為搬遷留好最小改動面）
- **webhook 一律回 200**（簽章合法後）：回非 2xx 會讓 LINE 重送並可能判定 webhook 失效；個別事件失敗只記 log
- **下載走背景佇列**：影片/語音要等 LINE 轉檔、檔案可達數百 MB，不能在 webhook 請求內同步處理；服務重啟會自動撈回殘留的 Pending 接續下載
- **群組/成員名稱背景快取**：同樣不在 webhook 請求內同步呼叫 LINE profile API，避免拖慢 webhook 回應
- **SQLite 的 DateTimeOffset 限制**：SQLite 只支援相等比較，`<`/`>` 無法轉譯。`MessageDbContext` 在 SQLite 環境對需要範圍比較的 `DateTimeOffset` 欄位（`EventTimestamp`、`Groups`/`GroupMembers` 的 `UpdatedAt`）套 `DateTimeOffsetToBinaryConverter`；SQL Server 維持原生 `datetimeoffset`（保持型別對其他工具可讀）
- **BackgroundService 例外一律就地捕捉**：.NET 6+ 預設未捕捉例外會停掉整個 host，清除或下載失敗只能記 log 等下輪，不能讓服務跟著死
- **檢視端沒有登入機制**：IP 白名單是最低防護，空白名單視為全拒
- **檢視端 DbContext 不設全域 NoTracking**：設定需要寫入，只在真正唯讀的查詢路徑個別加 `.AsNoTracking()`（見上方「資料庫存取」）
- **`ViewerSettings.Id` 是固定值而非資料庫產生**（`ValueGeneratedNever`）：這是單列設定，Id 恆為 1。留成 SQL Server identity 的話，程式碼補建這列時帶著 Id=1 會撞上 `IDENTITY_INSERT` 關閉而失敗
- **往前翻頁一定要能前進**：純粹「以游標時間往前 N 天」開窗，遇到比視窗還長的沉寂期會永遠回空、游標不動，按鈕看起來可按卻沒反應。API 因此會在視窗落空時把視窗錨定到下一則更早訊息

## 日誌（NLog）

兩個專案都用 NLog，各自輸出到 Console 與**執行檔目錄下的 `logs/{專案名}-{日期}.log`**（每日一檔，保留 30 天）。`Microsoft.*` 的雜訊只留 Warning 以上。

- **收錄端**：收到並存檔的每則訊息（型別/群組/是否排入下載）、內容下載完成（大小/MIME）、下載重試與最終失敗、影片/語音轉檔失敗、啟動接續補跑數量、保留期清除筆數與下次排程時間、簽章驗證拒絕
- **檢視端**：IP 白名單放行/拒絕的來源

## 測試

```bash
dotnet test
```

- `MessageService.Tests`：簽章驗證、五種型別分流（含 audio）、防重送、背景下載（成功/轉檔輪詢/轉檔失敗/重試耗盡/啟動接續）、群組/成員名稱快取（新增/過期更新/API 失敗 fallback）、保留期清除（含 CASCADE 驗證）、Controller 整合測試（401/200/畸形 body 仍 200）
- `MessageService.Web.Tests`：Groups/Messages API（分頁游標、hasMore、空視窗仍回 latestId、沉寂期長於視窗仍能翻頁、遮蔽套用）、內容串流（200/206/416/malformed Range）、Settings API（CRUD、群組範圍替換、單列設定被刪後補建）、`MaskingService`/`MaskingRuleSet`（含名稱遮蔽邊界情況）、IP 白名單 middleware（允許/拒絕/空白名單/CIDR）

測試都使用 SQLite（in-memory 或暫存檔），Web 端整合測試用 `IStartupFilter` 在 TestServer 補一個固定來源 IP（TestServer 的請求沒有真正 TCP 連線，`Connection.RemoteIpAddress` 預設是 null）。
