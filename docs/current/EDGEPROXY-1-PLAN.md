# EDGEPROXY-1：Deployment:Mode 新增 EdgeProxy（借用既有 HTTPS 入口轉發 webhook）

日期：2026-08-31。狀態：規劃定案，未實作。

## 背景與目標

Edge 主機沒有合法 HTTPS 憑證，LINE webhook 無法直接指向它。方案：在公司已有合法憑證的
對外伺服器上，於既有 IIS 站台底下加一個 application（`/MSLine`），部署**同一份**
MessageService.Web 產物、以新模式 `EdgeProxy` 運行——把 webhook 原封轉發給 Edge，
其餘一概不做。防火牆只需開通 proxy→Edge 單向。

選擇「單一專案加模式」而非獨立 proxy 專案或 IIS ARR：前者符合部署收斂輪確立的
「一份產物、模式矩陣」哲學；後者（ARR）要在共用的對外伺服器裝兩個全機層級 IIS 模組，
且驗簽保真依賴 IIS 設定不亂動——程式內轉發把 body 逐位元組保真的責任收回自己手上。

## 定案（與使用者確認）

1. **新列舉成員 `DeploymentMode.EdgeProxy`**（無舊名別名）。六個既有能力在此模式下
   天生全 false（推導式都是正列舉，零改動），所有 controller 路由自動消失。
2. **DI 註冊矩陣顯式短路（關鍵）**：EdgeProxy 不得走 `!HasDatabaseAccess`（Edge）分支——
   那會註冊用不到的 ingest client（BaseUrl 未設，解析即炸）、且 `HeartbeatService` 會因
   `Channel≠Pull` 註冊而其相依 `EdgeChannelState` 未註冊，**DI 解析失敗炸啟動**。
   EdgeProxy 模式跳過整個 ingest／outbox／heartbeat／媒體／Profile 註冊區，
   只註冊轉發所需的一支具名 HttpClient。
3. **轉發契約（硬契約）**：
   - 只處理 `POST /api/line/webhook`，轉發到 `{EdgeProxy:TargetBaseUrl}/api/line/webhook`。
   - **request body 逐位元組原封轉發**——`X-Line-Signature` 是對 raw body 的 HMAC，
     任何重新序列化／編碼都會讓 Edge 驗簽失敗。
   - 轉發標頭採**白名單制**：只轉 `Content-Type` 與 `X-Line-Signature`（`Content-Length`
     由 HttpClient 依實際 body 重算）；不轉 `Host` 等其他標頭。
   - 回應狀態碼透傳（Edge 的 200／401／500 原樣回給 LINE）。
   - Edge 不可達、逾時、連線層錯誤 → 回 **502**：proxy 零狀態、不緩衝不落地，
     讓 LINE redelivery 接手重送（Edge 端 outbox＋落地端唯一索引保證重送安全）。
   - `/healthz` 回 proxy 自己的存活（不探測 Edge）；**其餘一切路徑一律 404**（最小攻擊面）。
4. **設定**：新節 `EdgeProxy`——`TargetBaseUrl`（必填，經 `HttpBaseAddress.Create` 統一補
   結尾斜線，支援 IIS 子應用路徑）、`TimeoutSeconds`（預設 10，暫定）。
5. **啟動驗證**：`Mode=EdgeProxy` 而 `TargetBaseUrl` 空 → 擋啟動；偵測到 Line／Ingest／
   Viewer／Database 殘留設定 → 記 Warning（比照 Viewer 模式的殘留設定警告）。
6. **失敗日誌節流（硬契約）**：轉發失敗只在「轉為失敗」記一次含例外的 Warning，
   持續失敗期間最多每 10 分鐘一則摘要，恢復記一則——比照本專案既有的節流紀律。
7. **心跳缺席為已知限制**：EdgeProxy 不回報心跳，設定頁「主機狀態」看不到這台；
   監控用外部探測它的 `/healthz`。不做心跳繞道轉送（過度設計）。
8. **零設定影響（硬契約）**：既有四種模式的行為一位元組都不變——新模式只透過
   顯式的 `Mode=EdgeProxy` 分支生效，任何既有分支條件不得改寫語意。

