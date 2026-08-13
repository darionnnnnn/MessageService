# 部署收斂第二輪審查回饋——規劃

對照外部審查（五點需求驗收）逐項查證後的實作規劃。查證結論：審查的主要主張**全部屬實**，
另有兩處修正補充（兩台切法 A 需要 SQL Server 的硬前提；SQLite 搬路徑需要「建目錄＋ACL」配套，
`Microsoft.Data.Sqlite` 不會自動建立不存在的目錄）。

## 已定案的設計決策（與使用者討論後拍板）

1. **資料庫 provider 改推導式**：`Database:Provider` 顯式設定永遠優先；未設定時「有
   `ConnectionStrings:SqlServer` → SqlServer，否則 Sqlite」，推導結果必記 log。
2. **SQLite 救場（僅 AllInOne）**：啟動時探測 SQL Server 失敗 → 整個行程改用 SQLite 起來，
   行程存續期間不再切換；重啟時重新探測。加 `Database:SqliteFallback` 開關（預設 `true`），
   設 `false` 為嚴格模式（探測失敗即啟動失敗）。Core／Viewer 模式一律不救場。
   - 理由：執行中 SQL Server 斷線已由 outbox 緩衝保護（暫時性失敗退避重試、永不死信、有積壓
     告警），不會掉資料；真正會掉訊息的缺口是「啟動時連不上 → 站台起不來 → webhook 收不到、
     LINE redelivery 重試有限」。執行中動態切換反而製造資料分裂且需大改 EF 註冊架構，不做。
