# MessageService

> 本檔只講現行版本的事實；歷次規劃、審查回饋、設計決策理由等修改歷程放在
> [docs/history/](docs/history/)，非必要不需要讀，避免浪費 token。

LINE 群組訊息收錄與檢視系統，單一 ASP.NET Core 專案，依部署模式決定角色：

- **收錄**：LINE bot webhook，收訊息、下載媒體、每日清除逾期資料。
- **檢視**：唯讀網頁，把收錄到的對話用類似 LINE 聊天視窗的介面呈現，並提供關鍵字/名稱遮蔽設定。

兩者可以是同一台主機（單機部署，一次收齊），也可以拆成兩三台各自負責一部分——同一份程式碼、
同一份發佈成品，角色只是設定差異，見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md)。

```
MessageService.sln
├── MessageService.Data/       # 共用：實體、DbContext、EF Core migrations（Sqlite／SqlServer 各一套）
├── MessageService.Web/        # 唯一的可發佈專案：webhook + 背景服務 + MVC + API + 前端
└── MessageService.Web.Tests/  # 唯一的測試專案
```

**其他文件**：[docs/DEPLOYMENT-GUIDE.md](docs/DEPLOYMENT-GUIDE.md)（從零上線的部署操作手冊：IIS、SQL Server／SQLite、LINE 設定，第一次接觸本專案的人從這份開始）、
[docs/LINE-BOT-SETUP.md](docs/LINE-BOT-SETUP.md)（建立 Bot 與串接的逐步操作、疑難排解）、
[docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md)（四種部署角色：架構與能力矩陣）、
[docs/ENCRYPTION.md](docs/ENCRYPTION.md)（應用層欄位加密：範圍、金鑰設定、搜尋限制）。
檢視端歷次改版的設計決策理由與放棄的替代方案是修改歷程，見
[docs/history/WEB-UI-DESIGN-NOTES.md](docs/history/WEB-UI-DESIGN-NOTES.md)。

---

## 收錄

bot 加入群組後，透過 LINE webhook 接收群組內的訊息並寫入資料庫。職責只有三件事：**收 webhook → 落地資料庫 → 每日清除逾期資料**。

支援四種部署角色（`Deployment:Mode`：`AllInOne`／`Edge`／`Core`／`Viewer`），因應收 webhook 的
主機未必碰得到資料庫的情況；預設 `AllInOne` 就是下面這張圖。四種角色**功能完全對等**
（訊息收送、媒體下載、頭貼快取皆已用真實雙行程端到端驗證過，差別只在資料流經過幾台機器），
媒體下載與頭貼快取獨立於模式、只看 `Line:OutboundHere` 這台主機要不要對外連 LINE，
詳見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md)。

### 架構（`AllInOne` 模式）

```
LINE Platform ──POST──▶ LineWebhookController
                            │ 1. 驗證 X-Line-Signature（HMAC-SHA256）
                            ▼
                     WebhookEventHandler
                            │ 過濾群組訊息、依型別分流、組成 IngestEnvelope
                            ▼
                  outbox.db（本機 SQLite，立即回 200，跟主資料庫完全獨立）
                            ▼
              OutboxForwarderService（背景排空，失敗按退避重試）
                            ▼
                DirectIngestSink（防重送：WebhookEventId 唯一索引）
                            ▼
                ┌── GroupMessages / MessageContents(Pending) ──┐
                │                                              │
                ▼ 入列                                          │ 入列
     ContentDownloadQueue（Channel）              ProfileRefreshQueue（Channel）
                ▼                                              ▼
     ContentDownloadService（背景下載原檔）          ProfileRefreshService
       影片/語音先等 LINE 轉檔，失敗重試，              （背景快取群組名稱與成員顯示名稱，
        完成後寫回 MessageContents）                    7 天 TTL，呼叫 LINE profile API）

RetentionCleanupService（每日固定時間讀取檢視端設定頁存的保留天數，分批刪除逾期訊息，內容檔案靠 CASCADE 一併刪除）
```

### 訊息型別處理

| 型別 | Text 欄位 | 內容下載 |
|---|---|---|
| 文字 | 訊息內文 | — |
| 貼圖 | `(貼圖)`（fallback 顯示用） | 原檔，跟圖片走同一條管線存進 `MessageContents`（`StickerId` 保留供下載時組網址，檢視端不直連 CDN） |
| 圖片 | null | 原圖（非縮圖），背景下載 |
| 影片 | null | 原檔，先輪詢 LINE transcoding 完成才下載 |
| 語音 | null | 原檔，同影片先等 transcoding 完成才下載 |
| 檔案 | null（檔名存於內容表） | 原檔，背景下載 |
| 其他（位置等） | 略過不存 | — |

### 設定

| 設定鍵 | 說明 |
|---|---|
| `Deployment:Mode` | `AllInOne`（預設）／`Edge`／`Core`／`Viewer`（舊名 `Full`／`Line`／`Db` 相容），見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md) |
| `Database:Provider` | `Sqlite`／`SqlServer`，選填。未設定時依 `ConnectionStrings:SqlServer` 有沒有值推導，顯式設定永遠優先，見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md) |
| `Database:SqliteFallback` | `bool`，預設 `true`，僅 `Deployment:Mode=AllInOne` 有效。有效 provider 為 SqlServer 時，啟動時探測連線／schema 失敗就改用本機 SQLite 撐起服務；設 `false` 改成寧可啟動失敗 |
| `ConnectionStrings:SqlServer` / `Sqlite` | 連線字串，`Sqlite` 預設 `Data Source=Db/messages.db`（相對路徑以 ContentRootPath 為基準，第一次啟動自動建立目錄） |
| `ConnectionStrings:Outbox` | 本機 outbox 的 SQLite 檔，預設 `Data Source=Db/outbox.db` |
| `Database:AutoMigrate` | 啟動時是否自動跑 `Database.Migrate()`，預設 `true` |
| `Line:ChannelSecret` | 簽章驗證用。**勿進版控**，開發用 user-secrets、正式用站台目錄下不進版控的 `appsettings.Production.json` |
| `Line:ChannelAccessToken` | 內容下載與 profile API 用，同上 |
| `Line:OutboundHere` | `bool?`，這台要不要對外呼叫 LINE API（未設定時依模式推導），見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md) |
| `Viewer:Enabled` | `bool?`，這台要不要開檢視端（未設定時依模式推導），三台拓撲用 |
| `Viewer:AllowedClientIps` | 檢視端頁面／API 的 IP 白名單，空白名單視為全拒 |
| `Ingest:BaseUrl` / `Ingest:ApiKey` | `Edge`／`Core` 拆機用，`AllInOne` 模式不需要 |
| `Outbox:PollIntervalSeconds` / `BatchSize` | outbox 排空節奏，預設 5／50 |
| `Outbox:BaseRetryDelaySeconds` / `MaxRetryDelaySeconds` | 指數退避（第 N 次失敗延遲 Base×2^(N-1)，封頂 Max），預設 5／300。暫時性失敗永遠重試、沒有死信門檻——只有 `PermanentIngestException`（如 ingest API 判定 payload 格式不合）第一次遇到就直接死信 |
| `Retention:CleanupTimeOfDay` | 每日清除時間（本地時間，預設 03:00:00）。**保留天數本身不在這裡**——已搬進檢視端設定頁（`ViewerSettings.RetentionDays`，預設 1095 天＝3 年），`RetentionCleanupService` 每次執行時讀 DB |
| `ContentDownload:MaxRetries` | 下載重試次數（預設 3） |
| `ContentDownload:RetryDelayMilliseconds` | 重試間隔基數，逐次遞增（預設 2000） |
| `ContentDownload:TranscodingPollSeconds` / `TranscodingMaxPolls` | 影片/語音轉檔輪詢間隔與次數上限（預設 5 秒 × 24 次） |
| `ContentDownload:MaxConcurrency` | 並行下載 worker 數（預設 3）——一支等轉檔的影片不會卡住排在後面的圖片/檔案 |
| `ContentDownload:FailedRetryWindowDays` / `MaxFailedRetries` | Failed 內容只在訊息到達後這麼多天內（預設 7）、且累計失敗次數未達上限（預設 10）才會被重新撿回，避免 LINE 內容過期後每次重啟都無限重跑 |
| `ContentDownload:RequeueIntervalMinutes` | 週期性重掃待下載內容的間隔（預設 15 分鐘，0 表示只在啟動時掃一次，上限 24 天）。撿回其他主機或回填服務補出的 Pending、仍可重試的 Failed，以及**租約已逾期**的 Downloading。重複入列由認領檢查擋掉，不會重複下載 |
| `ContentDownload:ClaimLeaseMinutes` | 下載認領的租約分鐘數（預設 60）。認領時寫入 `MessageContents.ClaimedAt`，只有 `ClaimedAt` 為空或早於租約期限的 Downloading 才會被回收改回 Pending——別台主機正在下載中的不會被誤收，卡住的下載也不必等重啟 |
| `ContentDownload:MaxPendingIdsPerScan` | 單輪掃描最多撈多少待處理內容（預設 5000，Id 小的先處理），其餘留給下一輪。沒有上限時大量積壓會整包進記憶體，SQL Server 端還會撞上 2100 個查詢參數的硬限制 |
| `Database:SqliteBusyTimeoutMs` | SQLite 寫鎖被別的行程佔用時最多等這麼久（預設 30000 毫秒）。WAL 只讓讀寫不互相阻塞，寫／寫仍是全庫互斥 |
| `ProfileCache:RefreshAfter` | 群組/成員名稱快取的過期時間（預設 7 天） |
| `ProfileCache:FailureRetryAfter` | LINE profile API 失敗後的程序內冷卻時間（預設 10 分鐘），避免暫時性故障被每則訊息放大成持續性的無效呼叫 |
| `Encryption:Enabled` / `Key` / `SearchWindowDays` | 應用層欄位加密開關與金鑰，見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md)。所有直連資料庫的主機 `Key` 必須完全一致 |