## 現有可複用機制（執行端必須複用，不要另起爐灶）

- `HttpBaseAddress.Create`（[Services/HttpBaseAddress.cs]）：BaseAddress 統一補結尾斜線。
- `DeploymentModeConvention`＋`RequiresCapability`：新模式**不需要**動這兩個檔案——
  能力全 false 時 controller 自動移除。
- 具名 HttpClient 註冊寫法：比照 `EdgePullService.HttpClientName` 那段。
- 節流日誌形狀：比照 `HeartbeatService.LogFailure`（轉為失敗一次完整、10 分鐘摘要、恢復一則）。
- 殘留設定警告文案形狀：比照 `DeploymentValidator` 對 Viewer 模式的那段。

## 作業總覽

本輪委派模型：agy（gemini-3.7-flash-high），整輪一種；文件（作業C）Claude 親做。
測試基準：**919 綠**（dev@91c7984）。

### 作業A｜模式接線與 DI 短路
- `DeploymentMode` 加 `EdgeProxy` 成員（含 XML 註解，說明用途與「不要加舊名別名」）。
- `AddMessageServiceCore`：`Mode=EdgeProxy` 時跳過 ingest／outbox／heartbeat／媒體／Profile
  整個註冊區（含 `HeartbeatService`、具名 client "ingest"／"ingest-content"、
  `IIngestSink`、佇列——全部不註冊），只註冊轉發用具名 HttpClient
  （`BaseAddress = HttpBaseAddress.Create(TargetBaseUrl)`、`Timeout = TimeoutSeconds`）。
- 新增 `EdgeProxyOptions`（SectionName `"EdgeProxy"`、`TargetBaseUrl`、`TimeoutSeconds=10`）。
- `DeploymentValidator`：定案5 的擋啟動與殘留警告。
- 驗收（走真實 host，沿用 `DeploymentModeTests` 慣例）：
  - `Mode=EdgeProxy`＋`TargetBaseUrl` 已設 → host 啟動成功（**這就是 DI 短路的驗收**——
    不短路會在啟動時解析失敗）。
  - `Mode=EdgeProxy`＋`TargetBaseUrl` 空 → 啟動拋例外，訊息含 `EdgeProxy:TargetBaseUrl`。
  - EdgeProxy 下 `/api/ingest/*`、`/api/edge/*`、檢視端首頁 → 全部 404。
    （`/api/line/webhook` 的行為**整條歸作業B**——A 段不要對它寫任何斷言，
    否則 B 掛上 middleware 後 A 的測試會翻掉。）
  - `IHostedService` 集合中沒有 `HeartbeatService`／`OutboxForwarderService`／
    `EdgePullService`／`ContentDownloadService`。
  - 解析轉發用具名 HttpClient：`BaseAddress` 以斜線結尾且保留子應用路徑
    （`TargetBaseUrl=http://host/MSLine` → `http://host/MSLine/`）、`Timeout` 等於設定值
    ——這是作業B 要接的介面，形狀在本段釘住。
  - 既有四種模式的既有測試全綠（零設定影響的驗收）。
  - 測試總數 > 928（基準 919，本段至少 +9）。

### 作業B｜轉發 middleware 與端到端測試
- 新 middleware（暫定 `EdgeProxyForwarderMiddleware`）依定案3 實作，只在 EdgeProxy 模式掛進管線
  （`UseMessageServicePipeline` 加顯式分支；其餘模式管線一字不改）。
- 失敗日誌照定案6 節流。
- 驗收（真實 host＋`FakeHttpMessageHandler` 類手法攔轉發）：
  - POST 任意 body 到 `/api/line/webhook` → 轉發請求的 body 與原始請求**逐位元組相同**、
    標頭恰好只有白名單那兩個、目標 URL 含子應用路徑（`TargetBaseUrl=http://host/MSLine`
    時打到 `/MSLine/api/line/webhook`——上輪 404 的迴歸釘）。
  - Edge 回 200／401／500 → LINE 側收到同樣的狀態碼。
  - 轉發拋連線例外 → 回 502。
  - GET `/api/line/webhook` → 404 或 405（不轉發）；`/healthz` → 200；任意其他路徑 → 404。
  - 連續轉發失敗 20 次 → Warning 恰 1 則；時間推進 11 分鐘再失敗 → 第 2 則；恢復 → Information 1 則。
  - 測試總數 > 作業A 結束數 +10。

