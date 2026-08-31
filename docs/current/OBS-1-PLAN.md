# OBS-1：訊息流活性訊號＋加密設定檔三態與監看＋縮放文件收斂

日期：2026-08-31。狀態：規劃定案，未實作。
來源：EDGEPROXY-1 結案後的整體架構審視——四個跨功能發現（全綠盲區、Web Garden 熱生效、
加密檔 fail-open、機密雙真相）與縮放矩陣查核（AllInOne＋EdgeProxy 未文件化、缺拓撲轉換表）。

## 背景與根因

1. **全綠盲區（高）**：全走 proxy 拓撲下 proxy→Edge 中斷時，proxy `/healthz` 綠、Edge 心跳綠
   且 `OutboxPending=0`、Core 綠——四燈全綠但訊息正在永久流失（LINE redelivery 有限次數）。
   根因不是心跳缺欄位，是**系統沒有「訊息流活性」的一等訊號**。
2. **Web Garden 熱生效失效（高）**：設定頁存檔→寫檔→**同行程內** `Reload()`，無檔案監看；
   而內容下載子系統已明文背書 Web Garden。多 worker 下換金鑰會新舊值交錯、間歇 401。
3. **加密設定檔 fail-open（中）**：DPAPI 綁機器；搬機後 `Read` 解不開→一行 Warning→空字典，
   Webhook 來源限制靜默退回 `Any`、輪換過的金鑰靜默退回 appsettings 舊值，設定頁看不出異狀。
4. **機密雙真相（中）**：首次部署機密必須放 appsettings（啟動驗證要求非空），改由設定頁
   接管後 appsettings 留著過時明文；「可以清掉 appsettings 該鍵（合併後仍非空）」沒有文件講。

## 定案（與使用者確認，2026-08-31）

1. **發現 1 採資料面方案，不動心跳協定**：`Groups.LastMessageAt` 在落地時即時維護，
   `MAX(Groups.LastMessageAt)` 就是「全站最後落地訊息時刻」——零 schema、零協定、
   零跨主機改動，每種拓撲（1~4 台）天生正確。定位靠與既有 `OutboxPending` 交叉：
   靜默＋積壓 0＝斷在 Edge 上游；靜默＋積壓漲＝斷在 Edge→Core。
2. **門檻預設不告警**：時刻一律顯示；新鍵 `Monitoring:MessageSilenceWarnHours`（暫定名，
   int，預設 0＝只顯示不告警），由使用者依群組活躍度自行開啟。
3. **Web Garden 止血＋根治都做**：文件明訂 Edge 站台 Worker Processes=1；
   同時給加密設定 provider 加檔案監看讓兄弟行程也能熱生效。
4. **加密檔解不開改為看得見**：三態（不存在／已載入／解不開）＋Error log＋設定頁警示橫幅；
   POST 不擋（重新存檔即重建本機可解的新檔，就是復原路徑）。不做 fail-closed。
5. **AllInOne＋EdgeProxy 文件化為支援組合**（程式已支援：路由存在、啟動驗證明文允許）。
6. **燈號／告警一律伺服器端計算**（沿用 `HostHeartbeatDto` 的既有慣例，避免用戶端時鐘漂移）；
   前端沿用既有主機狀態區塊的樣式，不做新視覺設計。

## 核對摘要（規劃前查證）

- 心跳有三條路徑（DbHeartbeatReporter 自寫／HttpHeartbeatReporter 推送／EdgeController.Poll
  拉取），原「心跳加欄位」方案要動三條＋兩個 provider migration——被資料面方案取代。
- 主機狀態 API：`GET api/settings/host-heartbeats`（SettingsController），回傳
  `HostHeartbeatDto` 陣列，Status 伺服器端算；前端在 `wwwroot/js/settings.js`＋
  `_SettingsModal.cshtml` 的主機狀態區塊。
- `Groups.LastMessageAt` 為 `DateTimeOffset?`；保留期清除後會重算，可能變 null。
- `EncryptedSettingsFile.Read` 只有 provider.Load 與 `EdgeSettingsStore.Read` 兩個消費點；
  寫入走原子 `File.Move`（會觸發 rename 事件）；`Save()` 已做同行程 reload。

## 作業總覽

委派模型：**agy（gemini-3.7-flash-high）**，整輪一種；作業C（文件）Claude 親做。
測試基準：**1027 綠**（dev@83009ea）。分支：`feature/obs-1`。

### 作業A｜訊息流活性（主機狀態頁）

