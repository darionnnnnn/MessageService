# LINEOUT-1：LINE outbound 取數診斷性修正

狀態：規劃定稿，待實作。基線：dev@3b8a1c8（EDGEOPS-1 終檢後，1159 綠，尚未收尾）。

## 背景與定案

Web 頁面缺名稱／頭貼／附檔內容，程式面診斷（見上輪對談與 EDGEOPS-1）確認接線與 token 代入正確，
但存在五個讓「取不到」難以排查或自我維持的實作問題。本輪全部處理，目的是**實測防火牆前，
log 與設定頁就能辨識問題類別**。使用者定案：四個待決全做（含 proxy 設定熱讀、四網域連通測試）。

## 作業總覽

| 作業 | 主題 | 委派 |
|---|---|---|
| A | 無聲失敗出聲化（空 token 守門、Null 佇列出聲、失敗分類 log） | agy |
| B | 頭貼缺圖自癒（staleness 缺圖條件）＋ staleness 失敗退避 | agy |
| C | proxy 設定（OutboundVia/OutboundProxyBaseUrl）熱讀 | agy |
| D | 連線測試分頁擴充為四網域 | agy |

本輪委派模型：agy（gemini-3.7-flash-high，整輪一種）。順序 A → B → C → D（D 的「經由」顯示要吃 C 的熱讀結果；A 的分類器 D 也要用）。規劃/終檢模型：Fable 5（本輪起）。

## 作業 A：無聲失敗出聲化

### A1＋A2（一次委派：兩個小機制）

**A1 空 token 守門下移**
- `LineAuthorizationHandler`：token 為 null/空白時**不加 Authorization 標頭**（不再送裸 `Bearer`），
  並記 Warning「Line:ChannelAccessToken 為空，LINE API 呼叫將失敗——請在 /edge-admin 設定頁重新填寫」；
  警告需節流（暫定每 10 分鐘最多一次，比照 `EdgeProxyForwarderMiddleware` 既有節流形式）。
- 既有行為不變：token 有值時注入、請求已帶標頭時不覆寫（上輪測試已釘住）。
- 「什麼算空」：`string.IsNullOrWhiteSpace`。

**A2 Null 佇列出聲**
- `OutboundHere=false` 時：啟動期記一行 Information「此主機不做 LINE outbound（Line:OutboundHere=false 或模式推導），媒體下載與頭貼刷新由其他主機負責」（位置：註冊矩陣的 else 分支或啟動驗證，執行端自決）。
- `NullContentDownloadQueue` 與 `NullProfileRefreshQueue`：**第一次** Enqueue 時各記一則 Warning（整個行程生命週期只記一次；「一次」以各佇列各自計），說明工作正被丟棄與原因；之後靜默。需執行緒安全。
- Null 實作維持零副作用（不得累積、不得丟例外），既有 `NullQueuesTests` 綠。

**驗收**：測試涵蓋——空 token 不送標頭且記警告（斷言假 handler 收到的請求無 Authorization）、有 token 行為不變、Null 佇列首次 Enqueue 記一次 Warning 而第二次不記。全套綠且**測試總數 >= 基線+5**。

### A3 失敗分類 log（一次委派）

- 新增一個純函式分類器（放 Services/，命名自取），輸入例外（與可選的 HTTP 狀態碼），輸出一段繁中診斷字串，類別至少涵蓋：
  - `HttpRequestException.StatusCode`＝401 →「token 無效或為空（檢查 Line:ChannelAccessToken）」
  - 403 →「被對端拒絕（走 EdgeProxy 時檢查 EdgeProxy:AllowedClientIps；直連時檢查對外防火牆）」
  - 404/429/5xx → 標明狀態碼與含義（429 要提示 LINE 限流）
  - DNS 失敗（`SocketException`/`HttpRequestException` 內層）→「DNS 解析失敗（防火牆或 DNS 設定），目標 {host}」
  - 逾時（`TaskCanceledException` 非使用者取消）→「連線逾時（防火牆未開通？），目標 {host}」
  - 連線拒絕 →「連線被拒（目標服務未啟動或防火牆 REJECT），目標 {host}」
  - 其他 → 例外型別＋Message。
- 套用到五個失敗記錄點（log 訊息尾端附上分類字串與目標 host）：
  1. `ProfileRefreshService.RefreshGroupAsync` 例外
  2. `ProfileRefreshService.RefreshMemberAsync` 例外
  3. `LineProfileClient.DownloadPictureAsync` 例外
  4. `ContentDownloadService` 每次 attempt 失敗與最終失敗
  5. 作業 B 新增的 staleness 失敗點（B 段完成後由 B 段自己套；A3 只交付分類器與 1~4）