### 作業C｜文件與部署樣板（Claude 親做）
- `DEPLOYMENT-MODES.md`：角色表加 EdgeProxy 一列、設定鍵表、已知限制（心跳缺席）。
- `DEPLOYMENT-GUIDE.md` Part E 補「無合法憑證時借用既有 HTTPS 入口」一節
  （含 IIS application 建立、LINE Console webhook URL 改指 proxy、Verify 驗證順序）。
- README 設定表；`deploy/appsettings.Production.EdgeProxy.json` 新樣板
  （只有 Deployment:Mode 與 EdgeProxy 節，刻意無 Line／Ingest／Database——比照 Edge 樣板的註解風格）。

### 併回前終檢
- 跨段 grep：`EdgeProxyOptions` 兩個鍵的消費點、middleware 掛載點、樣板鍵名與程式一致。
- 手改輪收尾跑 NUL／BOM 掃描（本專案既有教訓）。
- 兩個獨立 Explore 審全 diff（程式碼＋文件）後才併 dev。

## 風險與已知取捨

- proxy 停機期間 webhook 依賴 LINE redelivery 補送（有限次數）——建議 LINE Console 開啟
  redelivery；長時間停機仍可能掉訊息，與 Edge 自身停機的既有風險同級。
- proxy→Edge 內網段為 HTTP 明文，由公司政策決定是否可接受（要全程加密可讓 Edge 綁
  自簽憑證，另案）。
- 設定頁看不到 proxy 心跳（定案7）。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A 模式接線與 DI 短路 | agy | 通過 | 931 綠（門檻 928）、零警告；突變測試三處皆紅 | 見下方兩條 |
| B 轉發 middleware | agy | 通過 | 947 綠（門檻 941）、零警告；子應用路徑與標頭白名單兩處突變皆紅 | 無落差；body 保真的第一次突變存活，判定為等價突變後改用決定性突變確認測試有效（見下） |

### 作業A 的兩條落差

**一、agy 的規格外改動（判定：必要且正確，保留）**

`DeploymentValidator` 把「Edge 開檢視端要擋啟動」的條件從 `!capabilities.HasDatabaseAccess`
收窄成 `mode is DeploymentMode.Edge`。規格沒要求，但不改的話 EdgeProxy（HasDatabaseAccess
同樣是 false）會撞進那個 throw，還丟出一個寫著「Deployment:Mode=Edge」的錯誤訊息——
與定案5「殘留設定只記 Warning」直接衝突。四種既有模式的判定結果完全不變
（Edge 兩個條件等價、其餘三種模式兩邊都是 false），且 `EdgeMode_ViewerExplicitlyEnabled_
ThrowsWithClearMessage` 保住了 Edge 的既有行為。突變測試（改回原條件）會讓
`EdgeProxy_WithLeftoverLineOrIngestOrViewerConfig_Warns` 變紅，確認這個收窄有測試釘住。

**二、規格的技術描述有誤，驗收因此有一條是弱斷言（已補強）**

規格寫「不做 DI 短路會**啟動就炸**」，並把「host 啟動成功」列為短路的驗收。實測突變
（停用短路）後那條測試**仍然綠**——因為：ASP.NET Core 的「建置時驗證所有服務可解析」
只在 Development 環境啟用，測試與正式部署都不是；而 `IHeartbeatReporter` 是 Scoped，
要到 `HeartbeatService` 第一次回報才解析。所以真實後果不是啟動失敗，而是
**站台正常起來、背景服務每個心跳週期噴一次解析失敗的例外**——更難發現。

補了 `EdgeProxyMode_EdgeOnlyServices_AreNotRegisteredAtAll`：直接對註冊表斷言
`IHeartbeatReporter`／`IIngestSink`／`IContentWorkSource`／`IProfileStore`／`OutboxDbContext`
在 EdgeProxy 模式下**根本沒被註冊**，並以突變驗證它會紅。教訓：**「host 起得來」是弱斷言，
證明不了服務註冊矩陣的正確性**——要驗「沒註冊」就直接查註冊表，別用啟動成功代替。

