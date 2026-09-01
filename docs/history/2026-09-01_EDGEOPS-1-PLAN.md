# EDGEOPS-1：模式後綴判別＋LINE 連通測試＋錯誤排查分頁

狀態：全案完成（含收尾體檢，體檢記錄見 LINEOUT-1 PLAN 的體檢輪修正——兩輪合併體檢）。

## 背景與定案

本輪三項需求與使用者定案：

1. **模式後綴判別**：啟動時依 `appsettings.{環境}.{模式}.json` 檔名後綴判別部署模式，五種模式（AllInOne/Edge/Core/Viewer/EdgeProxy）全支援；與既有 `Deployment:Mode` 鍵**共存**（相容規則見作業 A）。
2. **LINE 連通測試**：`/edge-admin` 加測試按鈕，直連與走 EdgeProxy 同一條路徑；「測已生效憑證」與「用表單剛填的值先測再存」**都做**。
3. **錯誤排查分頁**：`/edge-admin` 改分頁結構，新分頁顯示 (a) 記憶體環形緩衝（200 條、Warning 以上）(b) 今日 log 檔尾 (c) EdgeProxy 端錯誤（EdgeProxy 新增查詢 API 供 Edge 拉取；proxy 完全無回應時仍回頭看它機器上的 logs 目錄，此為文件註記非功能）。

## 作業總覽

| 作業 | 主題 | 委派 |
|---|---|---|
| A | 模式後綴判別與載入 | agy |
| B | LINE 連通測試（含 BaseAddress 改寫註冊測試） | agy |
| C | 環形緩衝 + EdgeProxy 錯誤 API + `/edge-admin` 分頁化 | agy |

本輪委派模型：agy（整輪一種，中途切換需註明起點且不換回）。
順序：A → C → B（B 的測試結果呈現要掛在 C 建立的分頁結構上；C 的緩衝也會接住 B 測試失敗的 log）。

## 作業 A：模式後綴判別

### A1 後綴偵測與載入（一次委派）

**行為契約**
- 啟動時掃描 ContentRoot 下的 `appsettings.{環境名}.{模式}.json`（環境名取 `IHostEnvironment.EnvironmentName`，實務上是 `Production`；模式為五個 enum 新名，比對不分大小寫，舊名 Full/Line/Db **不支援**後綴形式）。
- 恰好一份 → 以 `AddJsonFile` 附加載入（`optional:false`、允許註解與尾逗號，與既有樣板一致；`reloadOnChange` 暫定 true），模式以後綴為準。
- 該檔內若又寫了 `Deployment:Mode` 且與後綴不一致 → **啟動失敗**，錯誤訊息指名兩個來源與兩個值。一致或未寫 → 正常。
- 兩份以上後綴檔 → **啟動失敗**，訊息列出找到的檔名。
- 零份 → 完全沿用現行行為（`appsettings.{環境}.json` + `Deployment:Mode` 鍵、預設 AllInOne），升級相容。
- 優先權：後綴檔高於 `appsettings.json` 與 `appsettings.{環境}.json`；Edge 加密設定檔（`edge-settings.dat`）維持最高。環境變數與後綴檔的相對優先權標**暫定**：若能維持環境變數較高就維持，做不到則後綴檔最高並在文件註明。
- 啟動 log 記一行實際採用的來源（後綴檔名或「無後綴檔，使用 Deployment:Mode」）。
- 既有的舊名 Warning、`AllowedClientIps` 舊鍵擋門、`DeploymentValidator` 各規則行為不變。

**契約注意**
- 偵測邏輯抽成可單測的獨立類別（掃描結果 = 採用檔案/模式/錯誤原因），Program.cs 只做接線；插入點必須在讀取 `deploymentMode`（現 Program.cs:26 一帶）之前生效。
- 「什麼算一份後綴檔」：檔名完全符合上述樣式才算；`appsettings.Production.json`（無模式段）不算；未知模式名的檔（如 `appsettings.Production.Foo.json`）**不算後綴檔、也不報錯**（避免誤傷使用者自己的分檔習慣）——此判準列入測試。

**驗收**
- 單元測試涵蓋：恰一份（五種模式各一例可精簡為代表性三例）、零份沿用舊制、兩份衝突失敗、檔內 Mode 鍵不一致失敗／一致通過、未知模式名忽略、大小寫不敏感。
- 整合測試：以後綴檔啟動時 `DeploymentCapabilities` 推導結果與後綴一致。
- 既有全數測試綠。

