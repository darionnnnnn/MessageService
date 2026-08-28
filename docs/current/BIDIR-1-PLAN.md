# BIDIR-1：Edge↔Core 通道方向彈性化規劃

日期：2026-08-28。狀態：全案完成（實作＋終檢＋換模型體檢），測試 909 綠、零警告。

## 背景與目標

現行所有 Edge↔Core 流量一律由 Edge 發起（訊息推送、心跳推送、媒體派工拉取、Profile 更新），
前提是防火牆開通 edge→core。本輪新增反向拓撲支援：**防火牆只開通 core→edge 時系統仍完整可用**
（Core 每 1 秒輪詢 Edge 取訊息與心跳，媒體與 Profile 也走反向協定），並提供自動切換。

去重基礎：`WebhookEventId` 唯一索引已存在，雙通道重疊期重複投遞無害（at-least-once 語意）。

## 定案（與使用者確認）

1. **四條通道全反向化**：訊息、心跳、媒體派工＋blob 回收（含附檔與貼圖）、Profile 更新
   （明列四類：群組名稱、成員名稱、群組圖片、成員頭貼）。僅 core→edge 拓撲功能完全對等。
2. **通道設定 `Ingest:Channel`（Edge 端）**：`Auto`（預設）/`Push`/`Pull`。
   - `Push`：現行行為，不開 Edge API 面。
   - `Pull`：Edge 從不主動連 Core（不推送、不拉派工、不推心跳），只開拉取 API 面；`Ingest:BaseUrl` 可空。
   - `Auto`：推送優先；推送失敗即退回被動模式，之後**固定每 `Ingest:ChannelProbeIntervalMinutes`
     （預設 60）分鐘探測一次** Core 可達性，成功即恢復推送；同時開 API 面供 Core 輪詢。
     探測期間拉取模式照常運作、資料零損失，頻率只影響升級時機。
     明知單向封死的環境設 `Push`/`Pull` 可零無謂連線嘗試。
3. **Edge 端媒體 blob 暫存走記憶體**，護欄：
   - 總量上限 `Ingest:PullStagingMaxBytes`（暫定 600MB），**啟動驗證必須 ≥ `Ingest:MaxContentBytes`**。
   - 超過上限拒收新派工（工作留在 Core 端 Pending，背壓不掉資料）。
   - Edge 重啟遺失暫存 → 靠 Core 既有認領租約（`ContentDownload:ClaimLeaseMinutes`）逾時回收重派。
4. **Core 側啟停規則（Auto 收斂）**：Core 設定了 `Ingest:EdgeBaseUrl` 且超過
   `Ingest:PullActivationSeconds`（暫定 180 = 3×心跳間隔）沒收到推送心跳 → 啟動輪詢；
   恢復收到推送心跳 → 停止輪詢。未設 `EdgeBaseUrl`（預設空）→ 永不輪詢，行為與現行完全相同。
   edge→core 中斷後最壞約 `PullActivationSeconds` 內完成切換，期間訊息累積於 outbox 零遺失。
   **輪詢失敗退避（硬契約）**：poll 失敗 → 間隔指數退避（1s 起、上限
   `Ingest:PullFailureMaxBackoffSeconds` 暫定 60）；成功即回到正常間隔。
   失敗期間不逐次記 log，只記「進入退避／恢復」轉換（失敗持續中最多每 10 分鐘一行摘要）。
5. **零設定升級（硬契約）**：既有 appsettings.json 一字不改即可升級——所有新鍵皆有預設值；
   Core/AllInOne 行為與現行相同（輪詢器在 EdgeBaseUrl 空時不註冊）；Edge 推送行為不變，
   新增的 `api/edge` 路由面由既有共用金鑰 `Ingest:ApiKey` 保護（未設金鑰整面 404，沿用既有 middleware 語意）。
   啟用新拓撲唯一需新增的設定是 Core 端 `Ingest:EdgeBaseUrl`。
6. 輪詢間隔 `Ingest:PullIntervalSeconds` 預設 1。**單一在途**：上一次 poll 未返回前跳過本輪，不堆積。
7. 沿用「同一角色不部署兩台」限制：`EdgeBaseUrl` 為單一位址。
8. **blob 傳輸防重與必達（硬契約）**：
   - blob 走獨立 GET，不進 poll 回應；Core 對同一 content「取回中」期間不重發 GET（in-flight 標記），
     傳輸超過輪詢間隔絕不重複傳送。
   - GET 失敗（連線中斷、截斷、位元組數不符）→ 清除 in-flight，下一輪重新取回。
   - Core 以位元組數驗證完整並持久化後才 ack；Edge 收到 ack 前不得釋放暫存。