### 作業B 的突變測試紀錄

三個硬契約各做一次突變：

| 突變 | 結果 |
|---|---|
| `ForwardPath` 加開頭斜線（重現上輪的子應用路徑 404） | 2 條紅 ✓ |
| 額外標頭一併轉發（破壞白名單） | 1 條紅 ✓ |
| body 經 `UTF8.GetString`→`GetBytes`→`Trim` 往返 | **綠（存活）** |

第三個存活後先判「等價突變 vs 測試無力」：測試的 body 是合法 UTF-8 且前後無空白，
字串往返與 `Trim` 對這個輸入**不改變任何位元組**——是等價突變。改用必然改變輸出的突變
（body 尾端多一個位元組）重測即變紅，確認測試本身有效。

順帶釐清一個實務認知：真正會破壞 `X-Line-Signature` 驗簽的不是字串往返（LINE 送的
一定是合法 UTF-8），而是 **JSON 反序列化再序列化**（空白、鍵順序、跳脫方式都會變）。
現行實作從 `Request.Body` 直接 `CopyTo` 成 `byte[]`，這條路徑上沒有任何解析，安全。

## 終檢輪

### 文件終檢（獨立 Explore）

契約逐條對照：定案 1~8 全部做到，兩處與文件敘述不符、一處實作缺口，皆已修：

| 發現 | 處置 |
|---|---|
| **定案5 的實作缺口**：殘留設定警告漏了 Database（定案明列） | `DeploymentValidator` 加上 `db.HasSqlServerConnectionString` 條件並補測試。Sqlite 連線字串的解析在服務註冊階段、EdgeProxy 已提早返回拿不到，不為此改簽章——涵蓋「整份設定檔複製過來」這個主要情境即可，限制寫進註解 |
| `deploy/appsettings.Production.EdgeProxy.json` 註解宣稱 Database／Encryption 殘留會 Warning，實際不會 | 上一條補了 SQL Server 連線字串的檢查後，註解改為準確列出實際會警告的範圍 |
| `DEPLOYMENT-MODES.md`「除了 `/healthz` 之外所有路徑都回 404」與程式碼不符——`/healthz/ready` 在無資料庫的模式也回 200 | 敘述改為「除了兩支健康檢查探針之外」 |
| 「四種角色」字樣未同步成五種，共 6 處（`DEPLOYMENT-MODES` 前言與合法值、`DEPLOYMENT-GUIDE` 三處與樣板表、`deploy/README` 前言、`README` 兩處） | 全部同步。`README` 的「四種角色**功能完全對等**」改為「**前四種**角色功能完全對等」——EdgeProxy 刻意不對等，不能被這句話涵蓋 |

文件寫作紀律掃描本輪新增段落：無演變敘述違規。

### 程式碼終檢（獨立 Explore，重跑）

第一次執行因 session 額度中斷；重跑抓到 3 高 4 中，逐項查證後全數修正（954 綠）：

