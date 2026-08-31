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