## 二次稽核定案（Explore 查證後補強）

9. **推送心跳才算數**：Core 停止輪詢的判斷只認「推送通道」進來的心跳（`POST /api/ingest/heartbeat`
   端點以程序內記錄通知輪詢器），輪詢自己拉回的心跳不重置計時——否則拉取模式下會自我停止震盪。
10. **Edge API 面的白名單語意**：`IpAllowlistMiddleware` 空清單=全擋（既有語意，寧嚴勿鬆）。
    Edge 現有設定無白名單 → 升級後 `api/edge` 面「存在但全擋」，不破壞零設定升級；
    **啟用拉取拓撲時 Edge 必須設定 `Ingest:AllowedClientIps` 為 Core 的 IP**（文件明寫）。
    Edge 管線現況完全不掛保護層（掛載條件是 `HasDatabaseAccess && !Viewer`），作業A須新增
    `/api/edge` 專屬掛載段（IP 白名單→金鑰，順序同現行 ingest 群組）。
11. **通道狀態機暫停轉發器**：現行 `OutboxForwarderService` 無暫停開關；Auto「退回被動」由新的
    通道狀態機閘住轉發迴圈（失敗→暫停；探測成功→恢復）。切換窗口雙消費者重疊已查證安全
    （完成=刪除為冪等 no-op＋下游 `WebhookEventId` 去重），ack 遇到已不存在的條目視為成功。
12. **心跳統計抽出**：`ComputeOutboxStatsAsync` 現為 `HeartbeatService` 私有方法，抽成獨立可重用
    類別（純 OutboxDbContext 查詢、無相依），poll 端點即時計算心跳內容。
13. **記憶體 work source 遵守介面完整契約**：`IContentWorkSource.GetPendingIdsAsync` 契約為
    可重複掃描（非取出即消耗）＋租約回收語意，記憶體實作必須等價，否則週期重掃第二輪空手、
    失敗項目永久遺失。
14. **圖片不進 poll 回應**：群組圖片與成員頭貼統一復用媒體 blob 的「完成清單＋獨立 GET＋ack」
    通道（poll 用短逾時 client，不適合載 2MB×N 圖片）；poll 回應只帶文字類 Profile 結果。
15. **錯誤組合可見性**：Edge 設 `Pull` 而 Core 未設 `EdgeBaseUrl` 時系統靜默停擺且啟動驗證無法
    跨主機偵測（既有限制）——靠 UI 心跳過期告警呈現，文件已知限制明列此組合。

## 協定設計（契約層）

Edge 新開 API 面（新 capability，條件：`Mode == Edge` 且 `Channel != Push`），
認證沿用 `X-Ingest-Key` middleware（金鑰未設回 404）＋ `Ingest:AllowedClientIps` IP 白名單，
路由前綴暫定 `api/edge`：

| 端點（暫定） | 用途 |
|---|---|
| `POST /api/edge/poll` | 心跳＋待取訊息批次＋blob 完成清單（媒體與 Profile 圖片）＋Profile 文字結果一次回；request body 攜帶 Core 的派工（待下載媒體 id+meta、Profile staleness 清單） |
| `POST /api/edge/outbox/ack` | Core 落地成功後確認，Edge 才標記 outbox 完成（at-least-once；單一消費者，不需租約） |
| `GET /api/edge/content/{id}` | Core 取回已下載完成的 blob（最大 `MaxContentBytes`） |
| `POST /api/edge/content/{id}/ack` | Core 落地 blob 成功後確認，Edge 釋放記憶體暫存 |

設計原則：**輪詢合一**——1 秒一次的 `poll` 同時完成心跳、訊息、派工、完成通知、Profile 五件事，
避免多條輪詢迴圈；blob 因體積走獨立 GET。端點形狀（欄位名、批次大小上限）標暫定，
執行端可依實作事實調整並在回報寫理由，但「ack 前不得刪除」與「拒收即背壓」是硬契約。

## 新增設定鍵總表