開發環境設定機密（在 `MessageService.Web/` 目錄下）：

```bash
dotnet user-secrets init
dotnet user-secrets set "Line:ChannelSecret" "<你的 channel secret>"
dotnet user-secrets set "Line:ChannelAccessToken" "<你的 access token>"
```

### 本機串接 LINE 測試

完整的逐步操作（建立 Bot、取得金鑰、Webhook 設定、測試驗收、疑難排解）見 **[docs/LINE-BOT-SETUP.md](docs/LINE-BOT-SETUP.md)**。快速版：

1. LINE Developers Console 建立 Messaging API channel，**啟用 Allow bot to join group chats**（不開就完全收不到群組訊息）
2. 取得 Channel Secret（Basic settings 分頁）與 Channel Access Token（Messaging API 分頁），用 user-secrets 寫入
3. `dotnet run --project MessageService.Web --urls http://localhost:5072` 啟動（刻意只掛 HTTP；`Http:UseHttpsRedirection` 預設 false，別開它，否則 LINE 的 webhook 會被轉址擋掉）
4. 用 dev tunnel 或 ngrok 將該 port 開成 HTTPS URL
5. 在 LINE console 設定 Webhook URL 為 `https://<tunnel>/api/line/webhook`、啟用 Use webhook，按 Verify 確認
6. 將 bot 拉進測試群組，發文字/貼圖/圖片/影片/語音/檔案各一，確認 SQLite 落地與 `DownloadStatus` 流轉

---

## 檢視

唯讀網頁，值班/維運人員用來瀏覽 LINE 群組對話。除了讀取訊息，**設定會寫入自己的設定資料**（遮蔽規則、名稱顯示模式、別名），這是收錄以外唯一會寫資料庫的地方。

### 頁面與 API

**對話頁（`/`）**：原生 JS 模擬 LINE 的雙欄版面（Bootstrap 只保留 modal/toast/表單控件），所有資料透過 API 取得（不走 MVC Model 傳遞）。