- 目標 host 取自實際請求的 URI（EdgeProxy 拓撲下顯示的是 proxy 的 host——這是對的，實測時要看的就是「打向哪裡失敗」）。
- 分類字串同時供作業 D 的連線測試結果顯示使用（公開靜態方法即可，不做 DI 抽象）。

**驗收**：分類器單元測試逐類別一筆（401/403/DNS/逾時/拒絕/其他，至少 6 筆）；1~4 的 log 呼叫點以測試或 grep 驗證有帶分類字串。全套綠且**測試總數 >= 前段+8**。

## 作業 B：頭貼缺圖自癒＋staleness 退避（一次委派）

**B1 staleness 缺圖條件**
- 根因：頭貼下載失敗後名稱照樣 upsert、`UpdatedAt` 更新，而 staleness 只看 `UpdatedAt`——「缺圖」在判定裡不可見，7 天不重試。
- 修法（資料層驅動）：`DbProfileStore.GetStalenessAsync` 的 stale 判定改為
  「`UpdatedAt < cutoff` **或** （`PictureUrl` 有值 且 對應 blob 不存在）」，group 與 member 同型都改。
  staleness 計算單點在 `DbProfileStore`（`ApiProfileStore` 打 Core 的 ingest API → `IngestController` → `DbProfileStore`；`StagingProfileStore` 的值由 Core 派發），**只改這一處**，不要動 Api/Staging 實作。
- 防熱迴圈：`LineProfileClient.DownloadPictureAsync` 失敗仍回 `(null,null)`（名稱照常入庫），但要讓 `ProfileRefreshService` 知道「圖抓失敗」——介面回傳值加一個旗標（`GroupSummary`/`MemberProfile` 加欄位或另回傳，執行端自決，**不得為此改成丟例外**——名稱本身有價值，不能因圖失敗連名稱都丟）。`ProfileRefreshService` 收到「圖失敗」時：照常 upsert，但 **Suppress 改用 `FailureRetryAfter`（10 分鐘）而非 `SuppressWindow`（5 分鐘）之外再長套**——暫定：呼叫 `RecordFailure`（即冷卻 10 分鐘），不呼叫成功版 `Suppress`。效果：缺圖條目每 10 分鐘重試一次，防火牆修好後最慢 10 分鐘自動補齊。
- 對既有資料的影響（刻意的）：部署後「有 PictureUrl 但沒 blob」的存量條目會被重新嘗試下載，第一波有 LINE API 呼叫量；`RecordFailure` 冷卻讓它不會變成熱迴圈。
- 反例確認（破壞性判準）：群組/成員**本來就沒有頭貼**（LINE 回應無 `pictureUrl`）→ `PictureUrl` 為 null → 不觸發缺圖條件，不會無限重抓。這條要有測試。

**B2 staleness 查詢失敗退避**
- `ProfileRefreshService.ProcessAsync` 對 `GetStalenessAsync` 的例外就地 catch：對 group 與 member（有 userId 時）都 `RecordFailure`，記 Warning 並附 A3 分類字串，然後 return（不往外拋）。收掉 Edge→Core 不通時每則訊息重打一次的形狀。

**驗收**：測試涵蓋——(a) `UpdatedAt` 新但 PictureUrl 有值且無 blob → stale；(b) 無 PictureUrl 無 blob → 不 stale；(c) 圖下載失敗時 upsert 有做、冷卻有記、且冷卻窗內不重打 LINE；(d) staleness 例外 → RecordFailure 兩鍵都記、不外拋。member 與 group 同型皆測。全套綠且**測試總數 >= 前段+8**。

## 作業 C：proxy 設定熱讀（一次委派）