| 鍵 | 端 | 預設 | 說明 |
|---|---|---|---|
| `Ingest:Channel` | Edge | `Auto` | Auto/Push/Pull |
| `Ingest:EdgeBaseUrl` | Core | `""` | 空=永不輪詢（現行行為） |
| `Ingest:PullIntervalSeconds` | Core | 1 | 輪詢間隔（單一在途，未返回跳過本輪） |
| `Ingest:PullActivationSeconds` | Core | 180（暫定） | Auto 下多久沒推送心跳才啟動輪詢 |
| `Ingest:PullStagingMaxBytes` | Edge | 600MB（暫定） | 記憶體暫存上限，啟動驗證 ≥ MaxContentBytes |
| `Ingest:ChannelProbeIntervalMinutes` | Edge | 60 | Auto 退回被動後的推送探測週期（防火牆方向檢查） |
| `Ingest:PullFailureMaxBackoffSeconds` | Core | 60（暫定） | poll 連續失敗時的退避上限 |

## 作業總覽

本輪委派模型：agy（gemini-3.7-flash-high），整輪一種；中途切換須註明起點且不換回。
每作業一個 commit 主題，實作與測試同階段。

### 作業A｜Edge API 面基礎（訊息＋心跳）
- 新 capability（暫定 `EdgePullApi`）、路由閘門、**新增 `/api/edge` 專屬管線掛載段**
  （IP 白名單→金鑰，順序同現行 ingest 群組；定案10）、`Channel` 設定與啟動驗證
  （`Pull` 下 BaseUrl 可空；`Push`/`Auto` 下維持現行必填驗證）。
- 心跳統計抽成獨立類別（定案12），poll 回應即時計算。
- `poll`（先只含心跳＋訊息）與 `outbox/ack`：outbox 讀取語意=未 ack 的重複回傳；ack 後等同現行推送成功的標記。
- 驗收：Edge 各 Channel 值下路由開/關正確（Push 模式 `api/edge` 全 404）；缺金鑰 404、錯金鑰 401、
  白名單外 403；poll→ack 生命週期測試；未 ack 重複 poll 仍回同批（at-least-once）；
  ack 過的不再出現。既有 783+ 測試全綠。

### 作業B｜Core 輪詢器（訊息＋心跳）＋ Auto 收斂
- 新 BackgroundService：依定案4啟停；**只認推送通道心跳**（定案9）；poll 回應中訊息經
  `DirectIngestSink` 落地（重複靠唯一索引去重後仍 ack）、心跳代寫 `HostHeartbeats`
  （比照現行 Edge 代寫語意，fingerprint null）；落地成功才 ack；ack 遇已不存在條目視為成功（定案11）。
- Edge 端通道狀態機：閘住 `OutboxForwarderService` 轉發迴圈（定案11），`Auto` 失敗→暫停＋固定週期
  推送探測（`ChannelProbeIntervalMinutes`），成功→恢復；`Pull` 模式下推送/拉取類背景服務全不註冊。
- poll 用獨立短逾時 HttpClient（暫定 5 秒），不與 blob 長逾時 client 共用。
- 日誌紀律：每次 poll 不記 log，只記狀態轉換（輪詢啟動/停止、通道切換、探測成功恢復推送）。
- 驗收：門檻內有推送心跳→不輪詢；超過→輪詢啟動；推送恢復→輪詢停止（時間可注入的單元測試）；
  重複投遞不產生重複列；Core 未設 EdgeBaseUrl 時零行為變化（既有測試全綠即證）；
  連續 poll 正常運作下無 per-poll 日誌；poll 連續失敗時間隔退避至上限且不逐次記 error、
  恢復後回到正常間隔（時間可注入測試）；
  **零設定升級**：以現行 deploy/ 四份樣板設定起 host，行為與升級前一致
  （Edge 樣板下 `api/edge` 面存在、金鑰驗證掛載、白名單未設=全擋 403——定案10）；
  拉取模式下輪詢拉回的心跳不會停止輪詢（定案9的震盪測試）。

### 作業C｜媒體反向化
- poll request 攜帶派工（Core 端沿用既有 claim/lease，ClaimedBy 標記為 Edge 站台身分——由 poll 回應告知或設定；暫定）。
- 派工冪等：Core 已派、Edge 未完成的工作可能被重複下發（poll 回應遺失時），Edge 以 content id 去重不重複下載。
- Edge 端記憶體 work source 遵守 `IContentWorkSource` 介面完整契約（定案13：可重複掃描、租約回收語意）。
- Edge 記憶體暫存＋總量上限＋拒收背壓（拒收的派工 Core 不標 Downloading 或立即回收，硬契約：不得讓工作卡死）；
  `GET content/{id}`＋ack 釋放；Edge 重啟後 Core 靠租約逾時回收重派（測試模擬）。