3. **SQLite 資料庫檔案預設路徑改為站台目錄下的 `Db\` 子資料夾**（相對路徑寫在
   appsettings.json），使用者可自行搬移後改 `appsettings.Production.json` 指到別處。
   殘留風險（使用者已知悉）：「清目錄式重佈」（robocopy /MIR、刪資料夾重解壓）仍會連
   `Db\` 一起清掉——由文件明確警告＋驗收清單演練條把關。
4. **心跳狀態燈判斷基準**：不動 schema，文件與樣板約定「所有主機的 `Heartbeat:IntervalSeconds`
   必須一致」。
5. **低優先項只做健保卡 `\d{12}` 預設改關閉**；MaskMiddle 下 `/api/users` 遮蔽、
   `MessagesPageDto.Truncated` 死欄位、`CompleteAsync` 認領失敗的誤導 log 本輪不做；
   blob chunk index 綁 AAD 延到下次動加密格式時一併處理。

---

## 批次 A：SQLite 預設路徑改 `Db\` 子資料夾（P1 掉資料修正）

> 先做這批：批次 B 的救場邏輯會用到這裡的路徑解析 helper。

- **A1 路徑解析 helper**：新增小工具（例如 `SqliteConnectionStringResolver`），對 Sqlite 類
  連線字串（主庫 `Sqlite`、`Outbox`、救場路徑）做兩件事：
  1. `Data Source` 是相對路徑時，以 **ContentRootPath** 為基準轉成絕對路徑——不能依賴
     CWD（IIS in-process 與 `dotnet run` 的 CWD 行為不一致，這是相對路徑方案的最大地雷）；
  2. 解析後對目錄 `Directory.CreateDirectory`（`Microsoft.Data.Sqlite` 不會自建目錄）。
  套用點：`Program.cs` 的主庫連線、outbox 連線、`LegacySqliteBaseliner`、
  `OutboxSchemaUpgrader` 各靜態方法的呼叫端（統一在 Program.cs 解析一次再往下傳，避免
  散在各處各解析一次）。
- **A2 預設值**：`appsettings.json` 預設改 `Data Source=Db\messages.db`／`Db\outbox.db`；
  程式內的 fallback 字串（`?? "Data Source=messages.db"` 兩處）同步改成 `Db\` 版本。
- **A3 四份樣板**：`ConnectionStrings` 改用同樣的相對預設值，註解改寫為：
  「預設放在站台目錄下的 `Db\` 資料夾。若你的重佈方式會清空整個站台目錄（robocopy /MIR、
  刪資料夾重解壓），請把 `Db\` 搬到站台目錄外並把這裡改成絕對路徑」。
- **A4 `Set-AppPool.ps1`**：加選用參數 `-DataDirectory`，一次做掉「建目錄＋`icacls` 授與
  `IIS AppPool\<集區名>` Modify 權限」——給搬走 `Db\` 的人用；預設路徑情境沿用 D3 的
  站台目錄權限步驟即可（D3 補一句提及 `Db\` 子資料夾）。
- **A5 文件**：
  - Part B 加「重佈方式」警告：`dotnet publish -o` 與不帶 `/MIR` 的 robocopy 是覆蓋式、
    不會刪 `Db\`；清目錄式部署必須先搬資料。
  - Part I 備份路徑改 `Db\`（含 `-wal`／`-shm` 檔）。
  - 驗收清單「重佈演練」條加「確認 `Db\messages.db`／`Db\outbox.db` 仍在且內容未遺失」。
  - 加一段「已按舊樣板部署過」的升級指引：停站台 → 搬 `*.db*` 到新路徑 → 改設定 → 起站台。

## 批次 B：provider 推導＋啟動時 SQLite 救場（需求 2）

- **B1 推導**（`Program.cs:91` 一帶）：
  ```
  顯式 Database:Provider → 照用（推導不介入）
  未設定 → 有 ConnectionStrings:SqlServer 就 SqlServer，否則 Sqlite；結果記 LogInformation
  ```
- **B2 啟動探測**：條件＝模式 AllInOne ＋ 有效 provider 為 SqlServer ＋ `Database:SqliteFallback`
  （預設 true）。時機在 **AddDbContext 之前**（provider 決定 DI 註冊，探測必須先於註冊）：
  開 `SqlConnection` ＋ 一句輕量查詢；逾時取短（例如 5 秒，避免拖慢正常啟動——連線字串本身
  的 Connect Timeout 若更短則從其）。失敗 → 有效 provider 改 Sqlite、`LogError` 記完整原因，
  救場狀態存進 singleton（例如 `DatabaseRuntimeState`：`Provider`／`FallbackActive`／
  `FallbackReason`／`FallbackSince`）。
  - 「缺資料表」情境：探測只驗連線；schema 問題由既有的 `Migrate()` 段落涵蓋——`AutoMigrate`
    開著時 migration 失敗同樣要落入救場路徑，因此 `Migrate()` 的 try 段在
    「AllInOne＋SqlServer＋救場開啟」時失敗不能直接炸，需回頭以 Sqlite 重建（實作上最簡單的
    形狀：把「探測＋migrate」合併成啟動早期的一次驗證，全部通過才用 SqlServer 註冊 DI；
    任何一步失敗即定案 Sqlite。細節實作時再定，原則是**單一決定點、決定後不變**）。
- **B3 通知（三管齊下）**：
  1. 啟動 `LogError`（含原因與「重啟時會重新探測」說明）；
  2. 本機檢視端有開 → 全站持續性 banner「目前以 SQLite 救援模式運作（SQL Server 連線失敗），
     期間資料寫入本機 Db\messages.db」；資料來源為 `DatabaseRuntimeState`，經設定 API 曝露；
  3. 設定頁「主機狀態」分頁在本機列附註目前 provider 與救場狀態（同樣讀 runtime state，
     **不動心跳表 schema**）。
  - 已知限制（文件寫明）：兩台切法 A 救場期間，心跳寫進本機 SQLite，獨立 Viewer 那台會看到
    這台變 Offline——這本身就是警訊。
- **B4 切回偵測**：以 SqlServer 正常啟動時，若偵測到 `Db\messages.db` 存在且有訊息資料 →
  `LogWarning`「偵測到救援期間累積的 SQLite 資料，尚未存在於 SQL Server」。**不做自動合併**；
  文件說明處理選項（人工匯出或接受遺留）。
- **B5 `DeploymentValidator` 調整**：
  - 原「Provider=Sqlite 但有 SqlServer 連線字串」警告改為只在**顯式**設定 Sqlite 時觸發
    （推導路徑不會出現這個組合）；
  - 新增：顯式 SqlServer 但沒有連線字串 → 啟動失敗（現況大概也會炸，改成人話錯誤）；
  - 新增：非 AllInOne 模式設了 `Database:SqliteFallback` → 警告「僅 AllInOne 有效」。
- **B6 樣板與文件**：四份樣板拿掉 `Database:Provider` 鍵，註解改寫推導規則＋救場行為＋
  嚴格模式開關；`DEPLOYMENT-MODES.md` 設定鍵一覽表更新（`Provider` 改「選填，未設定時推導」、
  新增 `SqliteFallback`）；`DEPLOYMENT-GUIDE.md` Part C 對應改寫。
  - **實作時的有意識偏離**：Viewer 樣板保留顯式 `"Provider": "SqlServer"` 沒拿掉——Viewer
    沒有救場機制，若改成推導、又不小心漏填 SqlServer 連線字串，程式會悄悄改用一顆空的本機
    SQLite 安靜啟動，檢視端看起來像「還沒有任何訊息」，比啟動失敗更難察覺；顯式設定能讓
    這種疏漏在啟動當下直接報錯擋下來（理由也寫在樣板註解裡）。
- **B7 測試**：推導矩陣（顯式×2、未設定×2）、Validator 三條新舊警告、探測失敗→Sqlite 註冊
  ＋runtime state 正確（探測器做成可注入以便測試替身）、banner API 曝露、B4 切回偵測。

## 批次 C：兩台切法 A 文件補洞（需求 1）

- `DEPLOYMENT-MODES.md` 拓撲段改寫，兩台列出兩種切法與決策依據（**兩軸**）：
  ```
  A. AllInOne(Viewer:Enabled=false) ＋ Viewer——webhook 主機碰得到資料庫、只想隔開網頁流量。
     沒有 ingest API、沒有跨主機轉送、沒有 ApiKey 要對齊，維運簡單很多。
     硬前提：必須用 SQL Server（Viewer 要直連同一顆資料庫，SQLite 跨不了主機）。
  B. Edge ＋ Core——webhook 主機在 DMZ 碰不到資料庫，或還在用 SQLite。
  沒有網段隔離需求且已用 SQL Server 時，選 A 不要選 B。
  ```
- `DEPLOYMENT-GUIDE.md` Part C 步驟 4 與 Part E 同步補這個組合的設定說明。

## 批次 D：DEPLOYMENT-GUIDE 快速路徑（需求 3）

- 文件最前面（Part A 之前）加「最快路徑：單機部署 5 步驟」區塊：publish → 複製 AllInOne
  樣板改名 → 填三個值（`Line:ChannelSecret`／`Line:ChannelAccessToken`／
  `Viewer:AllowedClientIps`）→ IIS 建站台＋跑 `Set-AppPool.ps1` → LINE Console 填 webhook URL
  按 Verify。註明資料庫與資料表第一次啟動自動建好；拆機／SQL Server／加密往下讀。

## 批次 E：心跳強化（需求 4）

- **E1 幽靈列清除**：`DELETE /api/settings/host-heartbeats/{role}/{machineName}`＋設定頁
  「主機狀態」分頁每列「移除」按鈕（二次確認）。不做自動清除（自動清除會把「離線主機」
  從畫面上抹掉，正好抹掉要看的東西）。＋controller 測試。
- **E2 ingest 心跳驗證**：`IngestController.ReportHeartbeat` 加驗證——`Role` 必須能解析為
  `DeploymentMode` 的四個正名之一、`MachineName` 非空白且長度 ≤128（與欄位 `HasMaxLength(128)`
  一致，SQLite 不會實際擋長度所以必須在這裡擋），不合回 400。＋測試。
- **E3 間隔一致約定**：四份樣板的 `Heartbeat` 段（或註解）與 `DEPLOYMENT-MODES.md` 寫明
  「所有主機的 `Heartbeat:IntervalSeconds` 必須一致，狀態燈以檢視端這台的設定為基準」。
- **E4 Edge 列語意**：主機狀態頁對 Edge 角色那列補一句說明「Edge 的心跳經由 Core 代寫，
  『離線』代表 Edge→Core 回報中斷（Core 掛掉時 Edge 可能仍在正常緩衝）」。

## 批次 F：收尾項

- **F1 Viewer 樣板權限清單**：補上 `HostHeartbeats`（HeartbeatService 每 60 秒 upsert）與
  `Groups`（`GroupsController.RecoverDriftedLastMessageAsync` 自癒路徑），並把
  `MaskKeywordGroups` 子表明列（原文只寫 MaskKeywords）。照舊清單設帳號的症狀：Viewer 每
  60 秒噴心跳錯誤、且自己在主機狀態頁顯示 Offline。
- **F2 健保卡 `\d{12}` 預設改關閉**：`ViewerSettings.MaskNhiCard` 類別預設改 `false`＋新
  migration（`UpdateData`，兩個 provider 各一份）。修正：EF 的 `HasData` 種子機制沒有「只影響
  新資料庫」這個選項，`UpdateData` 一定是對既有資料庫的該列做無條件覆寫——這點跟批次規劃時
  的預期（既有資料庫的現值不動）不同，已跟其他任何一次改欄位/種子預設值的 migration
  一致處理（照樣套用），不做特殊保留；設定頁該選項說明文字加「12 碼數字亦常見於宅配貨運
  單號，開啟前請確認群組內容性質」。＋種子測試調整（`GetPiiMaskingSettings_DefaultsToAllEnabled`
  改名並更新斷言）。
- **F3 deploy/README.md**：加一句「樣板 JSON 帶 `//` 註解是刻意的，.NET 設定載入器
  （`JsonCommentHandling.Skip`）支援；編輯器或 JSON 驗證工具報錯可忽略，請勿清掉註解」。