### A2 文件同步（Claude 自做，不委派）

- `docs/DEPLOYMENT-MODES.md`、`docs/DEPLOYMENT-GUIDE.md` Part C、`deploy/README.md`：部署流程改為「將對應樣板直接放入站台目錄，不需改名」，保留舊制（改名 + `Deployment:Mode`）為相容路徑；補共存與衝突規則。

## 作業 B：LINE 連通測試

### B1 測試端點與按鈕（一次委派）

**行為契約**
- `/edge-admin` 的「連線測試」分頁（掛在作業 C 的分頁結構上）提供：
  1. **測已生效憑證**：一顆按鈕，不需輸入。
  2. **測表單值**：一個 token 輸入欄（password 型、不回顯既有值），填了就以該值作 Bearer 測試；此路徑**只測不存**，儲存仍走既有設定分頁的表單。
- 後端以既有 `LineProfile` 具名 HttpClient 發 `GET v2/bot/info`：`Line:OutboundVia=EdgeProxy` 時該 client 的 BaseAddress 已是 `{proxy}/line/api/`，同一呼叫自然驗證 Edge→EdgeProxy→LINE 整條鏈，**不新增 HttpClient 註冊**。
- 結果顯示在同一頁：成功 → bot 顯示名稱（bot/info 回應內容）與「經由：直連 / EdgeProxy({BaseUrl})」；失敗 → HTTP 狀態碼或例外類型與訊息、同樣標明經由路徑。訊息一律 HTML 逸出。
- `LineProfile` client 只在 `capabilities.OutboundHere` 為真時註冊；未註冊時測試功能顯示明確說明（「此主機未啟用 LINE outbound」）而非 500。
- 用表單值測試時，實際送出的 Authorization 必須是表單值；若 `LineAuthorizationHandler` 會覆蓋既有標頭，可調整 handler 為「已有 Authorization 就不注入」，但既有測試必須維持綠（行為契約：無標頭時注入設定值，不變）。
- 測試端點掛在 `/edge-admin` 路徑下（沿用 `EdgeAdmin:AllowedClientIps` 白名單），僅 Edge 模式存在。
- 逾時暫定 10 秒（不沿用 LineContent 的 10 分鐘）。

**驗收**
- 測試（FakeHttpMessageHandler）：成功回 200 顯示 bot 名稱；401 顯示失敗與狀態碼；表單值路徑實際帶出表單 token；OutboundHere=false 時顯示說明訊息；輸出逸出（token/錯誤訊息含 `<` 不得原樣輸出）。
- 既有全數測試綠。

### B2 BaseAddress 改寫註冊測試（順手補既有缺口，同段委派）

**行為契約**：比照 `EdgePullServiceRegistrationTests` 樣式，驗證 `Line:OutboundVia=Direct` 與 `=EdgeProxy` 兩情境下 `LineContent`/`LineSticker`/`LineProfile`/`LineProfileImage` 四個具名 client 的 BaseAddress（EdgeProxy 時前三者為 `{proxy}/line/data|sticker|api/`，`LineProfileImage` 不改寫）。純加測試，不改產品碼。

## 作業 C：錯誤可觀測性

### C1 環形緩衝 LoggerProvider（一次委派）

**行為契約**
- 新增記憶體環形緩衝 `ILoggerProvider`：保留最近 **200** 條 **Warning 以上**（含 Warning）；條目欄位：時間（本地時間顯示、內部存 UTC）、等級、logger 分類、訊息、例外摘要（型別 + Message + 最上層堆疊行；無例外則空）。滿了淘汰最舊。執行緒安全。
- 註冊條件：`Deployment:Mode` 為 **Edge 或 EdgeProxy** 時註冊（其他模式不註冊，不影響現行）。
- 提供讀取介面：回傳目前條目快照（新到舊）。
- 「一條」的判準：每次 logger 呼叫算一條，不合併重複訊息；等級門檻在 provider 端過濾，不動 nlog.config。

**驗收**：單元測試涵蓋 201 條淘汰最舊、門檻過濾（Info 不進、Warning 進）、例外摘要格式、並發寫入不炸。

### C2 EdgeProxy 錯誤查詢 API（一次委派）