- blob 取回實作定案8的 in-flight 防重、失敗重取、位元組數驗證後才 ack。
- 驗收：單檔=MaxContentBytes 可完整往返（上限反例）；取回耗時大於輪詢間隔時同一 content 只有一次傳輸
  （in-flight 測試）；模擬截斷/中斷後下一輪重取成功且最終落地完整；暫存滿時派工被拒且該工作之後仍會被重派；
  重複下發同一 id 不重複下載；ack 後記憶體釋放（暫存量歸零可觀測）；
  下載失敗回報走既有 MaxRetries 狀態機不疊新死信。

### 作業D｜Profile 反向化
- 範圍明列：群組名稱、成員名稱、群組圖片、成員頭貼四類，與現行 `ApiProfileStore`／`ProfileRefreshService`
  能更新的項目完全對等，一類都不得少。
- staleness 清單隨 poll request 下發；文字結果（群組名稱、成員名稱）隨 poll 回應上繳；
  **圖片類（群組圖片、成員頭貼）復用媒體 blob 通道**：完成清單＋獨立 GET＋ack（定案14）。
- 驗收：四類各有等價測試——與現行 `ApiProfileStore` 路徑落地結果逐欄位等價
  （比照 `IngestSinkEquivalenceTests` 的等價測試形狀）。

### 作業E｜可觀測性＋UI
- `HostHeartbeats` 加通道欄位（暫定 `Channel`＋`LastChannelSwitchAt`；migration 兩 provider 各一）；
  設定頁主機狀態顯示目前通道與最後成功時間。
- 驗收：推送模式顯示 push、輪詢模式顯示 pull；migration 在 Sqlite 實測、SqlServer script 目視。

### 作業F｜文件與部署樣板（Claude 親做）
- `DEPLOYMENT-MODES.md`（新拓撲、架構圖補反向箭頭、已知限制更新）、`DEPLOYMENT-GUIDE.md` Part E、
  README 設定表、`deploy/` 樣板新增 Pull 拓撲範例；遵守現況文件寫作紀律（不寫演變過程）。
- 安全與啟用條件明寫：啟用拉取拓撲時 Edge **必須**設定 `Ingest:AllowedClientIps` 為 Core 的 IP
  （空清單=全擋，poll 會 403）；已知限制新增「Edge=Pull 且 Core 未設 EdgeBaseUrl 時系統靜默停擺，
  啟動驗證無法跨主機偵測，靠 UI 心跳過期告警呈現」（定案15）。

### 併回前終檢
照 plan-before-dev skill：跨段產出鏈回頭 grep（poll 協定欄位 A/B/C/D 四段消費點逐一核對、
HostHeartbeats 新欄位前端消費點）、兩個獨立 Explore 審全 diff（程式碼＋文件）、
規格中途改版反向回頭核對（poll 協定最危險——四個作業共用同一協定，任一段改形狀都要回頭）。

## 風險與已知取捨