- **左側欄**：群組列表（48px 圓角方形頭貼、名稱、最後訊息預覽、時間、未讀數 badge，依最後活動倒序），前端即時搜尋過濾，底部為設定入口（開啟設定 modal）。側欄與聊天面板的所有區塊共用同一份 `--gutter`/`--radius-*` token（chat.css），群組項目與設定入口是內縮圓角卡而非通版直角色塊
- **側欄寬度與收合（桌面版）**：分隔線可拖曳調整寬度（200–480px，Pointer Events + `setPointerCapture`；拖到 <140px 吸附成窄欄、窄欄拖出 >180px 回展開，兩門檻錯開防臨界抖動；雙擊重設 320px；分隔線可 Tab 聚焦，←→/Home/End 鍵盤調整）；標題列「‹」鈕兩段式收合：全寬 → 72px 窄欄（只剩頭貼，原生 title 提示群組名、未讀 badge 疊頭貼右上、點頭貼即切換群組）→ 完全隱藏（聊天標頭出現「☰」展開鈕）。寬度與收合狀態各自記在 localStorage（`chat-sidebar-width`/`chat-sidebar-state`）；手機版（≤768px）一律停用（單欄全螢幕切換，桌面存的狀態不生效）
- **側欄未讀數**：每群組的「最後已讀訊息 Id」記在 localStorage（`chat-read-state`，每台裝置各自算，不進 DB），輪詢 `POST /api/groups/list` 用 body 帶上基準由後端計數（上限 99+）。開著的群組視為已讀（切入群組、新訊息接進畫面時都會推進基準）；本裝置第一次看到的群組直接以最後一則為基準（不會初次開啟整排 99+）；已消失的群組基準自動清掉
- **聊天面板**：標頭白底＋細分隔線（`--line-header-bg`/`--line-header-border`，仿 LINE 桌面版，與藍色訊息區明確分界；群組頭貼＋名稱＋成員數＋🔍搜尋＋「Aa」字級下拉）；訊息泡泡首顆帶指向頭貼的小尾巴（左上角、跟著字級用 em 縮放，避免大字級時圓角比尾巴大造成脫節）、時間戳貼泡泡外側；同一人連續訊息間距收緊、換人／換日的首則才拉開（LINE 的節奏）；對話寬度桌面版預設佔版面 2/3、手機版 75%，設定可勾選「全版面」改成滿版（影片／語音另有 30rem 絕對上限，加寬只給文字）；底部仿 LINE 輸入列但唯讀化（圖示灰化不可點、中央膠囊顯示同步狀態）
- **頭貼**：`Original` 模式顯示後端快取的頭貼，走自家 API（`api/groups/.../avatar`），前端不直連 LINE CDN（載入失敗仍 fallback 代號圖示）；其他模式一律顯示伺服器指派的動植物代號圖示（emoji 渲染，前端 `ICON_EMOJI` 對照表需與後端 `AvatarIconCatalog` 的 IconKey 同步維護）
- **字級**：設定「字體大小」數值輸入（px）＝聊天頁「中」檔泡泡文字的實際大小，小／大依比例（.87×／1.13×）跟著調整；聊天頁全部文字（不含頭貼/圖示）與設定 modal 本身的文字都吃同一份 `--font-base-px`（localStorage key `chat-font-base-px`，設在 `document.documentElement` 上、透過 inline style 覆寫、不寫死在樣式表裡，才不會被 CSS cascade 蓋掉）。設定 modal 跟聊天頁是同一個頁面，調字級時背後的聊天畫面會即時跟著變
- 預設載入 3 天內對話，「載入更早 7 天」膠囊以最舊訊息 Id 當游標往前翻頁，沒有更早歷史時自動 disable。兩個「按了會沒反應」的空窗情況都有處理：畫面上一則訊息都沒有（沒有游標可用）時改成放大天數視窗重繪；群組沉寂比一個視窗還久時由 API 把視窗錨定到下一則更早訊息，保證每次點擊都會前進。膠囊本身（可按的「載入更早」）常駐顯示；沒有更早歷史時的「沒有更早的訊息」是單純的狀態告知，只在捲到最頂部附近才顯示，捲到畫面中間看到會很突兀
- 「回到最新」浮動按鈕：使用者往上捲動時自動退出跟隨模式並顯示未讀數，點擊或捲回底部即恢復跟隨並自動捲到新訊息
- **訊息搜尋**：標頭 🔍 展開搜尋列（本群組／全部群組切換），比對訊息內容與發言者名稱，結果以 `<mark>` 高亮；點結果用 `aroundId` 跳轉到該訊息的上下文並閃爍定位，同時把視窗內符合的文字也標出來。跳轉後進入「歷史檢視」，此時 3 秒訊息輪詢**只更新 Pending 內容狀態、不把新訊息接到視窗尾端**（避免時間軸斷層），畫面改顯示「回到最新」常駐按鈕，點擊會呼叫既有的群組選取流程整個重置回即時畫面
- **歷史檢視的「載入更新的訊息」**：底部膠囊（捲到底也會自動觸發）以 `afterId` 往後接續，讓使用者能從搜尋跳轉的位置一路往下讀完整脈絡，不必靠「回到最新」跳離。`afterId` 的回應不帶 `latestId`，所以「是否追上最新」是拿側欄的 `lastMessageId` 比對；追上時自動退出歷史檢視，並把即時輪詢的基準交棒給目前視窗最新一則（不交棒會讓輪詢從舊基準重抓、畫面出現重複訊息）
- **視窗截斷提示**：單次回應超過 `MessageWindowLimit`（500）時 API 會回 `truncated`，清單頂端插入「此區間訊息過多，僅顯示最近 500 則」。這跟 `hasMore`（還有更早的訊息）語意不同，兩者可同時出現
- 每 3 秒輪詢新訊息與 Pending 內容的下載狀態，每 10 秒輪詢側欄群組列表（新群組/預覽/排序，歷史檢視期間依然照跑）；分頁隱藏（`document.hidden`）時皆暫停輪詢
- 新訊息進場有淡入＋位移動效（`prefers-reduced-motion` 使用者會停用）
- 圖片／影片／語音／檔案依 `DownloadStatus` 顯示 spinner／播放器／下載連結／失敗訊息；圖片點擊開全螢幕燈箱（白框＋右上角 ✕，點空白處或 Esc 皆可關閉），預設縮到剛好符合視窗（不放大本來就比較小的圖），再點一次切換原始尺寸並可捲動查看局部
- 貼圖走自家 API 顯示真實圖片，浮貼在背景上不裝進白色泡泡；下載中顯示既有的「內容抓取中…」狀態並由輪詢自動替換；下載失敗、或訊息本身沒有 `StickerId`（改版前收到的貼圖沒有這個欄位，LINE 不提供舊訊息回溯查詢）一律 fallback 回「(貼圖)」文字
- 文字訊息中的網址會轉成可點連結（`target="_blank"` + `rel="noopener noreferrer"`）；所有內容（含搜尋高亮）一律用 DOM 節點組裝（`textContent`／`createElement`），不用 `innerHTML`，避免訊息內容造成 XSS
- **手機版（<768px）**：群組列表與聊天面板全螢幕切換，標頭出現「‹」返回鈕，仿 LINE 手機版導覽

**設定**：聊天頁裡的寬版 modal（`modal-xl`，手機自動轉全螢幕），不再是獨立頁面——`/Home/Settings` 路由已移除。上方四個頁籤：介面顯示（字體大小 px 數值設定，見上方「字級」；「對話內容使用全版面寬度」勾選框，預設不勾＝桌面 2/3）、隱私與匿名（名稱顯示四模式，含完全匿名動植物代號；別名編輯器可依群組篩選成員；台灣個資自動遮蔽四開關——身分證/手機/市話預設開啟、健保卡預設關閉（12 碼數字跟宅配貨運單號撞格式）；訊息保留天數，異動有確認對話框防手滑）、關鍵字遮蔽規則（新增/刪除，預設等長 `*` 或自訂替換字串，全部群組或指定群組）、主機狀態（各部署主機的存活燈號／最後回報時間／outbox 積壓／加密金鑰指紋，讀 `HostHeartbeats` 表，指紋不一致時顯示警告，見下方資料表說明）。多數變更即存（字體大小與對話寬度為 localStorage、不進 DB；PII 開關切換即 PUT；其餘 PUT 後顯示 toast），保留天數則需按「儲存」並過確認對話框才會寫入。資料只在**第一次**打開 modal 時才載入（`shown.bs.modal` 才打 API，不會讓聊天頁一開就多打一輪設定用的請求），成功寫入任何變更後關閉 modal 會自動重新整理目前的訊息視窗與側欄，不用手動重新整理頁面。

**API**（都在 `MessageService.Web/Controllers/Api/`）：