| 級別 | 發現 | 處置 |
|---|---|---|
| 高 C3 | EdgeProxy 的驗證跑完自己的檢查後**沒有 return**，會被其他模式的規則誤擋——整份 appsettings 從 Edge/Core 複製過來時（最常見的部署方式），EdgeBaseUrl 缺 ApiKey、OutboundHere=true 缺 token、SqlServer 缺連線字串三條都會 throw，公網那台**部署當天就起不來**，錯誤訊息還指向 EdgeProxy 沒有的功能 | 專屬區塊前移＋檢查完直接 return；補「整份複製設定」的迴歸測試 |
| 高 D1 | 客戶端中斷測試假通過：`cts.Cancel()` 在送出**之前**，請求根本沒到伺服器，斷言恆真——刪掉整個 middleware 照樣綠 | 重寫為 middleware 直測（TestServer 模擬不了真實斷線）；「HttpClient 逾時（RequestAborted 未取消）→502 不往外拋」的關鍵區分一併補上（D2）；兩者皆以突變驗證會紅 |
| 高 C1 | 公網端點無 body 上限：無任何身分驗證的路徑無條件把 body 讀進記憶體兩份（Kestrel 預設 30MB），併發無上限＝記憶體放大點 | middleware 以 `IHttpMaxRequestBodySizeFeature` 夾到 512KB（LINE webhook 是 KB 級），超過由 Kestrel 回 413 不進記憶體；補 feature 斷言測試 |
| 中 B1 | 具名 client 的 `?? "http://localhost/"` 預設值：驗證被繞過時 webhook 會**轉發給自己**，被同一個 middleware 再攔再轉，自我遞迴到耗盡連線 | 改為 `?? throw`（與既有 "ingest" client 同型） |
| 中 B2 | `TargetBaseUrl` 只驗非空：漏打 `http://` 不會在啟動時出事，而是每則 webhook 都 502、訊息靜默全掉 | 啟動驗證加 `Uri.TryCreate` + http/https scheme 檢查；補兩個格式測試 |
| 中 A1 | 節流在 Edge「時好時壞」（flapping）時完全失效：每次成功→失敗轉換都算第一次失敗，log 以請求頻率被灌爆 | `_lastFailureLogAt` 成功不清除、恢復訊息也各自 10 分鐘節流——log 量有硬上界（每 10 分鐘最多一對）；補交錯 40 次的 flapping 測試並突變驗證 |
| 中 A3 | 尾斜線：LINE Console 的 URL 多打一個 `/` 時 proxy 不轉發（Edge 直收的路由卻寬容）→「直收正常、經 proxy 整批 404」 | 比對接受尾斜線變體；入站路徑改由轉發常數派生（消掉兩份字串）；補測試 |
| 低 A2/A5/C2/D4 | `_failing` 鎖外讀、catch 設狀態碼無 HasStarted 防線、Content-Type 解析失敗仍原樣外送、GET 斷言放寬成兩個值 | 全數順手修：讀寫全進鎖、加 HasStarted 檢查、解析不出就不帶、斷言釘死 404 |

終檢確認無問題的：不是開放代理（目的地完全不受外部輸入影響）、無資訊洩漏（狀態碼透傳不帶 body、例外只進 log）、dispose 鏈正確、DI 提早返回無缺件（多餘註冊皆惰性零成本）。

### 定案8 的字面違反（已於作業A 判定保留，此處覆核）

文件終檢指出 `DeploymentValidator` 把檢視端擋啟動條件從 `!capabilities.HasDatabaseAccess`
改寫成 `mode is DeploymentMode.Edge`，字面上違反定案8「任何既有分支條件不得改寫語意」。
覆核結論維持保留：四種既有模式的判定結果完全等價（只有 Edge 的 `HasDatabaseAccess` 是
false），且不改的話 EdgeProxy 會撞進該 throw 並丟出寫著「Mode=Edge」的錯誤訊息，
與定案5 直接衝突。判定為「定案8 的例外」而非違反，理由與測試釘記於作業A 落差一。

---

# 第二階段（使用者擴充需求，2026-08-31 定案）

三項擴充：LINE outbound 可經 EdgeProxy、Edge 極簡設定頁（僅可熱生效的鍵）、
webhook 來源限制。委派 agy，逐段驗收。

## 定案（第二階段）

9. **拓撲三選（保留環境彈性）**：由兩個既有機制的組合達成，不新增「拓撲」設定——
   - 全走 proxy：LINE Console 指向 proxy ＋ `Line:OutboundVia=EdgeProxy`
   - proxy 進、Edge 出：LINE Console 指向 proxy ＋ `Line:OutboundVia=Direct`（預設）
   - 完全無 proxy：LINE Console 直指 Edge，不部署 proxy