- 記憶體暫存在 Core 長時間停機時以背壓換取不掉資料：Edge 停止下載新媒體、訊息累積在 outbox（既有磁碟機制），恢復後自動排空。
- `Pull` 模式下 Edge 不主動探測，防火牆若後來改開 edge→core 不會自動升級為推送——這是使用者明示的取捨（不做多餘 retry），要自動就用 `Auto`（預設）。
- 雙向皆開通時：推送優先，Core 收到推送心跳即停輪詢，兩側自然收斂到推送模式，不會雙通道長期並跑。
- 1 秒輪詢 × 常駐：每次 poll 為小 JSON 往返＋單一在途保護，負載可忽略；`PullIntervalSeconds` 保留可調。

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A-step1 閘門與設定 | agy | 通過 | 809 綠（門檻 795）；突變測試（掛載路徑改錯）4 測試紅 | 新欄位帶了預設值 `= false`（為讓既有呼叫點免改），拿掉後編譯即揪出漏改的第三個呼叫點；順手移除未使用 using |
| A-step2 訊息與 ack | agy | 通過 | 821 綠（門檻 820）；兩處突變（移除死信過濾、ack 不刪除）皆紅 | 無落差。agy 依「反方向禁止複製」抽出 `WherePending` 共用 |
| B 輪詢器與通道狀態 | Claude | 通過 | 857 綠；自我震盪突變 3 測試紅 | agy Gemini 額度用罄（五小時 0%）中斷於前兩項，使用者決定改由 Claude 自做；規劃裡的「通道狀態機」原列在作業B，agy 中斷前未做到，補做 |
| C 媒體反向化 | Claude | 通過 | 874 綠；兩處突變（in-flight、長度驗證）皆紅 | 無 |
| D 名稱與頭貼 | Claude | 通過 | 886 綠 | **推翻定案14**：查證發現 `GroupSummary`／`MemberProfile` 本來就內含 `PictureBytes`，推送路徑早就把圖片走同一個 JSON；改用「單輪位元組預算＋第一筆必收」而非另開 blob 通道。代價是這條流沒有 ack（見下方修訂） |
| E 通道可觀測性 | Claude | 通過 | 888 綠；migration 兩 provider 各一份，Sqlite 實測 | 無 |
| F 文件與樣板 | Claude | 通過 | JSON 樣板語法驗證 | 自行發現 `AcceptedContentWork` 只有定義沒有消費點（背壓靜默），補十分鐘節流告警與 2 測試 |
| 終檢修正 | Claude | 通過 | 907 綠、零警告 | 見下方「終檢發現與處置」 |

## 終檢發現與處置

兩個獨立 Explore 各審程式碼與文件，逐項查證後處置：

| 發現 | 查證 | 處置 |
|---|---|---|
| Auto 模式下媒體與 Profile 沒有真的反向（只有訊息與心跳） | 成立，且是我設計作業C 時識別出卻延後的缺口 | 新增 `ChannelAwareContentWorkSource`／`ChannelAwareProfileStore`，依 `EdgeChannelState.UsePullResources` 二選一；補 5 個測試 |
| 一次暫時性失敗就停推 60 分鐘（違反定案5） | 成立 | 通道狀態加寬限期：失敗持續超過 `PullActivationSeconds` 才暫停，寬限期內沿用 outbox 秒級退避 |
| 通道閘門誤套到 AllInOne | 成立 | 閘門限縮到 `Mode == Edge` |
| 探測機會被空批次吃掉 | 成立 | 閘門移到取完 batch 之後，並加 `hasWorkToSend` 參數 |
| 暫存滿時把派工弄丟並燒掉重試額度 | 成立 | `TryStage` 收不下時不移除派工；容量檢查提前 |
| 派工掃描每秒一次會燒光 `MaxFailedRetries` | 成立（`Failed→Pending` 重設不受 `reclaimDownloading` 控制） | 改為「落地即派」＋全表掃描按 `RequeueIntervalMinutes` 節流 |
| blob 取回卡住每秒一次的 poll | 成立 | 拆成獨立的取回迴圈，`_inFlightContent` 因此才真正有作用 |
| Profile 工作在 poll 失敗時永久遺失 | 成立 | 失敗時放回累積集合 |
| 壞 payload 每秒一則 Error | 成立 | 十分鐘節流 |
| Pull 模式仍推心跳、空 BaseUrl 會炸 | 成立 | Pull 下不註冊 `HeartbeatService` 與兩支具名 client |
| `PullStagingMaxBytes` 驗證會擋既有部署啟動 | 成立 | 改為執行期夾到至少等於 `MaxContentBytes` ＋ 警告 |
| 非 Edge 設 `Channel`、設 `EdgeBaseUrl` 卻無 `ApiKey` 無提示 | 成立 | 前者警告、後者擋啟動 |
| 文件把租約寫成媒體重派的機制 | 成立（派工不認領，租約在這條路徑上沒作用） | 三處敘述改寫（文件兩處、程式碼註解兩處） |
| `EdgePullService.cs` 被當成二進位檔 | 成立 | 檔案裡有實體 NUL 位元組，是我寫註解時用 `U+0000 逸出序列` 逸出造成的，已移除 |

## 定案修訂

- **定案14 推翻**：圖片走 poll 回應而非獨立 blob 通道，理由見上表作業D。代價已寫進
  `DEPLOYMENT-MODES.md` 的已知限制：這條流沒有 ack，回應遺失時等 TTL 再次過期。