| 端點 | 用途 |
|---|---|
| `POST /api/groups/list` | 群組清單（僅列出有訊息的群組，名稱取自快取，無快取則顯示 GroupId；含最後訊息預覽〔已套遮蔽〕、最後訊息時間、成員數、最後訊息 Id，依最後活動倒序）。body `{"read":{"群組Id":最後已讀Id}}`（可省略）帶各群組的已讀基準，回應附每群組未讀數（SQL 端計數並截斷在 100，前端顯示 99+；沒帶基準的群組未讀數為 0）。**基準走 POST body 而不是查詢字串**：長度隨群組數線性成長且沒有上界，`maxQueryString` 只是把門檻推遠、擋不住（換 nginx 等前置代理更是另一套限制）。缺 body 或 `read` 省略＝沒帶基準回 200；`read` 的值型別不對由模型繫結回 400 |
| `GET /api/groups` | 同上但不帶已讀基準（未讀數一律 0） |
| `GET /api/groups/{groupId}/messages?days=` / `?beforeId=&days=` / `?afterId=` / `?aroundId=&days=` | 初載 / 往前加載 / 輪詢新訊息 / 以指定訊息為錨點開前後視窗（搜尋結果跳轉用），回應已套用遮蔽；單次視窗最多回 500 筆並附 `Truncated` 旗標，避免單一沉寂期特長的群組一次撈出過量資料 |
| `GET /api/messages/{id}/content` | 內容串流，支援 HTTP Range（見下方實作說明） |
| `GET /api/messages/statuses?ids=` | 查詢多筆內容目前的 `DownloadStatus` |
| `GET /api/messages/search?q=&groupId=` | 訊息搜尋，比對文字訊息內容與解析後的發言者名稱，`groupId` 省略＝搜尋全部群組，文字/名稱各自上限 50 筆獨立配額、新→舊排序（見下方「訊息搜尋」） |
| `GET/PUT /api/settings/display` | 名稱顯示模式 |
| `GET/POST/PUT/DELETE /api/settings/keywords[/{id}]` | 關鍵字遮蔽規則 CRUD |
| `GET/PUT/DELETE /api/settings/aliases[/{userId}]` | 使用者別名對照 |
| `GET/PUT /api/settings/retention` | 訊息保留天數（1–3650，供 `RetentionCleanupService` 讀取） |
| `GET/PUT /api/settings/pii-masking` | 台灣個資自動遮蔽四開關（身分證/手機/市話/健保卡） |
| `GET /api/users?groupId=` | 別名編輯器用的成員清單（可選群組篩選）。**`Anonymous` 顯示模式下改回傳動植物代號而非真實姓名**（唯讀查代號，不觸發新指派）；其他三種模式仍回傳真實姓名，這是刻意保留的例外——別名編輯器本來就需要看到真實身分才能設定別名，只有完全匿名模式下連編輯器本身都不該外流真名 |

**內容串流的技術重點**：
- 影片/語音的 Range 請求（拖拉進度）不能靠 ADO.NET 的 blob stream 做 `Seek`——SQL Server 與 SQLite 的 blob stream 都是 forward-only，不支援真正的隨機存取。因此 Range 切片直接在 SQL 端用 `SUBSTRING`/`substr` 完成（未加密時），每個 Range 請求只讀取實際要傳送的位元組，不會把整個檔案載進應用程式記憶體；沒有帶 Range 的請求（多數圖片/檔案下載）直接單次 SELECT，不繞經 Range 切片邏輯。
- **XSS 白名單**：只有白名單內的圖片/影片/音訊 MIME type 會用原始 `Content-Type` 內嵌顯示，其餘一律強制 `Content-Type: application/octet-stream` 並加 `X-Content-Type-Options: nosniff`，防止惡意檔名/內容偽裝成 `text/html` 造成儲存型 XSS。
- **檔名編碼**：`Content-Disposition` 同時輸出 ASCII fallback（`filename=`）與 RFC 5987 UTF-8 版本（`filename*=UTF-8''...`），中文檔名在新舊瀏覽器都能正確下載存檔。
- **快取**：`ETag` 直接用內容的 Id 推算（`"mc-{id}"`，不必為了算雜湊把整包內容讀出來）——內容一旦 `Completed` 就不會再被改寫，Id 相同即位元組相同，所以這是有效的 strong ETag，也才敢搭配 `Cache-Control: private, max-age=31536000, immutable`。命中 `If-None-Match` 回 304，同一則訊息的媒體重複載入（如捲動回上方）不用整包重傳。
- 應用層加密開啟時（見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md)），blob 以 1MB 為單位分塊加密，Range 請求換算成塊邊界後只解密涵蓋範圍內的塊，一樣不需要整檔載入記憶體。

實作見 `MessageService.Web/Services/ContentStreamService.cs`。

### 遮蔽機制

`IMaskingService.LoadRulesAsync()` 每個請求只呼叫一次，把當下的名稱顯示模式、關鍵字規則、別名對照載成一份 `IMaskingRuleSet` 快照，套用到該次回應的每則訊息時全是同步運算，避免每則訊息各打一次 DB。

- **關鍵字遮蔽**：不分大小寫的純字串比對（不用 regex），可設定全部群組或指定群組套用；預設遮蔽為與關鍵字等長的 `*`，也可設定自訂替換字串
- **台灣個資自動遮蔽**：跟關鍵字規則互補的第二層——不需要事先知道要輸入什麼關鍵字，只要「長得像」就遮。身分證／統一證號、手機、市話、健保卡四種格式各有獨立開關（存 `ViewerSettings`，前三種預設開啟、健保卡預設關閉——12 碼數字跟宅配貨運單號撞格式，設定頁可調），命中的字串一律套用跟名稱遮蔽同一套「首尾保留、中間 `*`」。實作在 `MaskingRuleSet.MaskText`，四組 regex 的邊界處理見下方「設計決策備忘」；搜尋端因為是拿遮蔽後的文字重新驗證，所以個資遮蔽自動也是搜尋的過濾條件，不會變成後門
- **名稱顯示四模式**：
  - `Original`：顯示原始快取的 LINE 顯示名稱，沒有快取則顯示 UserId；唯一會回傳頭貼 URL（自家相對路徑）的模式
  - `MaskMiddle`：首尾字保留、中間 `*`（1 字全遮；2 字只留首字，如「小明」→「小*」；3 字以上首尾各留一字，如「王小明」→「王*明」）
  - `CustomAlias`：依 `UserAliases` 對照表顯示别名；沒設定別名的人 fallback 為 `MaskMiddle`
  - `Anonymous`：名稱與頭貼一律替換為動植物代號（如「小熊」），由 `IAnonymousIdentityService` 依群組+使用者永久指派並存進 `AnonymousIdentities`，翻閱舊訊息時代號不會變、可分辨是否為同一人但認不出真實身分。同群組內代號唯一（`(GroupId, Label)` 唯一索引），同一圖示的第二人起加數字後綴，併發撞名時遞增後綴重試

以上四種模式（`Original` 除外）回應中一律不含 `PictureUrl`，即使前端不渲染也不外流，且 `AvatarsController` 會對非 `Original` 模式直接回 404 防止繞過遮蔽，因為頭貼與 URL 本身都是身分線索。真實的 LINE URL 不會外流到前端，只存在資料庫的 `PictureFetchedUrl` 欄位供判斷頭貼是否更新。

### 訊息搜尋

`GET /api/messages/search`（`MessagesController.Search`）比對文字訊息內容與解析後的發言者名稱，兩者符合其一即算命中；核心設計是不能讓搜尋變成遮蔽機制的後門：