10. **`Line:OutboundVia`（Edge 端，啟動快照、不進設定頁）**：`Direct`（預設）／`EdgeProxy`。
    `=EdgeProxy` 時需另設 `Line:OutboundProxyBaseUrl`（proxy 位址，經 `HttpBaseAddress.Create`）。
    **四支** LINE 具名 client **全有全無**一起切（Edge 出不去時全部都出不去，不做分流）。
    寫規格前查證的實際形態——分成兩類，處理方式不同：

    | client | 位址形態 | proxy 化做法 |
    |---|---|---|
    | `LineContent`（媒體）｜`LineProfile`（名稱） | `BaseAddress` ＋無開頭斜線相對路徑（`v2/bot/...`） | **只改註冊處 BaseAddress**，client 類別零改動（`??=` 不覆蓋註冊值，已查證） |
    | `LineSticker`（貼圖） | 同上（`BaseAddress` ＋相對路徑） | 同上 |
    | `LineProfileImage`（頭貼圖檔） | **LINE API 回應裡的絕對 URL**（`_imageHttpClient.GetAsync(pictureUrl)`，網域由 LINE 決定、非固定） | 見定案10b——改 BaseAddress 無效，這是規劃階段漏算的第四條 |

10b. **頭貼圖檔的 proxy 化（定案10 的例外，規格撰寫階段查證後補）**：
    `LineProfileClient` 下載頭貼用的是 LINE API 回傳的**絕對 URL**，改 `BaseAddress` 完全無效
    （絕對 URL 會蓋過 BaseAddress）。做法：
    - proxy 端提供 `/line/image/{host}/{**path}`，把 `{host}` 併回目標網域。
    - **`{host}` 必須通過寫死的網域字尾允許清單**（`.line-scdn.net`、`.line.me`）才轉發，
      否則回 403——這是把「目標網域寫死」這條硬契約在動態網域情境下的等價保證，
      絕不能讓外部輸入決定任意目的地（否則就是開放代理／SSRF）。
    - Edge 端在 `OutboundVia=EdgeProxy` 時，把絕對 URL 改寫成
      `{OutboundProxyBaseUrl}line/image/{原 host}{原 PathAndQuery}` 再下載；
      改寫邏輯抽成可單獨單元測試的純函式（不在 client 裡塞條件分支）。
    - 原 URL 的 host 不在允許清單時（LINE 換 CDN 網域）**不改寫、直連**——寧可退回直連
      也不要靜默丟掉頭貼；同時記一次節流過的 Warning 提示要更新清單。
11. **EdgeProxy 端 LINE 轉發路由（硬契約）**：
    - 三條固定路由（網域寫死在程式裡）：`/line/api/{**path}`→`https://api.line.me/{path}`、
      `/line/data/{**path}`→`https://api-data.line.me/{path}`、
      `/line/sticker/{**path}`→`https://stickershop.line-scdn.net/{path}`。
    - 一條動態網域路由：`/line/image/{host}/{**path}`（見定案10b），`{host}` 必須通過
      **寫死的網域字尾允許清單**才轉發，否則 403。
    - **路徑之外的任何輸入都不得影響目的地**；`{host}` 是唯一的例外，且被字尾清單夾住。
      絕不做通用 proxy。
    - 只允許 GET；`Authorization` 標頭透傳（proxy 不儲存 token）；其餘標頭不轉。
    - **串流轉發**：媒體可達數百 MB，用 `ResponseHeadersRead`＋`Stream.CopyToAsync` 直通，
      不得整包進記憶體（與 webhook 轉發的 512KB 緩衝策略刻意不同，理由要寫在註解）。
    - 掛新白名單 `EdgeProxy:AllowedClientIps`（填 Edge 的 IP，**空=全擋**，沿用
      `IpAllowlistMiddleware`）——這幾條路由在公網上，白名單是硬要求；
      webhook 轉發路由**不**掛這份白名單（LINE 的來源 IP 不固定）。
    - 防火牆矩陣變化要進文件：`OutboundVia=EdgeProxy` 需要開通 **edge→proxy** 單向
      （與 webhook 的 proxy→edge 相反方向，原環境可能沒開）。
12. **Edge 動態設定基礎（熱生效）**：自訂 `ConfigurationProvider` 啟動時解密
    `Db\edge-settings.dat`（**raw DPAPI machine scope**——不能用 ASP.NET DataProtection，
    Edge 上它的金鑰環是 ephemeral、重啟就解不開，實機 log 已見警告）疊在 appsettings 之上，
    支援 Reload；設定頁存檔→寫檔→Reload→**立即生效**。加密檔沒有的鍵自然回落 appsettings。