**行為契約**
- 主機狀態的資料來源加上「訊息流」資訊：`lastMessageAt` = `MAX(Groups.LastMessageAt)`
  （純量查詢、`AsNoTracking`；**不得載入 Groups 實體、不得 Include 任何子實體**——
  遵守 CLAUDE.md 資料層規則）。API 形狀（既有端點改包裝或新增端點）由執行端定，
  前端於同一階段跟上。
- 新設定鍵 `Monitoring:MessageSilenceWarnHours`（int，預設 0）。
- 狀態伺服器端算，三值（名稱暫定）：
  - `None`：`lastMessageAt` 為 null（沒有任何群組、或全部群組 `LastMessageAt` 為 null，
    含保留期清除後的情況）→ 前端顯示「尚無資料」，**永不告警**；
  - `Ok`：門檻為 0，或 `now - lastMessageAt` 未超過門檻；
  - `Silent`：門檻 > 0 且 `now - lastMessageAt` 超過門檻（小時）。
- 前端主機狀態區塊顯示一列「最後收到訊息：＜時刻＞」；`Silent` 時沿用既有
  Delayed／Offline 的警示樣式標記。不新增視覺元素種類。
- 既有 `host-heartbeats` 的回傳內容與燈號規則**一位元組語意都不變**；改了回應包裝形狀時，
  前端與既有測試同段跟上。

**驗收**
- 無任何群組 → `None`，且門檻設任意值都不告警（分母為零的釘）。
- 落地一則訊息後 → `lastMessageAt` 等於該群組的 `LastMessageAt`。
- 門檻 0＋最後訊息在很久以前 → `Ok`（預設不告警的釘）。
- 門檻 1＋最後訊息 2 小時前 → `Silent`；最後訊息 30 分鐘前 → `Ok`。
- 既有主機狀態測試全綠。
- 測試總數 > 1027＋6。

### 作業B｜加密設定檔三態＋檔案監看

**B1 三態與警示橫幅**
- `EncryptedSettingsFile.Read` 的結果攜帶載入狀態，三態（名稱暫定）：
  `NotFound`（檔不存在）／`Loaded`（成功）／`Unreadable`（檔存在但解密或反序列化失敗）。
  三態下的**生效值行為都與現行完全相同**（NotFound／Unreadable 皆退回 appsettings）。
- `Unreadable` 時 log 等級改 **Error**（現為 Warning），訊息含路徑與「常見原因：搬機或
  還原映像；重新在設定頁存檔即可重建」。
- provider 與 `EdgeSettingsStore` 暴露最後一次載入的狀態；`/edge-admin` GET 在
  `Unreadable` 時於頁面頂端顯示警示橫幅（文案含：目前生效的是 appsettings 的值、
  重新填寫並存檔即可重建）。`NotFound` 不顯示橫幅（首次使用是正常狀態）。
- POST 不擋：存檔覆寫產生本機可解的新檔，即復原路徑。

**B2 檔案監看（兄弟行程熱生效）**
- provider 監看 `edge-settings.dat`（Created／Changed／Renamed；debounce 暫定 0.5~2 秒，
  執行端依實測定），觸發後重讀並發 change token。`Save()` 的同行程即時 reload 保留不變
  （watcher 是兄弟行程的補充，不是取代）。
- 監看目標檔在啟動時不存在也要能運作（監看目錄、等檔案出現）。
- 監看回呼裡讀到壞檔（寫入方非原子、或競態）→ 依三態規則退回 appsettings 並記 Error，
  **不得拋出未處理例外**（背景回呼的例外會殺行程）。

**驗收**
- 壞檔（隨機 bytes）啟動 → 站台正常啟動、生效值＝appsettings、GET `/edge-admin`
  回應含橫幅文案子字串、log 有 Error；POST 存檔後檔案可正常讀回、再 GET 無橫幅。
- 檔不存在 → 無橫幅、無 Error。
- **兄弟行程模擬**：測試繞過 `EdgeSettingsStore`、直接以 `EncryptedSettingsFile.Write`
  寫檔（模擬另一個行程）→ 在上限秒數內輪詢等待 `IOptionsMonitor.CurrentValue`
  反映新值（避免依賴精確時序的 flaky 斷言）。這條是 B2 的核心驗收，
  **不可用同行程 `Save()` 代替**（那條路徑不經過 watcher）。