**行為契約**
- EdgeProxy 模式新增 `GET /proxy-admin/errors`（路徑暫定）：回傳環形緩衝條目 JSON（欄位同 C1）＋主機名＋啟動時間。
- 保護：掛 `EdgeProxy:AllowedClientIps` IP 白名單（與 `/line` 同機制、同一份設定鍵）；EdgeProxy 不持金鑰的設計約束不變，不引入 API key。
- **必須在 `EdgeProxyForwarderMiddleware`（全域轉發）之前生效**，不得被轉發吞掉；也不得影響 `/line/*` 與 webhook 轉發的既有行為。
- 僅 EdgeProxy 模式存在；其他模式此路徑 404。

**驗收**：整合測試——EdgeProxy 模式白名單內取得 JSON、白名單外 403/擋下、非 EdgeProxy 模式 404、webhook 轉發路徑不受影響（既有 `EdgeProxyForwarderMiddlewareTests` 全綠）。

### C3 `/edge-admin` 分頁化與錯誤排查分頁（一次委派）

**行為契約**
- `/edge-admin` 改為分頁結構：**「設定」（既有表單原封搬入）、「連線測試」（作業 B 掛入點）、「錯誤排查」**。維持整頁自包含（inline CSS/JS、無外部資源）；分頁實作方式（CSS-only 或少量 inline JS）由執行端自決。既有表單行為（POST、留空維持原值、陣列哨兵、PRG、遮罩）**一字不變**，既有 `EdgeAdminEndpointsTests`/`EdgeAdminPageTests` 必須綠（測試斷言若只因 HTML 結構搬動而失敗可調整斷言，不可放寬行為斷言）。
- 「錯誤排查」分頁三區塊：
  1. **本機最近錯誤**：C1 緩衝內容，新到舊，表格呈現（時間/等級/分類/訊息/例外摘要）；空時顯示「目前沒有記錄到警告以上訊息」。
  2. **今日 log 檔尾**：讀 `logs/messageservice-{yyyy-MM-dd}.log` 最後 **100 行**（暫定）；以 `FileShare.ReadWrite` 開檔避免與 NLog 互鎖；檔案不存在顯示提示（今天尚無 log）；讀檔失敗顯示錯誤而非炸頁。
  3. **EdgeProxy 端錯誤**：僅在 `Line:OutboundVia=EdgeProxy` 且有 `OutboundProxyBaseUrl` 時顯示此區塊；後端呼叫 `{proxy}/proxy-admin/errors`（逾時暫定 5 秒）並呈現同格式表格；呼叫失敗顯示「無法連上 EdgeProxy：{原因}——請直接查看該主機 logs 目錄」。非 EdgeProxy 拓撲時區塊隱藏或顯示「未使用 EdgeProxy」。
- 所有 log 內容輸出一律 HTML 逸出（log 訊息可能含使用者輸入）。
- 拉取 EdgeProxy 錯誤所用的 HttpClient：可重用既有 `edge-proxy` 具名 client 或新增具名 client，執行端自決，但 BaseAddress 來源必須是 `Line:OutboundProxyBaseUrl`。

**驗收**：測試涵蓋——分頁存在且既有表單測試綠；緩衝有條目時表格呈現且逸出；log 檔不存在的提示；EdgeProxy 區塊三態（非 proxy 拓撲隱藏／拉取成功呈現／拉取失敗顯示原因與 logs 指引）。

### C4 文件同步（Claude 自做）

- `docs/DEPLOYMENT-GUIDE.md`（E1 排查段）與 `docs/DEPLOYMENT-MODES.md`：補 `/edge-admin` 分頁、連線測試、`/proxy-admin/errors`；註記「EdgeProxy 完全無回應時直接查看該主機 `logs/` 目錄」。

## 拆分原則自查

- 分母為零／什麼算一個：A1「什麼算一份後綴檔」已列；C1「一條」判準已列；C3 各區塊空狀態已列。
- 破壞性判準：本輪無洗值/刪列操作。啟動失敗類（多份後綴、Mode 衝突）皆為擋門非破壞，且零份時完全走舊路。
- 單向閘門：無一次性旗標。後綴機制的「繞過路徑」即零份後綴檔＝舊制，已明寫。
- 移除類：本輪無移除。`LineAuthorizationHandler` 若調整為「已有標頭不注入」屬行為放寬，既有呼叫端（無標頭）不受影響，測試釘住。

## 複檢（規劃完成後）