- **F4 Program.cs 機械拆分**（目前 496 行）：純搬家不改邏輯，拆成三個 extension method——
  DI 註冊矩陣（`AddMessageServiceCore`）、schema 遷移＋mutex（`MigrateDatabaseIfEnabled`）、
  middleware 管線（`UseMessageServicePipeline`），各自一個檔案；`Program.cs` 剩設定讀取、
  能力推導與組裝呼叫。放最後做，避免與批次 A／B 的 Program.cs 改動打架。

## 批次 G：體檢輪（完成，602 測試綠）

兩輪獨立審查（文件一致性＋程式碼正確性）＋人工複查，共抓到 3 個真問題，全部修正並補上迴歸
測試：

1. **README.md 兩處過時**：「只有這幾張表是 Web 專案會寫入的」跟同一份文件後面段落
   （HostHeartbeats／Groups 自癒寫入）自相矛盾；`MaskNhiCard` 說明仍寫「預設全開」，跟批次
   F2 改的新預設（關閉）不符。已修正兩處並補上 Groups 自癒寫入路徑的說明。
2. **AutoMigrate=false + 救場觸發＝資料庫永遠沒有資料表（真 bug，非文件問題）**：
   `Database:AutoMigrate=false` 的原意是「schema 由外部工具管理」，這個假設對執行期才決定
   存不存在的救場 SQLite 不成立——救場觸發時若仍尊重這個旗標跳過 migrate，全新的 SQLite
   檔案永遠不會有任何資料表，第一筆寫入就直接炸掉，等於救場機制在這個設定組合下完全失效。
   修法：`MigrateMessageServiceDatabase` 的閘門條件改成 `AutoMigrate || SqliteFallbackTriggered`
   （不影響沒觸發救場時的原有行為）。用先讓測試在 revert 掉修正後真的失敗（重現
   `SQLite Error 1: no such table: GroupMessages`）、修正後再過的方式確認測試有效攔截。