- 四個 LINE 具名 client（`LineContent`/`LineSticker`/`LineProfile`/`LineProfileImage` 的註冊 configure lambda）改為執行時從 `IOptionsMonitor<LineOptions>` 讀 `OutboundVia`/`OutboundProxyBaseUrl`（`AddHttpClient` 有帶 `IServiceProvider` 的多載；configure action 在每次 `CreateClient` 時執行，改讀 monitor 即熱生效）。行為契約與現狀完全相同：EdgeProxy 且 BaseUrl 有值 → 三個 client 的 BaseAddress 改寫（`LineProfileImage` 永遠不改寫）；否則不設 BaseAddress。
- `LineProfileClient` 建構式裡的 `IOptions<LineOptions>.Value` 快照（供 `DownloadPictureAsync` 的 URL 改寫用）同步改為 `IOptionsMonitor`——否則頭貼改寫路徑仍是舊值，只改一半會做出「三條熱生效、頭貼不熱生效」的新不一致。
- `LineConnectivityTester` 的「經由」判定已用 monitor，不動。
- 上輪的 `LineHttpClientRegistrationTests`（BaseAddress 斷言）必須維持綠——允許為了熱讀改斷言的取值方式，不得放寬斷言值。
- 註：`DeploymentValidator` 的 EdgeProxy 檢查仍只在啟動跑，執行期熱改成「EdgeProxy 但 BaseUrl 空」時的行為＝不改寫 BaseAddress（等同直連）＋連線測試按鈕會報「沒有 proxy 位址」（上輪已做），可接受，不另加執行期驗證。

**驗收**：註冊測試新增熱更新情境——啟動 Direct、改設定後（以 `IOptionsMonitor` 測試替身或設定 reload 觸發）新 `CreateClient` 的 BaseAddress 變為 proxy 路徑；反向亦然。既有四 BaseAddress 斷言綠。全套綠且**測試總數 >= 前段+4**。

## 作業 D：連線測試分頁擴充為四網域（一次委派）

- `/edge-admin` 連線測試分頁改為一鍵測**四個目標**（沿用既有按鈕與表單，一次 POST 回四列結果）：
  1. `api.line.me`：既有 `GET v2/bot/info`（帶 token），判準不變（2xx 成功／其他失敗＋原因）。
  2. `api-data.line.me`：`GET`（暫定打 `v2/bot/message/0/content` 或根路徑，執行端自決），判準＝**收到任何 HTTP 回應即「可達」**（400/401/404 都算通，重點是 TCP/TLS 通）；DNS/逾時/拒絕＝不可達，附 A3 分類字串。
  3. `stickershop.line-scdn.net`：`GET stickershop/v1/sticker/52002734/android/sticker.png`（52002734 暫定，公開貼圖），2xx＝可達且內容正常；其他 HTTP 回應＝可達（附狀態碼）；連不上＝不可達。
  4. `profile.line-scdn.net`：`GET` 根路徑，判準同 2（僅驗可達性；此 host 暫定，是 LINE 頭貼 CDN 的主要網域）。
- 走既有四個具名 client（`LineProfile`/`LineContent`/`LineSticker`/`LineProfileImage`）發出——EdgeProxy 拓撲下 1~3 自動經 proxy，4 用 `LineImageUrlRewriter` 改寫後的 URL（`LineProfileImage` client 無 BaseAddress，直接給絕對 URL）；每列標明實際打的目標（host＋經由）。
- 覆寫 token 欄位保留，只影響第 1 項。
- 每項逾時沿用 10 秒；四項**循序**執行即可（最壞 40 秒，可接受，不做並行以免規格膨脹）。
- 結果表格四列：目標／用途（名稱查詢、媒體內容、貼圖、頭貼 CDN）／結果（可達/不可達＋原因）／經由。全部 HtmlEncode。
- `OutboundHere=false` 的行為不變（顯示說明、無按鈕）。

**驗收**：測試（Fake handler）涵蓋——四列都渲染；其中一項 DNS 失敗時該列顯示不可達與分類字串、其他列不受影響；404 顯示為「可達」；EdgeProxy 拓撲下第 1 項的請求 URI 是 proxy 路徑。全套綠且**測試總數 >= 前段+6**。

## 拆分原則自查

- 什麼算一個／分母為零：A2「一次」以各佇列各自計；D 的「可達」判準明寫（任何 HTTP 回應算通）。
- 破壞性判準：B1 的反例（本來就沒頭貼者不得觸發重抓）已列入驗收。
- 單向閘門：A2 的「只記一次」旗標——繞過路徑＝重啟行程即重置，可接受（它只是提示，不是狀態）。
- 移除類：無移除。C 是等價改寫，行為契約與斷言值不變。

## 複檢（規劃完成後）