- 與既有設計衝突：後綴檔載入插在 Edge 加密檔之前、加密檔維持最高 → 與 ENCRYPTION 設計相容。`DeploymentValidator` 規則不動，後綴只是模式來源的前置。EdgeProxy「不持金鑰」約束在 C2 明寫維持。
- 批次間衝突：B1 與 C3 都動 `EdgeAdminPage`/`EdgeAdminEndpoints` → 已排序 C3 先建分頁、B1 後掛入；A 與 B/C 無共檔。
- 細節：C3 讀 log 檔的換日邊界（跨午夜看昨檔）不處理，使用者可看緩衝；已接受。
- 複檢完成，除上列外無新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1 | agy | 完成 | 1081 綠（基準 1045） | 驗收抓到規格自身的洞：一致性檢查若讀「整條設定鏈」，會因基底 appsettings.json 本來就宣告 `Mode: AllInOne` 而讓「後綴檔不寫模式鍵」（本機制的正常用法）誤判成不一致、擋住啟動。改成只讀後綴檔本身宣告的值（`ReadDeclaredMode`），並補 3 筆測試釘住。Claude 手改。 |
| A2 | Claude | 完成 | — | — |
| B1+B2 | agy | 完成 | 1152 綠（+27） | 兩處手改：(1) `LineConnectivityTester` 多了一個沒人用的 `IOptions` 建構子與私有 `StaticOptionsMonitor`（規格外的抽象），刪除；(2) GET 與 test-line POST 各寫一份檢視模型組法（逐字相近區塊），抽成 `BuildViewModelAsync` 單一來源。`LineAuthorizationHandler` 改成「已有 Authorization 就不覆寫」，兩個方向都有測試釘住。 |
| C1 | agy | 完成 | 1102 綠（+21） | 無落差。agy 另補了「Edge/EdgeProxy 才註冊、其他模式不註冊」的 DI 整合測試，超出規格但正確，保留。 |
| C2 | agy | 完成 | 1110 綠（+8） | 無落差。白名單掛在全域轉發之前、沿用 `EdgeProxy:AllowedClientIps`、未引入金鑰，四類情境（允許/拒絕/空清單/非 EdgeProxy 404）皆走真實管線斷言。 |
| C3 | agy | 完成 | 1125 綠（+15） | CSS-only radio 分頁、三區塊皆逸出、既有表單測試零刪除零放寬。落差一項：GET handler 把 `LogRingBuffer` 宣告成可選參數（`= null`）並用 `?.`，是規格沒要求的防禦——Edge 模式一定註冊得到，可選化只會讓緩衝解析失敗時靜默顯示空表。改成必要相依，測試仍全綠（Claude 手改）。 |
| C4 | Claude | 完成 | — | — |
| 終檢 | Explore×2 | 完成 | 1159 綠 | 見下節 |

## 併回前終檢（兩個獨立 Explore：程式碼、文件契約）

### 已修（本輪處理）

