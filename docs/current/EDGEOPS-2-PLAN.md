# EDGEOPS-2 第 1 輪規劃：edge-admin 子應用路徑修正＋連線診斷補網址與 IP

> 狀態：規劃中
> 基準：dev@f033138（1228 綠）
> 來源：使用者實測回饋——(1) IIS 子 application 部署下 edge 設定頁測試功能 404；(2) LINE outbound 錯誤紀錄缺目標網址與 IP，難以跟網管核對防火牆開通。

## 批次總覽

| 批次 | 內容 | 規模 | 相依 | 實作方 |
|---|---|---|---|---|
| A | edge-admin 頁面根路徑改為 PathBase 前綴 | 小（2 檔） | 無 | agy |
| B | 新增 OutboundTargetResolver（DNS 解析取 IP，best-effort＋快取） | 小（1 新檔＋DI＋測試） | 無 | Claude 自己做 |
| C | 連線測試頁強化：每列顯示實際請求網址＋解析 IP、失敗補 log、Classify 補 host | 中（3 檔） | B | Claude 自己做 |
| D | LINE outbound 失敗 log 補 IP；內部通道失敗 log 補 host | 小～中（4 檔） | B | agy |

建議順序：B → C，A、D 與其平行（D 需等 B 完成才能派）。

## 批次A：edge-admin 根路徑改 PathBase 前綴

### 現況與核對結果

- `MessageService.Web/Configuration/EdgeAdminPage.cs:177,180` — `action="/edge-admin/test-line"`（測試目前 Token／覆寫 Token 測試兩個 form）。
- `EdgeAdminPage.cs:563` — `action="/edge-admin"`（儲存設定 form）。
- `MessageService.Web/Configuration/EdgeAdminEndpoints.cs:119` — `Results.Redirect("/edge-admin?saved=true", ...)`。
- 專案無 `UsePathBase`；IIS 整合會把子 application 前綴放進 `Request.PathBase`，server 端 routing 正常，但 HTML 寫死的根路徑會讓瀏覽器 POST 到站台根 → 404。同一坑已在 `HttpBaseAddress.cs:9` 註解記錄過（webhook 方向）。
- 全專案 grep `action="/`、`href="/`、`Redirect("/`、`src="/` 只有上述四處；`/proxy-admin/errors` 是純 JSON API，無此問題。

### 定案

- 修法採 **render 時前置 `Request.PathBase`**，不用相對路徑（相對路徑受 URL 結尾斜線影響，脆弱）。
- proxy-admin 核對後無需修改（本輪定案，列此留痕）。

### 改動

1. `EdgeAdminPage` 的 render 入口增加 `string pathBase` 參數（必要參數，不給預設值），三個 form action 改為 `{pathBase}/edge-admin...`。
2. `EdgeAdminEndpoints` 的 GET／POST handler 傳入 `context.Request.PathBase.Value`；119 行 redirect 改為 `$"{context.Request.PathBase}/edge-admin?saved=true"`。

### 測試／驗收

- 新測試：pathBase 為 `/MSLine` 時，render 出的 HTML 三個 action 均含 `/MSLine/edge-admin` 前綴；pathBase 空字串時維持 `/edge-admin`（`PathBase.Value` 於根部署時為空字串，直接插值即得原行為）。
- 新測試：POST 儲存後 redirect Location 含 pathBase 前綴。
- `dotnet test` 全綠且測試數 ≥ 1228。

## 批次B：OutboundTargetResolver

### 現況與核對結果

- 全專案無任何 DNS 解析邏輯（`Dns.`、`GetHostAddresses` 零命中）。
- 節流／TTL 紀律：注入 `TimeProvider`（DI 已註冊單例），不得直接用 `DateTimeOffset.UtcNow`。

### 定案

- 只在**失敗當下與連線測試時**解析 IP，成功的一般 runtime 路徑不做 DNS 查詢（連線測試頁例外：成功列也解析，見批次C）。
- 解析結果做短期快取（TTL 60 秒），避免重試風暴時重複查詢。
- 走 EdgeProxy 拓撲時解析到的是 proxy 的 IP——這是正確行為，防火牆要對的就是實際連線目標；顯示端沿用 `ResolveOutboundHost` 的推導，不另處理。

### 改動

1. 新增 `MessageService.Web/Services/OutboundTargetResolver.cs`：
   - 建構子相依：`TimeProvider`（必要相依，不做可選參數 fallback）。
   - `Task<string?> ResolveAsync(string host, CancellationToken ct)`：`Dns.GetHostAddressesAsync` 包 2 秒逾時；成功回傳 IP 清單字串（逗號分隔，IPv4 優先排前）；失敗或逾時回傳 `null`。
   - 內部快取：`host → (結果, 到期時刻)`，TTL 60 秒，負結果（null）同樣快取避免反覆等逾時。
   - 提供 `static string FormatTarget(string host, string? ip)` 之類的組字 helper：`"{host}（IP：{ip}）"`，ip 為 null 時 `"{host}（IP 解析失敗）"`——各 log 點統一用它，不各自拼字串。
2. DI 註冊為單例。

### 測試／驗收

- 用 `FakeTimeProvider` 驗快取 TTL：60 秒內同 host 不重查、過期後重查（DNS 呼叫層抽介面或以可注入 delegate 替身驗證次數）。
- 解析失敗回 null 且被快取。
- `FormatTarget` 兩種形態的文字。