3. **`Enum.TryParse<DeploymentMode>` 對數字字串驗證失效**：`IngestController.ReportHeartbeat`
   的 Role 驗證用 `Enum.TryParse` 有廣為人知的陷阱——只要字串能轉成底層 int（"0"／"99"／
   "-1"）即使沒有對應具名成員也會通過，寫進 DB 的還是原始垃圾字串，違背這條驗證原本要擋
   「無上限長列」的意圖。改成直接比對 `Enum.GetNames<DeploymentMode>()`。

另外對「救場決定點的單一性」「路徑解析在 IIS in-process／`dotnet run` 下的行為」各做了兩次
獨立的發佈執行檔端到端煙霧測試（含刻意用 RFC 5737 保留位址製造真實網路逾時），均驗證正確，
沒有發現額外問題。

## 終檢輪（完成，607 測試綠）

三角度平行審查（規劃比對／程式碼正確性／文件普查）＋逐項查證，修正：

**規劃承諾的缺漏補齊**
1. B3 通知的「全站持續性 banner」與「主機狀態頁附註 provider」原本只做了設定 modal 內的
   警示——補上聊天頁頂部的 `#db-fallback-banner`（chat.js 載入時查一次 database-status，
   救場是啟動時定案的狀態不用輪詢；`body.chat-page` 改 flex column 讓 banner 與聊天版面
   共存，隱藏時版面跟原本完全一樣）與主機狀態分頁的「本機目前使用的資料庫」附註。