| 嚴重度 | 發現 | 處置 |
|---|---|---|
| 高 | 後綴模式只寫進 Program.cs 的區域變數，`Configure<DeploymentOptions>` 綁的是設定鏈——後綴檔不寫 `Deployment:Mode`（本機制的正常用法）時，`EdgeChannelState`、兩個心跳回報、`EdgeController` 全部讀到基底 appsettings 的舊模式。既有測試與 deploy 樣板（樣板剛好都寫了 Mode）雙重掩蓋。 | 後綴模式改用 `AddInMemoryCollection` 寫回設定鏈；新增 `DeploymentModeSuffixStartupTests` 三條啟動整合測試（含突變驗證：拿掉修正後該測試確實紅） |
| 中 | 後綴檔優先權高於環境變數與命令列，規劃要求「做不到就在文件註明」但沒寫 | DEPLOYMENT-GUIDE Part C 補明 |
| 中 | 讀 log 用 `ContentRootPath`，NLog 寫的是 `${basedir}`；非 IIS 情境會分岔成「永遠顯示今天尚無 log 檔」 | 改用 `AppContext.BaseDirectory`，路徑集中成單一方法並註明與 nlog.config 的對應 |
| 中 | 環形緩衝可被外部灌爆：EdgeProxy 在公網上，任何來源打一輪就產生數百則白名單拒絕 Warning，把真正的轉發錯誤擠出 200 筆 | `IpAllowlistMiddleware` 的分類不進緩衝（NLog 檔案仍完整記錄） |
| 中 | EdgeProxy 轉發失敗的 Warning 記了含 query string 的目標 URL，而 LINE 內容 URL 的 query 帶短期權杖——這些訊息現在會經 API 送到 Edge 頁面顯示 | log 只記不含 query 的 URL |
| 中 | 拼錯或用舊名的後綴檔（`appsettings.Production.Edg.json`／`.Line.json`）被靜默忽略，症狀是「放了 Edge 設定檔卻起成 AllInOne」 | 忽略的檔案回報給呼叫端並在啟動時印出提醒與合法後綴清單；補兩條測試 |
| 中 | 後綴生效時，設定鏈上殘留的舊模式名仍會記「你在用舊名」Warning，指向已不生效的值 | 後綴生效時不記該 Warning |
| 低 | `LineConnectivityTester` 的 `BaseAddress ??=` 會在「設定走 EdgeProxy 但 client 沒 proxy 位址」時偷偷直連 LINE，把斷掉的鏈報成通的 | EdgeProxy 拓撲下 BaseAddress 為 null 直接回報失敗並說明原因 |
| 低 | `Process.GetCurrentProcess()` 未 Dispose；`Snapshot()` 在鎖內做配置；快取標頭三行重複；`ProxyAdminErrorsResponse.MachineName` 無消費者 | 依序修正；主機名改顯示在 EdgeProxy 錯誤區塊，成為真正的消費點 |
| 中 | log 檔尾的三條整合測試依賴共用的 `logs\` 目錄，而 NLog 在測試執行期間就會寫出當天的檔案（全套並行跑時會偽紅） | 讀檔邏輯改成吃路徑，改寫成四條單元測試（含檔案被獨佔時回錯誤不炸頁），保留一條「區塊有渲染」的整合測試 |
| — | 文件：DEPLOYMENT-GUIDE 最快路徑仍教「改名成 appsettings.Production.json」、Part C 標題與定調句、`EdgeAdmin:AllowedClientIps`／機密「只能放在 appsettings.Production.json」等處與新機制矛盾；ENCRYPTION／LINE-BOT-SETUP／DEPLOYMENT-MODES／README 同型過時 | 統一用語為「站台設定檔」（`appsettings.Production.<模式>.json`）並在 Part C 定義；逐處修正。README 補後綴來源、`/proxy-admin/errors` 走 `EdgeProxy:AllowedClientIps`、`EdgeAdmin` 白名單涵蓋三分頁與測試端點；DEPLOYMENT-MODES 補「Edge 的管理面」一節（C4 原本只做了一半） |

### 查證後不採納

- **終檢說「缺 `/edge-admin/test-line` 非白名單 IP 的測試」**：實際已有 `EdgeAdmin_TestLine_DisallowedClientIp_Returns403`，誤判。
- **`ValidateModeConsistency` 接受舊名別名算一致**（後綴 `Edge`＋檔內 `"Line"` 不擋）：契約沒寫，屬實作的語意擴張，但與專案「舊名相容」的既有原則一致，保留並在此記錄。

### 遞延（未做，理由）

- **`/proxy-admin/errors` 另立 `ProxyAdmin:AllowedClientIps`**：目前與 `/line` 共用同一把鍵，代表「能用 outbound proxy 的主機都讀得到錯誤緩衝」。拆鍵是新增使用者可見設定、且會動到升級路徑，本輪不加；實務上兩者的授權對象都是 Edge 那台。
- **錯誤排查資料改成只在該分頁請求時才取**：目前 CSS-only 分頁使得 GET 一律付「拉 proxy（上限 5 秒）＋掃 log 檔」的成本；LINEOUT-1 把 test-line 擴成四目標循序後最壞約 45 秒。改法要動到分頁結構（改為 query string 分頁或前端 fetch），本輪不做。
- **log 檔尾改為從檔尾反向讀固定 byte 窗**：目前是整檔掃描只留最後 100 行，日誌長到數百 MB 時每次 GET 都全檔讀。
- **`/edge-admin` 兩個 POST 沒有 antiforgery**：既有問題（存檔端點本來就沒有），本輪新端點沿用同一形狀；要補應兩個一起補。
- **現行文件的寫作紀律違規**（ENCRYPTION／DEPLOYMENT-MODES／README 若干處出現「本輪」「原本」「改成」）：皆為本輪之前的既有債，未在本輪改動範圍內。

## 體檢交接

- 實作方：agy（gemini-3.7-flash-high）實作、Claude Opus 5 規劃/驗收/終檢手改。
- 體檢方：Claude Fable 5（使用者切換模型後執行收尾）。
- 兩輪皆直接做在 dev（無 feature 分支），體檢對象 `origin/dev..dev`。