- 與既有設計衝突：B1 改 staleness 語意，`DEPLOYMENT-MODES`/`README` 未描述 staleness 細節，無文件矛盾；`ProfileCacheOptions.FailureRetryAfter` 註解語意（失敗冷卻）與 B1 用法一致。C 推翻「啟動快照」現狀，上輪 EDGEOPS-1 PLAN 遞延節與 `LineConnectivityTester` 錯誤訊息裡「改過設定後尚未重啟站台」一句要在 D 段或收尾時同步修掉——**列入 D 段規格**。
- 批次間衝突：A3 分類器 → B2/D 消費，順序已排；C 改 client 註冊、D 用 client 發請求，C 先 D 後；A1 改 handler、D 的第 1 項依賴 handler 注入行為，不衝突（D 不動 handler）。
- 細節補列：A1 節流警告在 EdgeProxy 模式不會出現（該模式無 LINE client）——不需處理。D 的第 4 項若 LINE 頭貼實際網域非 `profile.line-scdn.net`，執行端可依 `LineImageUrlRewriter` 白名單（`.line-scdn.net`/`.line.me`）選一個代表 host，標暫定。
- 複檢完成，除上列外無新增事項。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A1+A2 | agy | 完成 | 1169 綠（+10） | 無落差。啟動提示選了 `DeploymentValidator` 那條路（沿用該處既有 logger）。四個既有測試檔的改動都只是建構式簽章跟上（多注入 logger），無斷言放寬。 |
| A3 | agy | 完成 | 1186 綠（+17） | 首次委派零輸出零 diff（已知的假成功形狀），重派後成功；期間發現兩個 agy 殘留行程與新的一起在改 repo，已清掉舊的。三處手改：(1)(2) 兩個背景服務都加了「可選相依 `IOptions<LineOptions>? = null` ＋ 欄位 fallback」且同時又在 scope 內解析一次（規格明文禁止的形狀＋雙路徑），改為只留 scope 解析；(3) 兩個服務各寫一份「依 OutboundVia 推導 host」，抽成 `HttpBaseAddress.ResolveOutboundHost` 單一真值來源。另修掉分類器中規格外的防禦（以型別名字串比對判 TLS、host 解析的多重猜測分支）。 |
| B | agy | 完成 | 1200 綠（+14） | 缺圖 stale 判定抽成共用私有方法、反例（無 PictureUrl 不觸發）有測試。手改一項：agy 讓「頭貼過大跳過」兩條路徑回報成功，配上 B1 後同一張過大的圖會每 5 分鐘被重抓一次（B1 讓它永遠 stale）——改為回報失敗走 10 分鐘冷卻，並補一筆測試釘住。 |
| C | agy | 完成 | 1205 綠（+5） | 無落差。三個 client 的改寫決策抽成共用本地函式（未寫三份）；`LineProfileClient` 的頭貼改寫同步改吃 `IOptionsMonitor`；連線測試那句「尚未重啟站台」已改掉。熱更新雙向（Direct↔EdgeProxy）有測試。 |
| D | agy | 完成 | 1216 綠（+11） | 四筆共用同一個 `TestTargetAsync`，判定用既有分類器。手改一項：三個「有回應即可達」的判定寫了三份逐字相同的 lambda，收斂成單一 `ReachableOnAnyResponse`。 |
| 文件同步 | Claude | 待辦 | — | 收尾時處理 |
| 終檢 | Explore×2 | 完成 | 1225 綠 | 見下節 |

## 併回前終檢（兩個獨立 Explore：程式碼、診斷可用性）

### 已修（本輪處理）