2. B2 探測逾時補上「夾到 5 秒內」（`SqlConnectionStringBuilder.ConnectTimeout`，只影響探測、
   不影響正式連線；連線字串顯式給更短值就從其，0=無限也一併夾掉）。
3. A5 的 Part B 重佈方式警告補上。

**審查抓到的程式碼問題**
4. `MessageServiceCoreRegistration` 與 `DatabaseStartupDecision` 的 provider 欄位三欄重複
   （欄位漂移溫床，正是這專案抓過的 bug 家族）——registration 收斂成只放 decision＋兩條
   連線字串＋AutoMigrate，「救場前的推導結果」改為 decision 的導出屬性
   `ProviderBeforeFallback`。
5. 殘留救場資料偵測（`SqliteFallbackDataDetector`）的例外會讓 SQL Server 一切正常的部署
   啟動失敗——包 try/catch 降為警告；連線加 `Pooling=False` 避免行程存續期間持有殘留檔
   handle 擋住管理者搬移。
6. `Database:Provider` 大小寫在推導點收斂成標準寫法（顯式設 `"sqlserver"` 原本會靜默落入
   Sqlite 分支且兩條驗證規則都不觸發）；無法辨認的值維持原樣。
7. PII 遮蔽的「singleton 列不存在／建構子未指定」後備共**三處**硬寫全開（SettingsController／
   MaskingService／MaskingRuleSet），健保卡預設改關後全部漂移——收斂到
   `PiiMaskingSettings.Defaults`（從 `new ViewerSettings()` 投影，跟 migration 種子同一個
   定義點）。
8. 規劃 B6 補記 Viewer 樣板保留顯式 Provider 的有意識偏離（見該條目）。

**文件**
9. README.md 兩處「預設全開」漏改、一處死引用、一處英文 typo；DEPLOYMENT-GUIDE.md 一處
   章節指涉錯誤（D3→D5）。

不修的審查意見：`Mode.ToString()` 對別名 enum 的回傳值不定（既有行為、純顯示層面、臆測性）；
`database-status` 回應含例外訊息原文（檢視端有 IP 白名單把關，banner 本來就要把失敗原因
顯示給管理者）。

## 明確不做（本輪）

- 執行中動態切換資料庫（資料分裂＋EF 架構大改）。
- 救場資料自動合併回 SQL Server（複雜度不成比例，僅偵測＋警告）。
- 心跳表加 `IntervalSeconds`／`DatabaseProvider` 欄位（以文件約定與 runtime state 取代）。
- MaskMiddle 下 `/api/users` 遮蔽、`Truncated` 死欄位、`CompleteAsync` log 修正（使用者未選）。
- blob chunk index 綁 AAD（延到下次動加密格式）。