- **內容比對**：SQL 端先用原文 `LIKE`（`EF.Functions.Like` 帶 `ESCAPE`，`%`/`_`/`\` 會被跳脫成字面）撈候選，於記憶體用 `MaskingRuleSet.MaskText` 套用後的文字**重新驗證**仍含關鍵字才算命中——被關鍵字規則遮掉的詞（如「密碼」）搜不到，摘要也只顯示遮蔽後的文字，不會洩漏原文。
- **名稱比對**：走當下顯示模式**解析後**的名稱（`Original` 比對原名、`MaskMiddle`/`CustomAlias` 比對遮蔽後名稱或別名、`Anonymous` 比對動植物代號），符合的成員底下所有訊息都算命中（不限訊息內容本身有沒有關鍵字）。`Anonymous` 模式下只讀 `AnonymousIdentities`、**不觸發指派**——沒被指派過代號的成員姓名比對就是找不到，指派只應該發生在使用者實際開啟訊息視窗時。
- **範圍與限制**：`groupId` 省略即搜尋全部群組；只搜文字訊息的 `Text` 欄（媒體訊息無文字可搜，檔名未經遮蔽管線、不搜）；文字命中與姓名命中**各有 50 筆獨立配額**（合併後上限 100 筆），依 `EventTimestamp` 新到舊排序，不做分頁。回應是 `MessageSearchResponseDto`（`results` ＋ `limit`），不是裸陣列。
- **`Text` 欄無索引、全表掃描，是已知限制**：前綴萬用字元的 `LIKE '%q%'` 用不到索引，而全文檢索索引在中文情境下取代不了它。SQLite FTS5 的預設 tokenizer（`unicode61`）把連續中日韓字元當成單一 token，中文查詢一律零命中；`trigram` tokenizer 可用，但只索引 3 字元序列，**查詢字串少於 3 個字就用不到索引**，而中文最常見的正是 2 字詞與單字姓氏。SQL Server 的 `CONTAINS` 是詞彙比對而非子字串比對，有同樣的問題。要覆蓋 2 字查詢只能自建 n-gram 索引表，代價是資料庫明顯膨脹。實測數據見 [docs/history/REVIEW-FEEDBACK-3-PLAN.md](docs/history/REVIEW-FEEDBACK-3-PLAN.md)。
- **應用層加密啟用時，內容搜尋走另一條路**：密文沒辦法用 `LIKE` 做子字串比對，改成只在最近 `Encryption:SearchWindowDays`（預設 14、上限 90）天內的文字訊息解密後於記憶體比對，更早的訊息內容搜不到（姓名搜尋不受影響，因為姓名是另外從 `GroupMembers` 解出來比對的）。這條路徑同樣受候選筆數上限保護——少了上限的話，「撈回來逐筆解密」會變成任何人都能觸發的記憶體／CPU 消耗管道。因為候選上限套在關鍵字比對之前，實際可搜範圍是**最新 300 則文字訊息**。回應帶 `limit`（加密未啟用時為 `null`）：`windowDays` 是恆常生效的天數視窗、`candidateCapped` 只在這次真的撞到 300 則上限時才為 `true`——分成兩個欄位是因為單一布林旗標等同「只要開加密就常態顯示」，使用者兩天就學會忽略，真的被截斷時反而沒有訊號。見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md)。
- **跳轉上下文**：搜尋結果可用 `GET /api/groups/{groupId}/messages?aroundId={messageId}&days=` 取得以該訊息為錨點、前後各 `days` 天的視窗（含該訊息本身），供前端捲動並高亮；此模式回應不含 `latestId`（跳轉後屬歷史檢視，前端會暫停新訊息輪詢，不需要輪詢基準）。

### 健康檢查端點

`GET /healthz`（存活，不碰資料庫，恆 200）與 `GET /healthz/ready`（就緒，有資料庫的模式會 ping 一次，連不上回 503；探測結果快取 5 秒，成功與失敗都快取）。兩支在**所有部署模式**都存在，回應空 body，且**排除在 `Viewer:AllowedClientIps` 白名單之外**——監控系統與負載平衡器的來源 IP 通常不在辦公室 LAN 的白名單裡。監控設定建議見 [docs/DEPLOYMENT-GUIDE.md](docs/DEPLOYMENT-GUIDE.md)。

### IP 白名單（沒有登入機制）

`IpAllowlistMiddleware` 掛在管線最前面，擋下所有請求（`/api/line`、`/api/ingest`、`/healthz` 各有自己的處理，見該中介層的掛載條件）。**空白名單視為全拒**（寧嚴勿鬆）。

```jsonc
// appsettings.json
"Viewer": {
  "AllowedClientIps": [ "127.0.0.1", "::1", "10.1.0.0/24" ]  // 支援單一 IP 與 CIDR 網段
},
"UseForwardedHeaders": false,  // 部署在反向代理（IIS/nginx）後面時才需要開啟
"ForwardedHeaders": {
  "KnownProxies": [ "10.0.0.5" ],       // 反向代理本身的 IP（單一位址）
  "KnownNetworks": [ "10.0.0.0/24" ]    // 或反向代理所在的網段（CIDR，主機位元須全為 0）
}
```

放行與拒絕的來源 IP 都會記 NLog。若部署在反向代理後面卻沒開 `UseForwardedHeaders`，中介層看到的會是代理的 IP 而非真實來源，白名單會失效——這是最容易忘記的坑。

**部署預設值**：檢視端預設部署在 **IIS in-process 託管模式**，此模式下 ASP.NET Core Module 直接跑在 IIS 工作處理序內、沒有經過 Kestrel 反向代理這一層，`Connection.RemoteIpAddress` 拿到的就是真實來源 IP，**不需要開 `UseForwardedHeaders`**（也不應該開，開了反而會信任錯誤的標頭來源）。

只有在 IIS 前面又疊了一層獨立反向代理（例如 nginx、雲端負載平衡器，或改用 out-of-process 託管模式）時才需要開 `UseForwardedHeaders`；此時**務必同時設定 `ForwardedHeaders:KnownProxies` 或 `ForwardedHeaders:KnownNetworks` 其中一項**——ASP.NET Core 預設只信任 loopback，不設定的話中介層會直接忽略上游代理送來的 `X-Forwarded-For`，等同白開關（開啟卻兩者皆空時啟動會記一則警告提醒這件事）。`KnownProxies` 填反向代理本身的單一 IP；`KnownNetworks` 填反向代理所在的 CIDR 網段（跟 `Viewer:AllowedClientIps` 一樣要求主機位元全為 0，解析失敗會直接擋啟動）。

### 資料庫存取

`MessageDbContext` 不設全域 `NoTracking`：查詢型端點（對話、群組、遮蔽規則載入）各自在查詢上加 `.AsNoTracking()`，設定的「讀取實體→改屬性→存檔」寫入流程才能正常被 change tracker 偵測到——若改回全域 `NoTracking`，`UpdateKeyword`/`UpsertAlias` 這類更新會靜默失敗（改了值但沒真的寫進 DB）。

### 設定

| 設定鍵 | 說明 |
|---|---|
| `Database:Provider` / `ConnectionStrings:*` | 與其他直連資料庫的主機指向同一顆資料庫 |
| `Viewer:AllowedClientIps` | IP 白名單，見上 |
| `UseForwardedHeaders` / `ForwardedHeaders:KnownProxies` / `KnownNetworks` | 反向代理後方時開啟並設定其中一項；IIS in-process（預設部署方式）不要開，見上 |
| `Encryption:Enabled` / `Key` / `SearchWindowDays` | **所有直連資料庫的主機必須完全一致**，否則訊息會顯示成 `ENC2:` 密文、媒體一律回 404，見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md) |
| `Heartbeat:Enabled` / `IntervalSeconds` / `OutboxBacklogAlertMinutes` | 所有部署模式都跑的存活回報，供設定頁「主機狀態」區塊顯示；`Enabled` 預設 `true`，只有測試主機會關掉；`IntervalSeconds` 預設 60，**所有主機要設成一致**（狀態燈的 Online/Delayed/Offline 門檻是以檢視端這台的設定為基準判斷，見 `SettingsController.ComputeStatus`）；`OutboxBacklogAlertMinutes`（預設 30）是 outbox 最舊未死信項目滯留幾分鐘就記一則 Error |

---

## 共用資料表（`MessageService.Data`）

**GroupMessages**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| WebhookEventId | nvarchar(450) | LINE 事件 ID，唯一索引（防 redelivery 重複寫入） |
| LineMessageId | nvarchar(max) | LINE 的 message id |
| GroupId | nvarchar(64) | 來源群組；`(GroupId, Id)` 與 `(GroupId, EventTimestamp)` 各有複合索引，支撐依群組翻頁/搜尋的主要查詢路徑 |
| UserId | nvarchar(64), null | 發言者（未加 bot 好友時可能為 null） |
| MessageType | nvarchar(20) | text / sticker / image / video / audio / file |
| Text | nvarchar(max), null | 文字內容或 `(貼圖)`（顯示時會套遮蔽規則；應用層加密開啟時此欄位以 `ENC2:` 前綴整值加密存放，見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md)） |
| StickerId | nvarchar(max), null | 貼圖識別碼（僅 sticker 型別；此欄位加入前收到的貼圖為 null，檢視端 fallback 顯示文字） |
| PackageId | nvarchar(max), null | 貼圖包識別碼（同上；渲染現已改走 `MessageContents`，`StickerId` 只在下載時用來組 CDN 網址，一併保存供未來使用） |
| EventTimestamp | datetimeoffset | LINE 事件時間 |
| ReceivedAt | datetimeoffset | 收錄端收到時間 |

**MessageContents**（1:1 對 GroupMessages，ON DELETE CASCADE）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| GroupMessageId | bigint FK | |
| FileName | nvarchar(max), null | 檔案訊息的原始檔名（加密開啟時同 Text 走 `ENC2:` 整值加密） |
| ContentType | nvarchar(max), null | 下載完成後的 MIME type |
| DownloadStatus | nvarchar(20) | Pending / Completed / Failed |
| CompletedAt | datetimeoffset, null | 下載完成時間 |
| FailedAttempts | int | 累計下載失敗次數，`ContentDownload:MaxFailedRetries` 用它判斷是否放棄重試 |
| LastAttemptAt | datetimeoffset, null | 最後一次嘗試下載的時間 |

**MessageContentBlobs**（1:1 對 MessageContents，ON DELETE CASCADE）

| 欄位 | 型別 | 說明 |
|---|---|---|
| MessageContentId | bigint PK/FK | 主鍵同時是外鍵。SQLite 上必須是 rowid 別名（`INTEGER`），`SqliteBlob` 靠 rowid 開啟 blob 做增量串流 |
| Content | varbinary(max) | 原始檔案內容；加密開啟時以 1MB 為單位分塊加密存放（保留 Range 續傳能力，見 [docs/ENCRYPTION.md](docs/ENCRYPTION.md)） |

尚未下載完成的內容**沒有這一列**（不是 `Content` 為 null）；下載失敗時
`DbContentWorkSource.FailAsync` 會把列刪掉，不留殘骸。blob 獨立一表的理由見「設計決策備忘」。

**Groups** / **GroupMembers**：收錄端背景快取的群組名稱、成員顯示名稱與頭貼（7 天 TTL，來源是 LINE 的 group summary / member profile API），檢視端用來把 GroupId/UserId 轉成人看得懂的名稱。快取失敗時 fallback 顯示原始 ID；`ProfileCache:FailureRetryAfter`（預設 10 分鐘）冷卻期內失敗不會重複呼叫 LINE API。加密開啟時群組名稱/顯示名稱/頭貼 URL 同樣走 `ENC2:` 整值加密。檢視端也會寫這張表：`Groups.LastMessageId` 指向的訊息若被保留期清除刪掉，`GroupsController.RecoverDriftedLastMessageAsync` 會即時查回目前真正的最後一則並修正這一列（見 `docs/DEPLOYMENT-GUIDE.md` 的 Viewer 帳號權限說明）。

兩張表的頭貼快取欄位相同（來源 URL 與圖檔本體都存，理由見「設計決策備忘」）：

| 欄位 | 型別 | 說明 |
|---|---|---|
| PictureUrl | nvarchar(max), null | LINE 端的來源位址，不會直接送給前端 |
| PictureContentType | nvarchar(max), null | MIME 型別（例如 image/jpeg） |
| PictureFetchedUrl | nvarchar(max), null | 下載當時的來源 URL（用於判斷 LINE 頭貼是否更新） |
| PictureUpdatedAt | datetimeoffset, null | 圖檔下載完成時間 |

圖檔本體各自存在 **GroupPictures**（`GroupId` PK/FK）與 **GroupMemberPictures**（`GroupId`+`UserId` PK/FK）
兩張子表的 `Content` 欄位，皆 ON DELETE CASCADE，加密開啟時走 `ChunkedBlobCipher` 分塊加密。
沒有頭貼時是**沒有子列**，不是 `Content` 為 null。

**ViewerSettings**（單列，Id 固定為 1）：除既有的名稱顯示模式外，新增 `RetentionDays`（保留天數，預設 1095＝3 年，`RetentionCleanupService` 每次執行讀取）與 `MaskNationalId`/`MaskMobilePhone`/`MaskLandline`（預設全開）/`MaskNhiCard`（台灣個資自動遮蔽四開關；`MaskNhiCard` 預設關閉——12 碼純數字的偵測規則跟宅配貨運單號格式相同，開啟前請先確認群組內容性質）。

**MaskKeywords** + **MaskKeywordGroups**／**UserAliases**／**AnonymousIdentities**：檢視端寫入的顯示設定（含上面的 ViewerSettings）。`AnonymousIdentities`（GroupId+UserId 複合主鍵）是 `NameDisplayMode.Anonymous` 的代號永久指派表，跟其他幾張不同的地方是使用者不直接編輯——由 `GET /api/groups/{groupId}/messages` 第一次遇到某成員時自動指派並寫入。Web 專案實際會寫入的表不只這幾張，下面兩段的 `HostHeartbeats`（所有模式都跑）與上面的 `Groups`（保留期清除後的自癒路徑）也是。

**HostHeartbeats**（`Role`+`MachineName` 複合主鍵，每台主機一列，`upsert` 不成長）：`HeartbeatService` 每 `Heartbeat:IntervalSeconds` 秒更新自己那列，記錄 `LastSeenAt`、`OutboxPending`／`OutboxOldestAgeSeconds`（只有收 webhook 的主機才有值，其餘固定 `null`）、`EncryptionKeyFingerprint`（`FieldCipher.KeyId`，未啟用加密固定 `null`）。Edge 沒有本機資料庫，靠 `POST /api/ingest/heartbeat` 端點請 Core 代寫（見 `IHeartbeatReporter` 的兩種實作）。設定頁「主機狀態」區塊純讀這張表。

這些表的欄位是各部署角色的主機間（以及未來其他消費端）的共用契約，異動需評估相容性。

## 資料庫初始化

Migrations 放在 `MessageService.Data`，**每個 provider 一套獨立的 migrations 集合**
（`Data/Migrations/Sqlite/`、`Data/Migrations/SqlServer/`）——`MessageDbContext` 有兩個空殼
衍生類別 `SqliteMessageDbContext`／`SqlServerMessageDbContext`，純粹讓 EF 的 migrations 工具
能區分兩套集合，模型建構邏輯完全共用不重複。

程式啟動時（`Database:AutoMigrate` 預設 `true`）自動呼叫 `Database.Migrate()`，兩個 provider
都適用，不需要手動跑指令：

- **既有的 SQLite 檔案**（`EnsureCreated()` 時代建立、沒有 migrations 歷史的舊 `messages.db`）：
  `LegacySqliteBaseliner` 會偵測到「有資料表但沒有 `__EFMigrationsHistory`」，一次性補齊
  幾批分屬不同時期新增、只跑過部分手動 schema 升級的欄位／表（含 `StickerId`／`PackageId`／
  `AnonymousIdentities`），再標記為已套用到 baseline，之後 `Database.Migrate()`
  接手處理更新的 migrations——**既有部署升級不需要手動刪檔重建**。
- **全新的 SQLite 檔案**：`Database.Migrate()` 直接從頭建表。
- **SQL Server**：正式環境若不想在伺服器上安裝 SDK 執行 `dotnet ef`，把 `AutoMigrate` 留
  預設值 `true` 讓應用程式自己跑；想手動控制的話：

  ```bash
  ASPNETCORE_ENVIRONMENT=Production dotnet ef database update --project MessageService.Data --context SqlServerMessageDbContext --startup-project MessageService.Web
  ```

多實例（同機多站台，或未來多 worker）同時啟動時，`Database.Migrate()` 前會取一個具名
mutex，避免兩邊同時建 `__EFMigrationsHistory` 互相打架。**這把鎖只跨行程、不跨機器**：
多台主機直連同一顆資料庫時（三台拓撲），請只讓 Core 開 `Database:AutoMigrate`、Viewer 設
`false`。同機兩站台若集區身分不同而拿不到鎖，該站台會跳過 migration 並記 Warning，
不做無鎖硬跑——詳見 [docs/DEPLOYMENT-GUIDE.md](docs/DEPLOYMENT-GUIDE.md)。

> 改了 `MessageDbContext` 的模型之後，要對**兩個 provider 都**跑
> `dotnet ef migrations add <Name> --context SqliteMessageDbContext` 與
> `--context SqlServerMessageDbContext`——`MessageDbMigrationsConsistencyTests` 會在只改了
> 一邊時紅燈提醒，不用等到真的部署到某個 provider 才發現漏了一邊。

## 設計決策備忘

- **外部圖檔一律由連得到外網的主機下載後存 DB，前端只走自家 API**：拆機拓撲下檢視端可能完全沒有對外網路；這也讓去識別化模式能真正生效，因為圖檔不由瀏覽器直接向 LINE 索取。
- **圖片/影片/語音/檔案存 DB（varbinary）而非磁碟**：檢視端只要連 DB 就能讀到；保留期清除靠 CASCADE 一次帶走，不會產生孤兒檔案。代價是 DB 容量成長快，若量大屆時再評估 FILESTREAM 或磁碟存放（內容獨立一表已為搬遷留好最小改動面）
- **blob 各自獨立一表（`MessageContentBlobs`／`GroupPictures`／`GroupMemberPictures`），不掛在父實體上**：父表預設就是輕的，任何查詢忘記投影都不會把幾百 MB 的檔案拖進記憶體；要 blob 的路徑必須明確查子表。查詢與寫入規則見 [CLAUDE.md](CLAUDE.md) 的「資料層規則」，取捨過程見 [docs/history/2026-08-16_REVIEW-FEEDBACK-6-PLAN.md](docs/history/2026-08-16_REVIEW-FEEDBACK-6-PLAN.md)
- **webhook 除了「outbox 寫不進去」以外一律回 200**（簽章合法後）：回非 2xx 會讓 LINE 重送並可能判定 webhook 失效，所以 JSON 解析失敗、個別事件處理失敗都只記 log 並回 200（畸形 payload 重送也不會變好）。唯一的例外是寫本機 outbox 本身失敗（磁碟滿、檔案鎖住、DB 損毀）——那是唯一會真的把訊息弄丟的情況，改回 500 讓 LINE 的 redelivery 接手，重送造成的重複由 `WebhookEventId` 唯一索引擋掉。**前提是 LINE Developers Console 要開啟 webhook redelivery**（預設關閉，見 [docs/LINE-BOT-SETUP.md](docs/LINE-BOT-SETUP.md)）；沒開的話回 500 等於直接放棄那則事件
- **webhook 收進來後只寫本機 outbox，不直接碰資料庫**：落地（含防重送）延後到背景排空時才做，webhook 回應時間因此跟資料庫是否可用完全脫鉤，短暫斷線不會掉訊息；這也是收錄端支援網段分離部署（`Deployment:Mode`）的基礎，詳見 [docs/DEPLOYMENT-MODES.md](docs/DEPLOYMENT-MODES.md)
- **下載走背景佇列**：影片/語音要等 LINE 轉檔、檔案可達數百 MB，不能在 webhook 請求內同步處理；服務重啟會自動撈回殘留的 Pending 接續下載
- **群組/成員名稱背景快取**：同樣不在 webhook 請求內同步呼叫 LINE profile API，避免拖慢 webhook 回應
- **SQLite 的 DateTimeOffset 限制**：SQLite 只支援相等比較，`<`/`>` 無法轉譯。`MessageDbContext` 在 SQLite 環境對需要範圍比較的 `DateTimeOffset` 欄位（`EventTimestamp`、`Groups`/`GroupMembers` 的 `UpdatedAt`）套 `DateTimeOffsetToBinaryConverter`；SQL Server 維持原生 `datetimeoffset`（保持型別對其他工具可讀）
- **BackgroundService 例外一律就地捕捉**：.NET 6+ 預設未捕捉例外會停掉整個 host，清除或下載失敗只能記 log 等下輪，不能讓服務跟著死
- **檢視端沒有登入機制**：IP 白名單是最低防護，空白名單視為全拒
- **檢視端 DbContext 不設全域 NoTracking**：設定需要寫入，只在真正唯讀的查詢路徑個別加 `.AsNoTracking()`（見上方「資料庫存取」）
- **`ViewerSettings.Id` 是固定值而非資料庫產生**（`ValueGeneratedNever`）：這是單列設定，Id 恆為 1。留成 SQL Server identity 的話，程式碼補建這列時帶著 Id=1 會撞上 `IDENTITY_INSERT` 關閉而失敗
- **往前翻頁一定要能前進**：純粹「以游標時間往前 N 天」開窗，遇到比視窗還長的沉寂期會永遠回空、游標不動，按鈕看起來可按卻沒反應。API 因此會在視窗落空時把視窗錨定到下一則更早訊息
- **outbox 暫時性失敗永遠重試，不設死信門檻**：webhook 落地失敗如果重試幾次就放棄，等於默默掉訊息且不易察覺；改成指數退避（`BaseRetryDelaySeconds`×2^(N-1)，封頂 `MaxRetryDelaySeconds`）無限重試，只有 `PermanentIngestException`（payload 本身格式不合、重試也不會成功的情況）才第一次遇到就死信。`OutboxForwarderService` 每小時記一次目前死信筆數，供人工排查
- **加密的 EF 模型快取鍵含 `EncryptionEnabled`**：EF Core 預設的 `IModelCacheKeyFactory` 只用 DbContext 的 CLR 型別當快取鍵，不看建構子帶進來的 `FieldCipher` 狀態，會讓同一支程式裡第一個建立的 `MessageDbContext` 實例決定「加密轉換器要不要套用」給後續所有實例。`MessageDbContext` 自訂了 `IModelCacheKeyFactory`，把 `EncryptionEnabled` 一併納入快取鍵
- **PII 正規表示式用環視斷言取代 `\b`**：四組個資 regex 用 `(?<!\d)`/`(?<![A-Za-z0-9])`/`(?!\d)`，不用 `\b`——.NET regex 的 `\b` 以 `\w` 邊界判斷，中日韓文字本身算 `\w`，會導致「身分證字號A123456789」這種中文緊貼英數字元的情況比對失敗
- **內容端點只信任白名單 MIME type 內嵌顯示**：只有白名單內的圖片/影片/音訊型別才用原始 `Content-Type` 顯示，其餘一律 `application/octet-stream` + `nosniff`，防止偽裝檔案觸發儲存型 XSS

## 日誌（NLog）

NLog 輸出到 Console 與**執行檔目錄下的 `logs/messageservice-{日期}.log`**（每日一檔，保留 30 天）。`Microsoft.*` 的雜訊只留 Warning 以上。日誌路徑用 NLog 的 `${basedir}` 變數錨定在執行檔目錄，不是行程當下的工作目錄——用 IIS/Windows 服務等非互動方式啟動時，工作目錄未必等於執行檔所在目錄，寫死相對路徑會讓 log 檔案憑空跑到別的地方（甚至因為沒有寫入權限而整個 NLog 靜默失敗）。

- **收錄端**：收到並存檔的每則訊息（型別/群組/是否排入下載）、內容下載完成（大小/MIME）、下載重試與最終失敗、影片/語音轉檔失敗、重掃補跑與租約逾期回收的數量、保留期清除筆數與下次排程時間、貼圖內容回填筆數、簽章驗證拒絕
  - 保留期清除若印出 `Deleted N orphaned MessageContentBlob(s)` 的 Warning，代表有 blob 子列
    失去父列——正常情況（兩層 FK cascade 有作用）這裡永遠是 0，出現非 0 要查 cascade 是否失效
- **檢視端**：IP 白名單放行/拒絕的來源

## 測試

```bash
dotnet test
```

只有一個測試專案 `MessageService.Web.Tests`，依收錄／檢視兩塊職責分兩段列：

- **收錄相關**：webhook 事件解析（五種型別分流含 audio、過濾規則）、outbox 落地（`DirectIngestSink` 的防重送——`WebhookEventId` 唯一索引、撞鍵與暫時性儲存失敗以回查分辨〔前者當重複成功、後者拋回 outbox 重試不掉訊息〕、change tracker 不污染同批後續、各型別存檔行為、重複情境也正確回傳既有 ContentId、側欄 Groups.LastMessageId／LastMessageAt 追蹤）、outbox 批次排空（到期判斷、批次上限、指數退避與封頂、批次中途暫時性失敗整批重試、僅 `PermanentIngestException` 那一筆立即死信其餘照常、成功後依 IngestSideEffects 決定要不要入列本機佇列、每小時死信計數記錄）、outbox schema 升級（既有 outbox.db 補欄位不動既有資料、啟用 WAL、新舊 schema 皆冪等）、部署角色（能力推導單元測試＋真實 host 整合驗證：四種角色的路由閘門、Edge／Core 啟動驗證缺漏擋下、OutboundHere 與 ChannelAccessToken 的啟動驗證、ingest API 認證〔缺金鑰 404／錯金鑰 401／IP 不在白名單 403／正確金鑰經真實 DirectIngestSink 寫入並確認去重〕、content-work 與 profiles 端點的真實 HTTP 生命週期、批次端點）、`HttpIngestSink`（狀態碼分流：2xx 成功並解析回應帶出 ContentId、400 永久失敗、其餘與連線層錯誤皆可重試；批次端點 404 時自動退回逐筆模式）、`IContentWorkSource`／`IProfileStore` 兩套實作、Null 佇列（入列後 ReadAllAsync 永不產出）、`IngestSideEffects`（依 ContentId 有無決定要不要入列、Null 佇列搭配無錯誤）、**兩套 IIngestSink 落地路徑的等價性測試**（同一批 envelope 分別走 DirectIngestSink 與 HttpIngestSink→真實 Core 模式 host，斷言產生的 GroupMessages／MessageContents 完全相同，含重複送出時兩邊都回傳同一個 ContentId、批次落地路徑也逐欄位比對）、背景下載（成功/轉檔延遲重排/轉檔輪詢上限/重試耗盡/啟動接續/**週期重掃**〔間隔到期再查、間隔 0 只查一次、某輪例外不中斷〕/**多 worker 並行下載**〔gate 機制證明同時有多筆在下載〕/**轉檔中的影片不擋住排在後面的圖片**〔並發回歸測試〕/**Failed 重試視窗**〔超過 `FailedRetryWindowDays` 或 `MaxFailedRetries` 不再撿回〕）、`LineContentClient`（狀態碼分流、失敗時不洩漏連線）、`DbContentWorkSource`（SQLite 的 `zeroblob`/`SqliteBlob` 分塊寫入路徑、寫入長度與宣稱長度不符時拒絕標成 Completed）、群組/成員名稱快取（新增/過期更新/API 失敗 fallback/失敗冷卻期內不重複呼叫）、保留期清除（DB 讀取保留天數、分批刪除、含 CASCADE 驗證、清除後重算 Groups 側欄指標）、`LegacySqliteBaseliner`（既有 SQLite 檔案橋接到 migrations baseline，含「橋接後 Migrate() 跟全新 Migrate() 逐欄位等價」的關鍵測試）、兩個 provider 的 migrations 一致性守門測試、Controller 整合測試（401/200/畸形 body 仍 200 但 outbox 寫入失敗回 500、webhook 經真實 outbox＋背景排空落地資料庫）、加密（`FieldCipher` 整值加解密/`ENC2:` 前綴帶 key id、keyId 不符時原樣回傳/`ENC1:` 舊格式與舊明文混讀、`ChunkedBlobCipher`/`ChunkedEncryptingStream` 分塊格式與邊界情況、`MessageDbContext` 不同 `FieldCipher` 狀態間的模型快取隔離）
- **檢視相關**：Groups/Messages API（分頁游標、hasMore、空視窗仍回 latestId、沉寂期長於視窗仍能翻頁、遮蔽套用、視窗上限 500 筆與 `Truncated` 旗標、`aroundId` 雙段查詢〔錨點在群組最舊訊息時另一側不借用未用完額度、兩側都不足半窗不截斷〕、側欄未讀數〔依 POST body 基準計數、上限 100、缺 body 視為 0、值型別錯誤回 400〕、側欄 Groups 指標漂移回退並順手修正）、訊息搜尋（文字/名稱各自 50 筆配額獨立生效）、內容串流（200/206/304/416/malformed Range、無 Range 直讀、XSS 白名單 MIME 判定、RFC5987 檔名編碼、ETag 產生與 `If-None-Match` 命中）、Settings API（CRUD、群組範圍替換、單列設定被刪後補建、保留天數驗證範圍、PII 遮蔽開關讀寫）、`/api/users`（Anonymous 模式回代號不觸發新指派、其他模式回真實姓名）、`MaskingService`/`MaskingRuleSet`（含名稱遮蔽邊界情況、四種台灣個資 regex 含 CJK 緊鄰英數字元的真實場景）、IP 白名單 middleware（允許/拒絕/空白名單/CIDR，檢視端與 ingest 端各自獨立設定）、加密端到端（開啟加密後整個請求生命週期的文字與 blob 加解密、Range 請求跨分塊邊界）

測試都使用 SQLite（in-memory 或暫存檔），Web 端整合測試用 `IStartupFilter` 在 TestServer 補一個固定來源 IP（TestServer 的請求沒有真正 TCP 連線，`Connection.RemoteIpAddress` 預設是 null）。