13. **設定頁只收可熱生效的鍵（使用者定案）**：`Line:ChannelSecret`、`Line:ChannelAccessToken`、
    `Ingest:ApiKey`、`Ingest:AllowedClientIps`、webhook 來源限制（定案15）。
    消費點從「啟動快照」改「動態讀」：`IOptions`→`IOptionsMonitor.CurrentValue`
    （LineSignatureValidator、IngestApiKeyMiddleware）；`IpAllowlistMiddleware` 改每請求讀
    IConfiguration；**出站 `X-Ingest-Key` 統一改為 DelegatingHandler 動態附加**（現況 6 處
    註冊時 default header＋HttpIngestSink 自帶——不改的話熱換金鑰會出現「入站驗新值、
    出站送舊值」的裂縫，兩台協調換鑰時必炸）；LINE client 的 `Authorization` 同理改
    DelegatingHandler。其餘鍵一律不進設定頁。
14. **極簡設定頁**：簡單 HTML 但排版乾淨（內嵌基本 CSS，不引外部資源、不套 ui-ux-pro-max
    ——使用者定案）；minimal API 兩支（GET 頁面＋POST 存檔），不進 controller／capability
    體系；只在 Edge 模式 Map；守門白名單 `EdgeAdmin:AllowedClientIps`（**只能放 appsettings**
    ——放進加密檔＝設錯一次把自己鎖在門外；空=全擋）。機密欄位只顯示「已設定＋末四碼」，
    POST 空值＝維持原值，**永不回傳明文**。
15. **webhook 來源限制（需求3 定案）**：動態設定 `WebhookSource:Mode`（`Any` 預設／
    `AllowlistOnly`）＋`WebhookSource:AllowedIps`——`AllowlistOnly` 時 Edge 的
    `/api/line/webhook` 只接受清單內來源（填 proxy 的 IP），其餘回 403。熱生效、進設定頁。
    放行判斷用 `RemoteIpAddress`（proxy 直連 Edge，不需 forwarded headers）。
16. **範例資料紀律**：文件、樣板、測試中的位址一律用 RFC 5737（192.0.2.0/24）或
    `*.example`，不得出現真實環境資料。

## 俯視影響評估（使用者要求，寫規格前先做）

| 影響點 | 判定與對策 |
|---|---|
| LINE client 相對路徑 | ⚠️ 三支是 BaseAddress＋相對路徑（只動註冊處即可），**第四支 `LineProfileImage` 用 LINE 回傳的絕對 URL**——規劃階段漏算，寫規格時查證補為定案10b（proxy 端動態網域路由＋字尾允許清單）。教訓：「改 BaseAddress 就好」的前提要對**每一支** client 逐一查證，不能從三支推論到全部 |
| 出站 X-Ingest-Key 六處快照 | ❌ 熱換金鑰的裂縫（見定案13），本輪一併收斂成 DelegatingHandler——**Core 端也受影響**（同一註冊碼路徑），驗收要含 Core 模式出站標頭仍正確 |
| `IpAllowlistMiddleware` 動態化 | ⚠️ 行為變化外溢：appsettings 預設 `reloadOnChange=true`，改每請求讀後 **Core／Viewer 的白名單改 appsettings 也會熱生效**（現況要重啟）。判定為刻意改善，記進文件；驗收含「三處掛載點行為不變」的既有測試全綠 |
| `IOptionsMonitor` 引入 | ⚠️ 全專案首次使用——只限定案13 列的消費點，其他地方**不得**跟風改（防止 agy 大面積替換）；`Configure<T>(section)` 已存在，change token 由新 provider 觸發 |
| `Line:OutboundVia` 誤用 | ⚠️ `Line` 節所有模式都讀得到；非 Edge 模式設 `EdgeProxy` 無意義——啟動驗證記 Warning（比照 `Ingest:Channel` 的同型警告） |
| BIDIR Pull 交互 | ✅ Edge=Pull＋OutboundVia=EdgeProxy 是合法組合（Edge 完全不主動連 Core、只連 proxy）；心跳（打 Core）不經 proxy，不受影響 |
| Edge 重佈掉設定 | ⚠️ `Db\edge-settings.dat` 與 outbox.db 同目錄——沿用既有「重佈時 Db\ 要搬出站台目錄」的部署建議，文件提一句即可 |
| DPAPI 測試 | ⚠️ machine scope 在 CI／他機解不開——加密層抽最小介面，測試用明文替身；DPAPI 實作只做「加密後能解回」的 Windows 整合測試 |