| 嚴重度 | 發現 | 處置 |
|---|---|---|
| 高 | B1 讓「有 PictureUrl 卻沒圖」永遠 stale，但**永久性失敗**（頭貼超過 2MB 上限、404／410）也拿到跟暫時性失敗一樣的無限重試預算：每筆每 10 分鐘打一次 Profile API＋CDN，永遠不收斂，且冷卻表條目只增不減 | 分流永久／暫時：`LineProfileClient` 回傳 `PictureFetch`（Downloaded／Transient／Permanent），永久性失敗時 `DbProfileStore` 把網址寫進既有的 `PictureFetchedUrl`，staleness 的缺圖條件加上 `PictureUrl != PictureFetchedUrl`——「這個網址試過拿不到」就不再判缺圖（換新頭貼會重新試）。暫時性失敗完全不動，防火牆修好仍是 10 分鐘內自動補齊。補 7 筆測試（含「換網址要重試」與「暫時失敗不可蓋掉 FetchedUrl」） |
| 高 | 連線測試分頁在 EdgeProxy 拓撲下把 host 顯示成 LINE 的網域，但實際連的是 proxy——與 runtime log（已用 `ResolveOutboundHost`）互相矛盾，會讓人去開錯誤的防火牆洞 | 測試器改用同一個 `HttpBaseAddress.ResolveOutboundHost` 推導顯示的 host |
| 高 | 後三列的判準「任何 HTTP 回應＝可達」讓 proxy 回的 403（白名單沒放行）／502／504（proxy 連不到 LINE）顯示成綠色「可達」，正好把斷掉的鏈報成通的 | 403／502／503／504 改判為失敗並附分類字串，其餘狀態碼仍算可達 |
| 中 | 頭貼那列繞過「EdgeProxy 但沒有 proxy 位址」的保護（它沒有 BaseAddress），會靜默退回直連還報成功 | 該列改以「URL 有沒有被改寫成 `/line/image/` 路徑」判斷，與其他三列同一個守門 |
| 中 | `IsDnsError` 把 `HostUnreachable`／`NetworkUnreachable`／`HostDown` 都報成「DNS 解析失敗」——企業防火牆 DROP 封包時最常見的形狀被指向錯誤方向 | 只留 `HostNotFound`／`NameResolutionError`；新增「網路無法到達：沒有路由（防火牆 DROP 或路由設定）」一類 |
| 中 | 分類器把任何 `TaskCanceledException` 都報成「連線逾時（防火牆很可能未開通）」，使用者中斷頁面請求也會這樣報 | 內層有 `TimeoutException` 才算逾時；純取消回「請求已取消（呼叫端中斷，不是連線問題）」 |
| 中 | A3 的 host 推導用 `IOptions`（啟動快照），而 C 已把 client 改成熱讀——改完 proxy 設定後請求打向新 proxy、log 卻寫舊 host | 兩處改吃 `IOptionsMonitor` |
| 中 | 分類器沒套到實測會用到的其他 outbound 失敗點 | 補上心跳（Edge→Core 最早的訊號）與 EdgeProxy 的 webhook 轉發（proxy→Edge 唯一的訊號） |
| 中 | 節流測試是恆真形狀（把間隔改成 1 秒或無限大都仍綠），10 分鐘沒被釘住 | `LineAuthorizationHandler` 改注入 `TimeProvider`（必要相依，非可選），測試用假時鐘斷言「9 分 59 秒不記、10 分 1 秒再記」 |
| 低 | 分類器五次重複走訪例外鏈＋規格外的 `Uri` 多載與 host 猜測分支；`EdgeAdminPage` 用中文字串 `Purpose == "名稱查詢"` 決定顯示「成功／可達」；`LineContentClient` 留著未使用的 `IOptions` 參數；AllInOne 模式下新的 Information 與既有 Warning 語意打架 | 分類器改單次走訪＋收齊訊號再產字串；結果型別加 `StrictSuccess` 旗標取代字串比對；刪死參數；Information 加 `mode is not AllInOne` 條件 |

### 遞延（未做，理由）

- **分類器補到 `HttpIngestSink`、`EdgePullService`、`ApiProfileStore`／`ApiContentWorkSource`**：這幾條是 Edge↔Core 的資料通道，與本輪要診斷的「LINE outbound」是不同的鏈路；心跳已經是這條鏈路最早的訊號，先看它就夠。
- **拉取模式下 Core 端派工（`EdgePullService.BuildProfileDispatchAsync`）沒有冷卻表**：缺圖條目在 Core 眼中每輪都 stale，會白跑 staleness 查詢與派工（實際 LINE 呼叫仍被 Edge 端的 10 分鐘冷卻擋住，不是熱迴圈）。永久失敗分流後受影響範圍已大幅縮小，剩下的是「暫時失敗期間每輪多一次查詢」。
- **`ProfileRefreshService` 的冷卻粒度**：圖失敗時整筆（含已成功的名稱）進 10 分鐘冷卻，沒有「名稱 OK／圖沒 OK」的分離粒度。
- **B 的冷卻長度測試仍以 50ms 冒充 10 分鐘**（`ProfileRefreshService` 未注入時鐘）：能抓到「把 `RecordFailure` 換回 `Suppress`」的回歸，但釘不住長度。
- **連線測試第 2、4 項用 `probe` 路徑**（規劃書寫的是根路徑或既有路徑）：功能等價，未改。

## 體檢交接

- 全量測試：**1225 綠 / 0 失敗 / 0 略過**（本輪起點 1159，共 +66）。
- 建置 0 警告 0 錯誤；改動檔案皆無 BOM 與 NUL 汙染。
- 文件同步（README／DEPLOYMENT-GUIDE 的排查段）尚未做，留給收尾流程。