- 監看期間寫入壞檔 → 生效值退回 appsettings、行程不死。
- 既有 EncryptedSettings／EdgeAdmin／熱生效測試全綠。
- 測試總數 > 作業A 結束數＋8。

### 作業C｜文件收斂（Claude 親做）

- `DEPLOYMENT-GUIDE.md` E1e：補「用設定頁接管某機密後，建議清掉 appsettings 同名鍵
  （合併後仍非空，啟動驗證會通過）」與「首次啟動前機密仍需放 appsettings，站台起來後
  改由設定頁管理」兩句；補「Edge 站台不可啟用 Web Garden（Maximum Worker Processes
  保持 1）」（若 B2 落地，改寫為「已支援多 worker 熱生效，但仍建議保持 1」——依實作結果定稿）。
- AllInOne＋EdgeProxy 組合文件化：`DEPLOYMENT-MODES.md` 拓撲組合處補一列；
  `DEPLOYMENT-GUIDE.md` E1c 補一句「`TargetBaseUrl` 也可指向 AllInOne 主機」。
- `DEPLOYMENT-GUIDE.md` 新增「拓撲轉換」小節：AllInOne→＋Viewer（SQL Server 硬前提）、
  AllInOne→Edge＋Core、加／拆 EdgeProxy、`Line:OutboundVia` 切換需重啟 Edge——每條列
  要動的設定與順序，一條一兩行即可。
- `README.md` 設定表加 `Monitoring:MessageSilenceWarnHours`。
- `DEPLOYMENT-MODES.md` 已知限制更新：「EdgeProxy 不回報心跳」條目補「訊息流靜默由
  主機狀態頁的最後收到訊息時刻涵蓋」；範例位址守 RFC 5737。

### 併回前終檢

- 跨段 grep：`Monitoring:MessageSilenceWarnHours` 的消費點與 README 一致；作業A 新增的
  API 欄位有前端消費點；橫幅文案子字串測試與實作一致。
- NUL／BOM 掃描（PowerShell 位元組掃描，不用 bash `$'\x00'`——本輪已踩過假警報）。
- 兩個獨立 Explore 各審程式碼與文件全 diff 後才併 dev。

## 明確不做（本輪定案，附觸發條件）

- **心跳協定加 `LastWebhookAt`**：資料面方案以零 schema 達成偵測＋定位。
  觸發條件：需要在 `OutboxPending` 之外更細分「Edge 收到但未落地」的粒度時。
- **加密檔狀態進心跳／主機狀態頁**：橫幅＋Error log 已覆蓋主要情境。
  觸發條件：Edge 台數多到逐台開設定頁檢查不切實際時。
- **安全鍵 fail-closed（解不開就擋啟動）**：違背可用性優先，會把「搬機」變成「webhook 中斷」。
- 既有 BACKLOG 項目（CSRF、DPAPI entropy、LAN 段加密等）維持不變。

## 風險與已知取捨

- 靜默告警的誤報風險由「預設 0＝不告警」承擔；使用者開啟後的門檻選擇是其自己的
  活躍度知識，程式不猜。
- `FileSystemWatcher` 行為有平台差異；本專案部署面為 Windows，驗收以 Windows 為準。
- 保留期清除可能讓 `lastMessageAt` 變 null（極端：全部訊息過期）——落入 `None` 態
  顯示「尚無資料」，屬正確行為，規格已涵蓋。

## 規劃完成後複檢

- 與既有行為衝突：作業A 若改 `host-heartbeats` 回應包裝，前端與既有測試同段跟上（契約已寫明）；
  燈號慣例沿用伺服器端計算，無矛盾。作業B 三態下生效值行為不變（契約明寫），與
  「設定損毀不擋啟動」的既有決策一致。
- 批次間衝突：A（SettingsController／settings.js）與 B（Configuration/）無共同檔案；
  C 的 Web Garden 文案依 B2 結果定稿——順序上 C 排在 B 之後執行。
- 四個坑：分母為零（`None` 態）已釘；無破壞性判準；無單向閘門；無移除類。
- 升級／既有資料：A 零 schema；B 對既有 `edge-settings.dat` 只讀不遷移。
- **複檢完成，除上述已寫入之調整外無新增事項。**

## 執行紀錄

| 作業-階段 | 執行者 | 結果 | 驗收 | 落差與處置 |
|---|---|---|---|---|
| A 訊息流活性 | agy | | | |
| B 加密設定檔三態＋監看 | agy | | | |
| C 文件收斂 | Claude | | | |
| 終檢 | Claude | | | |