## 批次C：連線測試頁強化

### 現況與核對結果

- `MessageService.Web/Services/LineConnectivityTester.cs:87,153` — HTTP 狀態碼路徑 `Classify(ex, null)` 不帶 host，4xx/5xx 說明無目標資訊。
- `LineConnectivityTester` 不寫任何 log，結果只回頁面（`LineConnectivityTestResult`）。
- 結果 record（12–20 行）有 `Target`（host 推導自 `ResolveOutboundHost`）但無 IP、無實際請求完整 URL。
- 測試逾時 10 秒（208 行），四列依序執行。

### 定案

- 成功與失敗列**都**顯示「實際請求網址＋解析 IP」，完整呈現測試結果供網管核對（使用者定案）。
- IP 解析與 HTTP 測試各自獨立：DNS 解析失敗不影響 HTTP 測試結果判定，只影響顯示。
- 測試失敗時寫 Warning log（含 purpose、網址、IP、分類結果），讓沒開著頁面的情況也留下紀錄。

### 改動

1. `LineConnectivityTestResult` 增加 `string RequestUrl`（實際送出的絕對 URL：直連＝LINE 網域組出的 URL；EdgeProxy＝proxy 改寫後的 URL）與 `string? ResolvedIp`。
2. `LineConnectivityTester`：
   - 建構子加必要相依 `OutboundTargetResolver` 與 `ILogger<LineConnectivityTester>`。
   - `TestTargetAsync` 內解析 `target` 的 IP（每次測試四列，快取會讓同 host 只查一次）；組出實際請求絕對 URL 填入結果。
   - `:87` 與 `:153` 的 `Classify(ex, null)` 改為 `Classify(ex, target)`——`ReachableOnAnyResponse` 是 static，需把 target 傳入（改成實例方法或加參數）。
   - 每列失敗時 `LogWarning`：purpose、RequestUrl、ResolvedIp、Description。
3. `EdgeAdminPage` 測試結果表格增列「請求網址」「IP」欄（沿用現有表格樣式，值做 HTML encode）。

### 測試／驗收

- 既有 LineConnectivityTester 測試更新建構子後全綠。
- 新測試：結果含 RequestUrl（直連與 EdgeProxy 兩種拓撲各驗一次，EdgeProxy 時為 proxy URL）；HTTP 500 路徑的 Description 含目標 host（驗 `Classify(ex, target)` 生效）。
- 新測試：失敗時有 Warning log、成功時無。
- 頁面 render 測試：表格含 IP 與請求網址欄。

## 批次D：失敗 log 補目標資訊

### 現況與核對結果

缺目標資訊的失敗 log 點：

- `MessageService.Web/Middleware/EdgeProxyForwarderMiddleware.cs:145` — webhook 轉發失敗，`Classify(ex)` 不帶 host。
- `MessageService.Web/Services/HeartbeatService.cs:77,85` — 心跳失敗，不帶 host。
- `MessageService.Web/Outbox/OutboxForwarderService.cs:260,264` — outbox 推送失敗，無 host/URL。
- `MessageService.Web/Middleware/EdgeProxyLineForwarder.cs:201` — 已有完整 TargetUrl 但無 IP（這條是 LINE outbound，防火牆核對主場景之一）。

### 定案（範圍邊界，使用者定案）

- **IP 解析只做「LINE outbound 失敗」**：`EdgeProxyLineForwarder.cs:201` 補 IP。
- **內部通道（webhook／心跳／outbox，目標是自家 core/edge 伺服器）只補目標網址（host），不解析 IP**——目標是自己的機器，網址即足以核對。
- 其餘已含 host/URL 的 LINE outbound log 點（頭貼下載、內容下載、profile 刷新）**本輪不加 IP**：失敗訊息已由 `OutboundFailureClassifier` 分類出「DNS 解析失敗／連線逾時」等含 host 的說明，且連線測試頁已提供 IP 核對管道；避免每個 log 點都掛上 resolver 相依。若日後實務仍不足再擴。

### 改動

1. `EdgeProxyLineForwarder`：失敗 log（201 行）在 catch 內以 `OutboundTargetResolver` 解析目標 host 的 IP（必要建構子相依），訊息改為含 `FormatTarget` 輸出。
2. `EdgeProxyForwarderMiddleware:145`：`Classify(ex, <edge 轉發目標 host>)`，並在訊息模板加上目標網址。
3. `HeartbeatService:77,85`：訊息加上心跳目標 URL（host）。
4. `OutboxForwarderService:260,264`：訊息加上 Core 推送目標 URL（host）；`entry.LastError` 維持只存 `ex.Message`，不膨脹 DB 欄位。

### 測試／驗收

- 每個改動點各一測試：失敗時 log 訊息含目標網址（EdgeProxyLineForwarder 另驗含 IP 或「IP 解析失敗」字樣）。
- **不得出現「可選參數 `= null` ＋ fallback」的相依形狀**——resolver 一律必要建構子相依，測試替身誠實跟上。
- `dotnet test` 全綠且測試數不減。

## 明確不做（本輪定案）

- proxy-admin 頁：核對後為純 JSON API，無根路徑問題，不改。
- 內部通道（webhook／心跳／outbox）不做 IP 解析。
- 頭貼／內容下載／profile 刷新等已含 host 的 log 點不加 IP。
- 不引入 `UsePathBase`（IIS 整合已正確填 `Request.PathBase`，只需修 HTML 產出）。