- **定案3 修訂**：Edge 重啟遺失暫存後靠「下一輪 poll 重新派工」補回，不是靠租約逾期——
  派工不做認領，租約只在推送方向的下載路徑上有作用。
- **定案2 修訂**：`Auto` 的暫停加了寬限期（`PullActivationSeconds`），與 Core 端接手輪詢的
  門檻同一個時點，避免短暫失敗就停推一小時。

## 體檢交接

- 實作模型：Opus 5（作業A 由 agy/gemini-3.7-flash-high 完成 step1、step2；B~F 與終檢修正由 Opus 5 親做）。
- 體檢模型：Fable 5。
- 實作方測試數：907 綠、零警告。
- 實作方自認最沒把握處：終檢修正 commit `291d0c2` 是一次性大改（ChannelAware* 切換、
  寬限期、blob 取回拆獨立迴圈、派工節流），改完只跑了全量測試，沒有被任何獨立視角看過。

## 體檢輪修正（Fable 5）

對 `dev..feature/bidir-edge-core-channel` 全 diff 體檢，重點掃終檢後手改 commit `291d0c2`
（實作方自認最沒把握處）。抓到四項，皆已修並補迴歸測試：

| 哪裡 | 症狀 | 修法 | 迴歸測試 |
|---|---|---|---|
| `HttpHeartbeatReporter` | **雙盲窗口**：心跳不經通道閘門、每分鐘照打，防火牆恢復後心跳一成功 Core 就停輪詢，但 Edge 轉送仍在暫停期等保底探測（最長 60 分）——兩邊都不動，訊息卡住最長一個探測週期 | 心跳成功即 `MarkPushSucceeded()`（失敗側刻意不通知，避免偶發失敗拖入暫停）；恢復時間從最長 60 分縮到最長 60 秒，`ChannelProbeIntervalMinutes` 降為純保底 | `HttpHeartbeatReporterTests` 2 測試 |
| `EdgePullService.PollOnceAsync` | poll 失敗時只放回 profile 派工，媒體派工候選（`_freshContentIds` 取走的）沒放回——「落地即派」斷掉，要等最長 `RequeueIntervalMinutes` 的全表掃描兜底 | `RestoreProfileWork` 擴為 `RestoreDispatch`，兩批一起放回 | `PollOnceAsync_PollFails_FreshContentDispatchIsRestored` |
| `ProcessContentQueueOnceAsync` | 取回失敗的 Warning 無節流——失敗的 id 下一輪 poll 就重新入列，持續失敗每秒一則，違反本輪自己立的日誌紀律 | 比照壞 payload 的十分鐘節流 | （日誌路徑，靠既有測試維持行為） |
| `EdgeChannelState.ShouldAttemptPush` | `hasWorkToSend` 參數是死碼：唯一正式呼叫點恆傳 true（閘門已移到非空批次之後），false 分支只有測試自己在用 | 移除參數與分支、刪對應測試（消除「測試自己造出來的物件測死分支」） | — |

另發現 PLAN 本身又混入一個實體 NUL 位元組——實作方在**描述**前一個 NUL 錯誤時，寫下的
逸出序列再次被 Python 解成實體位元組（同型錯誤二犯），已修。

體檢後測試：見文末測試數。

## 明確不做（本輪定案）

- **Edge 端多台部署**：`EdgeBaseUrl` 為單一位址，沿用「同一角色不部署兩台」的既有限制。
- **名稱／頭貼結果的 ack**：這條流維持 at-most-once（遺失等 TTL 再刷新），已寫進已知限制。
- **`GetContent` 的速率限制**：終檢觀察到的不對稱（有效金鑰可反覆拉取暫存 blob），
  攻擊面需先持有共用金鑰＋通過 IP 白名單，暫不處理；若日後開放更廣的來源再議。
- **Pull 模式下 outbox 死信積壓告警**：`OutboxForwarderService` 不註冊連帶失去每小時的
  死信計數 log；Pull 模式的死信只能由 Core 端 poll 不到（OutboxPending 持續增加）間接觀察。
  觸發條件：實際部署 Pull 拓撲且需要死信可見性時再補。
- **`AllInOne`＋`Channel=Pull` 的組合**：只記 Warning 不擋啟動（`Channel` 對非 Edge 無作用）。