## 作業總覽（第二階段）

### 作業D｜LINE outbound 經 EdgeProxy
- 定案10＋10b＋11。Edge 端：`LineOptions` 加 `OutboundVia`（enum）＋`OutboundProxyBaseUrl`，
  OutboundHere 區塊的**三支** BaseAddress 型 client 依 OutboundVia 決定 BaseAddress；
  第四支（頭貼圖檔）走定案10b 的 URL 改寫純函式；啟動驗證（`=EdgeProxy` 而位址空→擋啟動；
  非 Edge 模式設 EdgeProxy→警告）。
- EdgeProxy 端：四條轉發路由＋串流＋`EdgeProxy:AllowedClientIps` 白名單。
- 驗收：
  - Edge 端三支 client 在兩種 OutboundVia 下的 BaseAddress 形狀（含子應用路徑）。
  - 頭貼 URL 改寫純函式：`Direct` 時原樣不動；`EdgeProxy` 時改寫成
    `/line/image/{host}{path}` 且保留 query；**host 不在允許清單時不改寫**（退回直連）。
  - proxy 端真實 host 測試——四條路由 GET 轉發到正確目標（前三條網域寫死、
    第四條由 `{host}` 併回）、`Authorization` 透傳、非 GET 不轉發、白名單空=403、
    **`{host}` 不在字尾允許清單時 403**（開放代理防線，這條一定要有）。
  - **大 body 串流**：假 handler 回一個「被讀到底就會失敗」的自訂 Stream，
    斷言 proxy 端沒有整包緩衝。
  - 既有 webhook 轉發測試全綠（兩組路由互不干擾）。
- 測試總數 > 954＋14。

### 作業E｜動態設定基礎與熱生效化
- 定案12＋13。新 `EncryptedSettingsProvider`（含最小加密介面＋DPAPI 實作）、
  DelegatingHandler（X-Ingest-Key、LINE Authorization 各一或共用一支，實作自定）、
  四個消費點改 `IOptionsMonitor`／每請求讀。
- 驗收：寫入加密檔→Reload→`IOptionsMonitor.CurrentValue` 立即反映；檔案不存在→
  純 appsettings 行為不變；**熱換 `Ingest:ApiKey` 後：入站驗證用新值、出站標頭同請求即新值**
  （裂縫回歸釘）；驗簽用新 ChannelSecret 即時生效；Core／AllInOne／Viewer 既有測試全綠
  （行為不變的驗收）；加密檔內容非明文（不含已知子字串）。
- 測試總數 > 作業D 結束數＋14。

### 作業F｜極簡設定頁＋webhook 來源限制
- 定案14＋15。GET/POST minimal API、`EdgeAdmin:AllowedClientIps` 守門、
  webhook 來源檢查（掛在 webhook 路徑、`Any` 時零行為差異）。
- 驗收：白名單外 403、空白名單全擋；GET 頁面不含任何機密明文（含末四碼遮罩斷言）；
  POST 空值不覆蓋既有機密；存檔後不重啟即生效（端到端：改 ChannelSecret→舊簽章 401）；
  `WebhookSource=AllowlistOnly` 時清單外來源 403、清單內 200，`Any` 時不限；
  既有 webhook 測試全綠。
- 測試總數 > 作業E 結束數＋12。

### 作業G｜文件與樣板（Claude 親做）
- `DEPLOYMENT-MODES`：三種拓撲組合表＋防火牆矩陣（edge→proxy 新方向）；
  `DEPLOYMENT-GUIDE`：設定頁使用說明、機密改由設定頁管理後「appsettings 填哪些」的改寫；
  README 設定表；Edge／EdgeProxy 樣板更新。範例位址守定案16。

### 第二階段終檢
- 跨段 grep：新設定鍵消費點、DelegatingHandler 掛滿六處出站、樣板與程式鍵名一致。
- 兩個獨立 Explore 審全 diff；NUL／BOM 掃描。

