# 部署指南

> 本檔只講現行版本的操作步驟；歷次規劃與審查回饋的修改歷程見 [docs/history/](history/)，
> 非必要不需要讀，避免浪費 token。

從零把 MessageService 部署上線的操作手冊，面向沒碰過這個專案的人。只講「怎麼做」；
「為什麼這樣設計」查 [README.md](../README.md)、[DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md)、
[ENCRYPTION.md](ENCRYPTION.md)。

**只有一個可發佈專案（`MessageService.Web`），只需要 `dotnet publish` 一次**——不管部署成
一台還是拆成兩三台，成品完全一樣，差別只在每台主機的 `appsettings.Production.json` 內容。

---

## 最快路徑：單機部署（AllInOne）5 步驟

多數情況（單一 Windows Server、SQLite 就夠、不需要跨主機拆機）走這條路就好，不用讀完整份
文件：

1. `dotnet publish MessageService.Web -c Release -o C:\Deploy\MessageService`
2. 複製 `deploy/appsettings.Production.AllInOne.json` 到站台目錄，改名
   `appsettings.Production.json`
3. 填三個值：`Line:ChannelSecret`、`Line:ChannelAccessToken`、`Viewer:AllowedClientIps`
4. IIS 建站台（應用程式集區選「沒有 Managed Code」）指到該目錄，跑
   `deploy\Set-AppPool.ps1 -AppPoolName "你的集區名稱" -SiteName "你的網站名稱"`
5. LINE Developers Console 的 Webhook URL 填 `https://你的網域/api/line/webhook`，按
   **Verify**

資料庫、資料表、索引都會在第一次啟動時自動建好，不用先做任何準備；SQLite 資料庫檔案預設
放在站台目錄下的 `Db\` 資料夾，也是自動建立。

拆機（兩台／三台）、SQL Server、加密請繼續往下讀完整流程；上面 5 步驟只是把 Part B～F
的預設路徑濃縮起來，遇到問題或想確認細節時對應章節都還在。

---

## 部署前要決定的三件事

1. **一台包辦（AllInOne）還是拆機？**
   - 收 webhook 的主機碰得到資料庫、單機就夠 → **AllInOne** 一台（多數情況選這個）
   - 收 webhook 的主機碰得到資料庫，但想把網頁流量隔開成獨立一台 → **AllInOne
     （`Viewer:Enabled=false`）＋獨立 `Viewer`** 兩台（硬前提：要用 SQL Server，見下一點）
   - webhook 要對外曝露在 DMZ、資料庫在內網連不到 → **Edge + Core** 兩台拆機
   - 想讓檢視端獨立一台、Core 專職資料庫與 ingest → **Edge + Core + Viewer** 三台
   - 五種角色的能力矩陣、兩台拓撲兩種切法的完整比較見 [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md)
2. **SQL Server 還是 SQLite？**
   - 正式環境、多人同時查詢 → SQL Server：填 `ConnectionStrings:SqlServer` 就好，不用另外設
     `Database:Provider`——沒填就自動用 SQLite、填了就自動用 SQL Server（見
     [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) 的 `Database:Provider` 說明）
   - 單機小規模、省成本 → SQLite（單一檔案，什麼都不用填，本文件預設值）
   - 單機部署（AllInOne）額外多一層保護：填了 SQL Server 連線字串但啟動時連不上／schema
     不對，會自動改用本機 SQLite 撐起服務（`Database:SqliteFallback`，預設開啟），不會直接
     打不開站台；拆機模式（Edge／Core／Viewer）沒有這層，設定錯誤會直接啟動失敗
3. **要不要開應用層加密？**（選用，跳到 [Part G](#part-g選用開啟應用層加密)）

## 需要準備的東西

| 項目 | 說明 |
|---|---|
| .NET 10 Hosting Bundle | IIS 主機要裝，讓 IIS 認得 ASP.NET Core Module |
| Windows Server + IIS | 或任何能跑 Kestrel 的主機 |
| SQL Server 執行個體 | 選 SQL Server 才需要 |
| LINE 官方帳號 | 用來建立 Messaging API channel |
| 對外 HTTPS 網域 + 有效憑證 | webhook 用，LINE 只接受公開可連的 HTTPS，正式環境不能用 dev tunnel |

---

## Part A：LINE Bot 建立與金鑰

不管哪種拓撲都要做，先做這步。完整教學（含疑難排解）見
[LINE-BOT-SETUP.md](LINE-BOT-SETUP.md)，這裡只列必要動作：

1. [LINE Developers Console](https://developers.line.biz/console/) 建立 Provider → 建立 Messaging API Channel
2. **Messaging API** 分頁開啟 **Allow bot to join group chats**（**必做**，不開完全收不到群組訊息）
3. （建議）**LINE Official Account features** → 停用自動回覆、停用加好友歡迎訊息
4. 取得兩把金鑰，**分別在不同分頁**：
   - **Basic settings** 分頁 → Channel secret
   - **Messaging API** 分頁 → Channel access token (long-lived) → 按 Issue
5. （建議）**Messaging API** 分頁開啟 **Webhook redelivery**（預設關閉）：讓收 webhook 的主機在
   本機 outbox 寫入失敗時，能靠 LINE 重送把事件補回來

先把兩把金鑰記下來，**不要填進 repo 內的 `appsettings.json`**（會進版控）——正式環境的值
填在 [Part C](#part-c設定站台目錄下的-appsettingsproductionjson) 提到的
`appsettings.Production.json`，那個檔案不進版控。

---

## Part B：發布程式

只需要發佈一次，成品同時服務所有拓撲：

```bash
dotnet publish MessageService.Web -c Release -o C:\Deploy\MessageService
```

每台主機都用**同一份成品**，差別只在站台目錄下各自的 `appsettings.Production.json`。

> **重佈方式注意**：SQLite 資料庫檔案預設放在站台目錄下的 `Db\` 子資料夾。上面這種
> `dotnet publish -o` 覆蓋式重佈只覆蓋程式檔、不刪多餘檔案，`Db\` 與
> `appsettings.Production.json` 都安全；但「先清空目錄再放新版」的重佈方式
> （robocopy `/MIR`、Web Deploy 同步刪除、砍資料夾重解壓）會把 `Db\` 連同所有訊息
> 一起清掉——用這類方式的話，請先照 [D3](#d3-資料夾權限) 的說明把資料庫搬到站台目錄以外。

---

## Part C：設定站台目錄下的 `appsettings.Production.json`

`deploy/` 目錄下有五份樣板，對應五種角色：

| 樣板 | 角色 | 用在哪台主機 |
|---|---|---|
| `deploy/appsettings.Production.AllInOne.json` | 單機部署 | 一台主機收 webhook＋直連資料庫＋檢視端全包 |
| `deploy/appsettings.Production.Edge.json` | 拆機：Edge | 只收 webhook，透過 ingest API 轉送給 Core |
| `deploy/appsettings.Production.Core.json` | 拆機：Core | 直連資料庫＋ingest API＋檢視端（兩台拆機時） |
| `deploy/appsettings.Production.Viewer.json` | 三台拓撲、或兩台拓撲切法 A：Viewer | 純檢視端，不收 webhook、不開 ingest API（見 [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) 的兩台拓撲兩種切法） |
| `deploy/appsettings.Production.EdgeProxy.json` | Edge 沒有合法 HTTPS 憑證時 | 借用既有憑證的對外伺服器，只把 webhook 原封轉發給 Edge（見下方 Part E1c） |

部署到某台主機時：

1. 把對應的樣板**複製**到該主機的站台目錄，**改名成 `appsettings.Production.json`**
2. 填上機密：`Line:ChannelSecret`／`ChannelAccessToken`、`Ingest:ApiKey`、連線字串
   （`ConnectionStrings:Sqlite` 或 `SqlServer`）、要加密的話還有 `Encryption:Key`
3. 填上該主機實際的白名單網段：`Viewer:AllowedClientIps`（檢視端使用者的辦公室網段）
   與／或 `Ingest:AllowedClientIps`（Edge 主機的對外 IP）——**兩個是分開的設定，語意不同，
   不要填反**
4. 三台拓撲時，Core 那台額外加一行 `"Viewer": { "Enabled": false }`，把檢視端交給獨立的
   Viewer 主機負責；兩台拓撲的切法 A（AllInOne＋獨立 Viewer）則是 AllInOne 那台加這一行，
   見 [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) 的兩台拓撲說明

`ASPNETCORE_ENVIRONMENT=Production`（已內建在 `web.config`）會讓 ASP.NET Core 自動載入
`appsettings.Production.json`，疊加在成品裡 `appsettings.json` 的開發預設值之上。這個檔案
**不在發佈成品裡**，重新部署解壓新版本時不會被覆蓋——設定天然存活於重佈之間，不需要每次
重新填一次。

> `Deployment:Mode` 的合法值是 `AllInOne`／`Edge`／`Core`／`Viewer`／`EdgeProxy`；升級前的舊部署若還在用
> `Full`／`Line`／`Db`，不用急著改，程式會自動接受並在 log 記一則提醒，但新部署一律用新名稱。

### SQL Server：建表

只有 SQL Server 需要手動建表；SQLite 由程式啟動時自動跑 migrations（見下方 D5）。

```bash
cd MessageService.sln 所在目錄
set ASPNETCORE_ENVIRONMENT=Production
dotnet ef database update --project MessageService.Data --context SqlServerMessageDbContext --startup-project MessageService.Web
```

> 之後每次改動資料庫結構都要對兩個 provider（`SqliteMessageDbContext`／
> `SqlServerMessageDbContext`）各自跑一次 `dotnet ef migrations add`——這是給熟悉 EF 的維護者
> 看的，一般部署不需要碰這個。若不想在正式主機上安裝 SDK 執行 `dotnet ef`，可以把
> `Database:AutoMigrate` 留預設值 `true`，讓應用程式啟動時自己跑 migrations（SQLite／SQL Server
> 都適用；多實例同時啟動時有具名 mutex 防止互撞，見 D4 的 IIS 常駐設定）。

> **多台直連同一顆資料庫時**（三台拓撲的 Core 與 Viewer、或同機兩站台）：兩台都會在啟動時
> 跑 migration，而那把具名 mutex **只跨行程、不跨機器**。請只讓 **Core 開
> `Database:AutoMigrate`，Viewer 設成 `false`**，由 Core 單獨負責升級 schema。
> 同機兩站台若應用程式集區身分不同，第二個站台連建立那把鎖都會被拒絕；這種情況下它會
> **跳過 migration**（不做無鎖硬跑），schema 交給拿得到鎖的那一台升級。跳過之後若資料庫
> 沒有待套用的 migration，只記一筆 Warning「本次啟動未執行 schema migration」照常啟動；
> **若真的有待套用的 migration，站台會直接啟動失敗**（例外訊息會說明落後幾個 migration）——
> 帶著舊 schema 服務只會產生一連串缺欄位錯誤，Warning 早被淹沒，寧可讓它起不來。
> 唯一例外是 SQLite 救場觸發時（那顆救場檔沒有第二個升級者），照常啟動只記 Warning。

### 既有 SQL Server 環境升級注意事項

**這一節只適用於「已經在跑正式資料的既有 SQL Server 環境」升級到新版本**——全新部署、
或還在用 SQLite 的環境不用管這段。

> **外部工具寫入 `GroupMessages` 要注意 SET 選項**：這張表從 `FilterMessageTypeIndex` 起有了
> 篩選索引（`MessageContents` 一直都有），SQL Server 要求對帶篩選索引的資料表做 DML 的連線
> 必須 `ANSI_NULLS`／`QUOTED_IDENTIFIER`／`ANSI_PADDING`／`ANSI_WARNINGS`／
> `CONCAT_NULL_YIELDS_NULL`／`ARITHABORT` 為 ON、`NUMERIC_ROUNDABORT` 為 OFF，否則寫入會拿到
> Msg 1934。應用程式自己（SqlClient）預設就對；會踩到的是外部 ETL、`sqlcmd -I` 沒開、
> 或舊的 OLEDB 工具直接往這張表寫資料的情況。

某幾次改版（例如 `SchemaHardeningRound1` 那次）的 migration 對既有欄位做了
`ALTER COLUMN`（把幾個 `nvarchar(max)` 欄位收斂成有限長度，好建索引）。SQL Server 執行
`ALTER COLUMN` 時會**整張表重寫**並持 **Sch-M（結構性）鎖**，鎖持有期間這張表完全無法讀寫；
裝著所有訊息附檔 blob 的 `MessageContentBlobs` 是全庫體積最大的一張表。既有環境的
`GroupMessages`／`MessageContents`／`MessageContentBlobs` 若已經累積相當資料量，升級前務必：

1. **先估列數**：`SELECT COUNT(*) FROM GroupMessages; SELECT COUNT(*) FROM MessageContents;`——
   幾萬列通常幾秒內完成，數百萬列以上就要認真排時段
2. **安排維護時段**：升級期間服務會整個停擺（webhook 收不到、檢視端打不開），不是背景
   悄悄進行；時段長度抓「表重寫」的量，用 blob 的
   `SELECT SUM(DATALENGTH(Content)) FROM MessageContentBlobs` 概估比列數
   準——大量媒體檔案會讓重寫時間遠超單純列數估出來的直覺
3. **確認交易紀錄檔（transaction log）空間**：`ALTER COLUMN` 整張表重寫是單一大交易，
   交易紀錄檔空間不夠會直接失敗並回滾（回滾同樣要花跟正向操作差不多的時間），升級前
   檢查可用空間、必要時先擴充或先做一次紀錄檔備份釋放空間
4. **先備份**：完整備份（見下方 Part I）加上升級前的還原點，確認備份確實可還原再開始
5. 跑 `dotnet ef database update`（見上方指令），確認新版 log 沒有錯誤後才恢復對外服務

`ALTER COLUMN` 鎖表這件事 SQLite 環境不受影響——既有檔案由 `LegacySqliteBaseliner` 一次性
橋接到 baseline，之後跟全新 SQLite migrations 走同一條路。**但下一節的 `SplitBlobTables`
兩種資料庫都適用，SQLite 環境務必看。**

### 升級到 `SplitBlobTables`：兩種資料庫都適用

**這一節 SQLite 環境也必須看**——而且 SQLite 反而是風險比較高的那一種，因為它預設是站台一
啟動就自動跑 migration，沒有人工步驟可以把關。

這個 migration 把三個 blob 欄位（`MessageContents.Content`、`Groups.PictureContent`、
`GroupMembers.PictureContent`）搬到 `MessageContentBlobs`／`GroupPictures`／`GroupMemberPictures`
三張新表，做法是「建表 → `INSERT … SELECT` 整批複製 → 刪掉舊欄位」。資料量大的環境**這是
一個很長的操作**。

共通注意事項：

- **升級當下需要約兩倍的 blob 空間**（舊欄位刪除前，同一份內容在新舊兩處各存一份），
  交易紀錄檔的用量也對應放大。空間估算在升級**前**查舊欄位：
  `SELECT SUM(DATALENGTH(Content)) FROM MessageContents`（頭貼兩張表通常小到可以忽略；
  SQLite 用 `SELECT SUM(LENGTH(Content)) FROM MessageContents`）。
- **升級完成後空間不會自己回收**：刪掉的舊欄位留下的是可重用的空頁。要真的把檔案縮小，
  SQL Server 端在確認服務正常後另外安排 `DBCC SHRINKFILE`（會造成索引碎片，記得之後重建），
  SQLite 端跑一次 `VACUUM`。兩者都建議排在維護時段而不是升級當下。
- 搬遷 SQL 三段都寫成 `NOT EXISTS`，中斷後重跑不會撞主鍵。
- 全新部署不受影響：新表從一開始就是空的，migration 的搬遷步驟不搬任何列。

#### SQLite 環境的升級步驟（照做，不要只是換 build 就重啟）

站台啟動時是**同步**跑 `Database.Migrate()` 才開始接請求的。IIS 的 ANCM 預設只給 120 秒，
超過就判定啟動失敗、回收行程，下一個請求又從頭再跑一次 migration——結果是站台永久 500.3x
的無限迴圈。本專案的 `web.config` 已經把 `startupTimeLimit` 拉到 3600 秒，但幾十 GB 的資料庫
仍然應該離線升級：

1. **停掉站台**（IIS 管理員停止站台與應用程式集區）。
2. **備份 `messages.db`**，並確認磁碟可用空間 ≥ 目前檔案大小（升級中途需要約兩倍）。
3. **離線跑 migration**（在有 SDK 的機器上，連線字串指到那顆檔案）：
   ```bash
   dotnet ef database update --project MessageService.Data --context SqliteMessageDbContext --startup-project MessageService.Web
   ```
4. 部署新版 build 並啟動站台——此時自動 migration 只是一次冪等的空操作。
5. 確認服務正常後，另外安排一次 `VACUUM` 回收空間。

**升級期間建議把 `web.config` 的 `stdoutLogEnabled` 暫時改成 `true`**，讓啟動階段（NLog 還沒
接手之前）的錯誤有地方落地；確認升級成功後再改回 `false`。

**怎麼確認 migration 是「正在跑」而不是「卡死」**：站台會把過程記在
`logs/messageservice-{日期}.log`——有待套用時記兩則（開始前的「共 N 個待套用：<名稱清單>」
與完成後的耗時），已是最新時只記一則「不需套用」。看到開始那則、還沒看到完成那則，
就是還在搬資料。EF 自己的 `Applying migration` 屬 `Microsoft.*` 類別，已被 `nlog.config`
濾掉，不要找它。

### 既有 SQLite 環境升級到新的預設資料庫路徑

**這一節只適用於「按舊版樣板部署過」的既有環境**——舊版樣板把 `messages.db`／`outbox.db`
直接指到站台目錄本身（跟 `dotnet publish -o` 的輸出目錄同一個路徑），新版樣板改成站台目錄下
的 `Db\` 子資料夾。全新部署直接用新樣板即可，不用管這段。

升級步驟：

1. 停掉站台
2. 在站台目錄下建立 `Db\` 子資料夾
3. 把既有的 `messages.db`／`messages.db-wal`／`messages.db-shm`（若存在）與
   `outbox.db`／`outbox.db-wal`／`outbox.db-shm`（若存在）搬進 `Db\`
4. 把 `appsettings.Production.json` 換成新版樣板的內容（或至少把 `ConnectionStrings`
   段落換成新版的 `Db/messages.db`／`Db/outbox.db`）
5. 啟動站台，確認 log 沒有錯誤、資料照舊完整

不搬檔案也不會啟動失敗——程式會依新的預設路徑在 `Db\` 建一個全新的空資料庫，只是舊資料
「看起來消失了」（其實還在站台目錄下的舊路徑，只是程式不再讀它），所以務必照上面的步驟搬檔案。

---

## Part D：建立 IIS 站台（每台主機都要做一次）

### D1. 安裝 IIS 與 .NET Hosting Bundle

1. Windows Server「新增角色及功能」→ 開啟 **Web 伺服器 (IIS)**，順便勾選
   **應用程式初始設定**（Application Initialization，D4 的常駐設定需要它才會真正生效）
2. 下載安裝 [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0)
3. 安裝完重啟 IIS：`net stop was /y && net start w3svc`（重載才吃得到新模組）
4. 準備好對外網域的 SSL 憑證，之後綁在 IIS 網站上（LINE 只認可信任的憑證，自簽的不行；
   純內網的 Core／Viewer 主機視情況可以只用內部憑證或純 HTTP）

### D2. 建立站台

1. IIS 管理員 → **應用程式集區** → 新增：
   - **.NET CLR 版本**：沒有 Managed Code
   - **受控管線模式**：整合式
2. IIS 管理員 → **網站** → 新增網站：
   - 實體路徑指到 `C:\Deploy\MessageService`（同一份成品，每台主機都指到這裡或各自的副本）
   - 繫結：HTTPS，port 443（或自訂），選好剛才準備的憑證
   - 套用剛建立的應用程式集區
3. 確認站台目錄下已經放好這台主機專屬的 `appsettings.Production.json`（見 Part C）

### D3. 資料夾權限

`appsettings.Production.json` 所在資料夾、`logs/` 資料夾都要給 IIS 應用程式集區的執行帳號
（預設 `ApplicationPoolIdentity`）完整讀寫權限——一般情況下站台目錄整層開好這個權限就夠，
SQLite 檔案（`messages.db`／`outbox.db`）預設就放在站台目錄下的 `Db\` 子資料夾，自動涵蓋
在內，不用另外設定；程式第一次啟動時會自動建立這個子資料夾。

只有把 `ConnectionStrings` 改指到站台目錄以外的絕對路徑時（見
[appsettings.Production.AllInOne.json](../deploy/appsettings.Production.AllInOne.json) 的
說明——通常是因為重佈方式會清空整個站台目錄），才需要額外對那個路徑授權，可以用
`Set-AppPool.ps1 -DataDirectory` 一次做掉（見 D4）。

### D4. 設定應用程式集區常駐執行（重要，容易漏掉）

IIS 應用程式集區預設**閒置 20 分鐘沒有請求就回收**、**固定跑滿 1740 分鐘就強制回收**——
這台主機上跑的背景服務（保留期清除、貼圖內容回填、outbox 排空、媒體下載、頭貼刷新）都不是靠 HTTP 請求
觸發的 `BackgroundService`，行程被回收就整個停掉，且不會有任何錯誤訊息，只是「該做的事
安靜地沒有發生」。

用 `deploy/Set-AppPool.ps1`（系統管理員身分執行）固化這幾項設定：

```powershell
.\Set-AppPool.ps1 -AppPoolName "你的集區名稱" -SiteName "你的網站名稱"
```

這支指令稿會把集區設成 `startMode=AlwaysRunning`、閒置逾時與固定間隔回收都歸零，並開啟
站台的 `preloadEnabled`。若把 SQLite 資料庫檔案搬到站台目錄以外（見 D3），加上
`-DataDirectory` 一次把目錄建立與授權都做掉：

```powershell
.\Set-AppPool.ps1 -AppPoolName "你的集區名稱" -SiteName "你的網站名稱" `
    -DataDirectory "C:\ProgramData\MessageService"
```

### D5. 啟動站台

啟動後看 `logs/messageservice-{日期}.log` 確認沒有錯誤——SQLite 環境會在這時自動跑
migrations（含既有舊檔案的一次性橋接，見 [README.md](../README.md) 的 schema 管理段）。

> log 的等級與保留天數由站台目錄下的 `nlog.config` 決定（預設保留 30 天）。程式啟動時會
> `ClearProviders()` 再掛上 NLog，所以 `appsettings.json` 的 `Logging:LogLevel` 對檔案 log
> **不生效**，要調等級請改 `nlog.config` 的 rules。行程在 NLog 初始化之前就炸掉時，唯一的
> 線索是 `logs/nlog-internal.log` 與（若有開啟）`stdoutLogEnabled` 的輸出。

三台拓撲或兩台拆機時，**建議先啟動 Core，再啟動 Edge**——Edge 的 outbox 排空會打
Core 的 ingest API，Core 沒起來的話 Edge 只是把事件暫存在本機 outbox 等待重試，不會遺失，
但先啟動 Core 可以避免一開始就看到一堆重試的 Warning log。

---

## Part E：拆機模式（Edge＋Core）的額外設定

適用情境：webhook 要對外曝露（DMZ），資料庫在內網連不到對外主機——也就是
[DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) 兩台拓撲裡的切法 B。如果選的是切法 A
（`AllInOne`＋獨立 `Viewer`），不需要這個 Part：沒有 ingest API、沒有 outbox 跨主機轉送、
沒有 `Ingest:ApiKey` 要對齊，照 Part C 的樣板說明填完就好。除了 Part D 的一般步驟，
拆機模式（Edge＋Core）還要注意：

### E1. Edge ↔ Core 的共用設定

- `Ingest:ApiKey`：**兩端必須逐字相同**，這是收發雙方的共用密鑰
- `Ingest:BaseUrl`（只有 Edge 要設）：指向 Core 主機的網域，例如 `https://core-host.example/`
- `Ingest:AllowedClientIps`（只有 Core 要設）：Edge 主機的對外 IP，不是辦公室網段

### E1b. 防火牆只開通 core→edge 時

預設（`Ingest:Channel=Auto`）不需要任何額外設定：Edge 先試推送，不通就自動改由 Core
輪詢接手；防火牆哪天開通，心跳（每分鐘一次）一送成功就自動升級回推送。要讓它運作，兩端各補一項：

- Core 端加 `Ingest:EdgeBaseUrl`，指向 Edge 主機，例如 `https://edge-host.example/`。
  站台裝在 IIS 子應用程式底下時要把那段路徑寫進來（`http://edge-host/MSLine`），
  結尾有沒有斜線都可以，程式會補齊
- **Edge 端也要設 `Ingest:AllowedClientIps`**，填 Core 主機的內網 IP。這個白名單空清單
  等於全部拒絕，沒設的話 Core 的輪詢一律吃 403

確定 edge→core 這個方向永遠不會開通時，Edge 端可以再加 `Ingest:Channel=Pull`：
不做任何主動連線與探測，`Ingest:BaseUrl` 也可以留空。反過來確定只走推送的環境設 `Push`，
Edge 就不會開放 `/api/edge` 這組端點。

設定完成後，在設定頁的「主機狀態」區塊可以看到每台主機目前走的是「推送」還是「輪詢」。
機制與完整的資料流見 [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) 的「通道方向」。

### E1c. Edge 沒有合法憑證時：借用既有的 HTTPS 入口

LINE 要求 webhook URL 是合法憑證的 HTTPS。Edge 主機在內網、沒有憑證時，不需要為它另外
申請——在公司已有合法憑證的對外伺服器上多跑一個轉發站台即可。

1. **在對外伺服器建立 application**：IIS 管理員 →既有站台上按右鍵→「加入應用程式」，
   別名填 `MSLine`（對外 URL 就是 `https://既有網域/MSLine`），實體路徑指向新的發行目錄。
   沿用既有站台的 443 繫結與憑證，不必新增網域或憑證。
2. **應用程式集區**：.NET CLR 版本選「沒有 Managed 程式碼」（ASP.NET Core 走 in-process
   裝載，不需要 CLR），其餘比照 `deploy/Set-AppPool.ps1` 的設定。
   那台若還沒跑過 ASP.NET Core 站台，需要先裝一次 .NET 10 Hosting Bundle。
3. **發行**：用**同一份** MessageService.Web 發行產物，`appsettings.Production.json`
   照 `deploy/appsettings.Production.EdgeProxy.json` 樣板填——只有兩個設定要填：
   `EdgeProxy:TargetBaseUrl` 指向 Edge（例 `http://192.0.2.10/MSLine`），
   以及視需要調整的 `EdgeProxy:TimeoutSeconds`。
4. **防火牆**：只需開通 proxy→Edge 單向。Edge 端的 `Ingest:AllowedClientIps`
   **不用為此加東西**——那份白名單保護的是 `/api/edge/*`，webhook 端點靠簽章驗證。
5. **LINE Console**：Webhook URL 改成 `https://既有網域/MSLine/api/line/webhook`，
   按 Verify 應回成功；並確認「Use webhook」開啟、建議一併開啟 redelivery
   （轉發鏈多一個節點，它短暫停機時靠 redelivery 補送）。

驗證順序（在對外伺服器上跑）：

```bash
curl -i -X POST https://既有網域/MSLine/api/line/webhook -H "Content-Type: application/json" -d "{}"
```

回 **401** 就是整條鏈都通了——proxy 轉發成功、Edge 收到並執行簽章驗證後拒絕這個沒簽章的
假請求。回 502 表示 proxy 連不到 Edge（查防火牆與 `EdgeProxy:TargetBaseUrl`）；
回 404 表示 proxy 自己的路由沒對上（查 application 別名與 URL 路徑）。

之後發一則真訊息，Edge 的 log 應出現 `Queued ... to outbox`。
proxy 本身不回報心跳，設定頁的「主機狀態」看不到它，這是預期行為。

### E1d. 讓 Edge 完全不需要對外網路（LINE outbound 也走 proxy）

E1c 只把 webhook「進來」的方向搬到 proxy；Edge 對 LINE 的 outbound（下載媒體、貼圖、
查群組／成員名稱、下載頭貼）預設仍是自己直連 internet。Edge 那台完全沒有對外網路時，
再加這兩步：

1. **Edge 端**設定：

   ```json
   "Line": {
     "OutboundVia": "EdgeProxy",
     "OutboundProxyBaseUrl": "https://既有網域/MSLine"
   }
   ```

2. **EdgeProxy 端**設定 `EdgeProxy:AllowedClientIps`，填 Edge 主機的 IP
   （**空清單＝全擋**，不填的話 Edge 的每個 outbound 都會吃 403）。

3. **防火牆要開通 `Edge → proxy` 這個方向**——注意它與 E1c 的 `proxy → Edge` **相反**，
   只開了 E1c 那個方向的環境要另外開通這一條。

驗證（在 Edge 主機上跑，`<KEY>` 換成 `Line:ChannelAccessToken`）：

```bash
curl -i -H "Authorization: Bearer <KEY>" https://既有網域/MSLine/line/api/v2/bot/info
```

回 200 並帶 bot 資訊就是整條鏈通了。回 403 表示 `EdgeProxy:AllowedClientIps` 沒把 Edge 的 IP
放進去；回 502 表示 proxy 連不到 LINE。

### E1e. Edge 設定頁（免重啟改設定）

Edge 提供一個極簡設定頁 `/edge-admin`，可以在不重啟站台的情況下改幾個常動的設定，
存檔後**立即生效**。設定值以 DPAPI（機器層級）加密後存在 `Db\edge-settings.dat`，
優先權高於 `appsettings.json`。

**開啟方式**：在 `appsettings.Production.json` 加白名單（**這個鍵只能放在這裡**）：

```json
"EdgeAdmin": { "AllowedClientIps": [ "192.0.2.50/32" ] }
```

空清單或未設定＝全擋（頁面回 403）。這個鍵刻意不能從設定頁自己改——
設錯一次就把自己鎖在門外，只能回頭改檔案救。

**能改的設定**：LINE Channel Secret／Channel Access Token、Ingest 共用金鑰、
Ingest 允許來源 IP、Webhook 來源限制（模式與允許 IP）。
其餘設定（部署模式、通道方向、各種逾時）仍在 `appsettings.json`——
它們決定啟動時要註冊哪些服務，本來就不可能熱生效。

**機密的顯示**：頁面永遠不會回傳明文，已設定的只顯示遮罩與末四碼；
要改就直接填新值，**留空表示維持原值**（不會被清成空字串）。

加密檔綁這台機器，複製到別台解不開；重佈站台時 `Db\` 目錄要一併保留
（與 `outbox.db` 同一個目錄，見上方對 `Db\` 的說明）。

### E1f. 只接受來自 EdgeProxy 的 webhook

用了 EdgeProxy 之後，Edge 的 webhook 端點仍然接受任何來源（靠簽章驗證把關）。
要再加一層縱深，可在設定頁把「Webhook 來源限制」改成 `AllowlistOnly` 並填入
EdgeProxy 的 IP——之後直接打 Edge 的 webhook 請求會被回 403，不會進到簽章驗證。

預設是 `Any`（不限制），不設定就與升級前行為完全相同。

### E2. IIS 上傳大小限制（容易漏掉的一步）

Core 端會接收 Edge 端轉來的媒體檔案上傳（最大到 `Ingest:MaxContentBytes`，預設 300MB），
但 IIS 的 `requestFiltering` 預設只放行約 28.6MB，程式碼裡調高 Kestrel 的限制擋不住 IIS
這一層。`MessageService.Web/web.config`（已進版控）已經把 `maxAllowedContentLength` 設成
`314572800`（300MB）——如果改了 `Ingest:MaxContentBytes`，記得同步改 `web.config` 這個值，
兩處要對齊。

### E3. Webhook URL 指向 Edge

LINE Console 設定的 Webhook URL 要指向**對外的 Edge 主機**，不是 Core。

### E4. 升級順序：先升 Core 再升 Edge

Edge 端的 outbox 排空預設會打 Core 的批次 ingest 端點（`POST /api/ingest/events-batch`）一次
送整批；如果 Core 還沒升級到有這支端點，Edge 會收到 404 並自動退回逐筆模式（功能不受影響，
只是吞吐量變低），同時記一則警告 log。升級順序遵照這個原則就不會踩到：先升 Core，
確認正常後再升 Edge。

---

## Part F：設定 Webhook（所有拓撲都要做）

1. 決定 URL：AllInOne 指向那台主機、拆機模式指向 Edge 主機。固定路徑：
   ```
   https://你的網域/api/line/webhook
   ```
2. LINE Console → channel → **Messaging API** 分頁 → **Webhook settings** →
   Webhook URL 貼上 → **Update**
3. 按 **Verify**，顯示 **Success** 代表網址通、Channel Secret 也對
4. **Use webhook** 切成啟用
5. 用 Bot 的 Basic ID 或 QR code，把 Bot 加入要收錄的正式 LINE 群組

---

## Part G：（選用）開啟應用層加密

要加密訊息內容與媒體檔案的話，**每一台直連資料庫的主機**（AllInOne／Core／Viewer）都要設定
`Encryption:Key`，金鑰產生指令、格式、注意事項見 [ENCRYPTION.md](ENCRYPTION.md)，這裡不重複。
核心規則只有一條：**所有直連資料庫的主機，`Encryption:Key` 必須逐字相同**，不一致的話
文字會顯示成 `ENC2:` 亂碼、媒體會整個 404（Edge 主機不直連資料庫，不需要設這個值）。
可以到設定頁「主機狀態」區塊比對各主機的金鑰指紋，不用等看到亂碼才發現設定沒對齊，
見 [ENCRYPTION.md](ENCRYPTION.md) 的「金鑰指紋」一節。

---

## Part H：部署後驗收清單

- [ ] 站台啟動無錯誤（看 `logs/messageservice-{日期}.log`）
- [ ] `GET /healthz` 與 `GET /healthz/ready` 都回 200（見下方「健康檢查端點」）
- [ ] LINE Console 的 Webhook **Verify** 顯示 Success
- [ ] Bot 加入測試群組後發一則文字，Edge／AllInOne 的 log 出現 `Saved text message`
- [ ] 檢視端網頁看得到剛才那則訊息
- [ ] 發一張圖片，幾秒內從 Pending 變成能點開看
- [ ] 用白名單外的 IP 連檢視端，應該被拒絕（驗證 `Viewer:AllowedClientIps` 真的生效）
- [ ] （拆機模式）暫時關掉 Core 端，Edge 端連續發幾則訊息，確認 webhook 仍回 200
      且沒有出現簽章錯誤；重新啟動 Core 端後，觀察 Edge 端的 outbox 自動排空、
      訊息補齊且不重複
- [ ] （拆機模式）檢查 Edge／Core 兩端的 `Line:OutboundHere` 組合：預設值（Edge=true、
      Core=false）不用動；若把媒體下載搬到 Core（Core 顯式設 true），Edge 必須同步顯式
      設 false，否則兩台會重複下載同一批媒體（Core 端啟動 log 會有一則提醒 Warning）
- [ ] **應用程式集區常駐設定已套用**（`Set-AppPool.ps1` 跑過）——打開檢視端設定頁的
      「主機狀態」區塊，確認這台主機的狀態燈是「正常」且「最後回報」在一分鐘內
      （心跳服務跟保留期清除、outbox 排空同樣是 `BackgroundService`，行程被 IIS 回收的話
      心跳會第一個停，不用像過去那樣等到隔天早上翻 log 才發現）
- [ ] 站台目錄的存取權限只開放應用程式集區帳號與管理者——`appsettings.Production.json`
      含明文機密，不該讓其他人（或其他應用程式集區）讀得到
- [ ] 重佈演練：用你實際會用的重佈方式（不是隨便解壓一份到別的資料夾）跑一次，確認
      `appsettings.Production.json` 還在、內容沒被覆蓋，**且 `Db\messages.db`／
      `Db\outbox.db`（或你改指到的絕對路徑）也還在、資料沒有遺失**——「先清空目錄再放
      新版」的重佈方式（robocopy `/MIR`、Web Deploy 同步刪除、砍資料夾重解壓）預設會把
      `Db\` 一起清掉，若你用這種方式，請先照 D3 的說明把資料庫搬到站台目錄以外
- [ ] （拆機模式）設定頁「主機狀態」區塊看得到 Edge 主機那一列（由 Core 代寫，見
      `POST /api/ingest/heartbeat`），且 outbox 積壓數是 0 或很快歸零
- [ ] （多台直連資料庫）設定頁「主機狀態」區塊裡各主機的「加密金鑰指紋」欄位一致
      （或都是 `—`）——不一致的話代表某台的 `Encryption:Key` 設定跟其他台不同，
      見 [ENCRYPTION.md](ENCRYPTION.md) 的「金鑰指紋」一節

---

## 健康檢查端點

給 IIS、負載平衡器與監控系統用的兩支端點。**所有部署模式都有**（包含沒有 UI 的 `Edge`），
而且**不受 `Viewer:AllowedClientIps` 白名單限制**——監控來源的 IP 通常不在辦公室 LAN 的
白名單裡。回應一律是空 body，不帶版本、主機名或任何設定值。

| 端點 | 語意 | 回應 |
|---|---|---|
| `GET /healthz` | 存活探針。只證明行程還在跑、還能接請求，不碰資料庫 | 恆 200 |
| `GET /healthz/ready` | 就緒探針。有資料庫的模式會 ping 一次資料庫，結果快取 5 秒 | 可連線 200；連不上 503 |

探測結果快取 5 秒（成功與失敗都快取），監控輪詢間隔低於 5 秒也不會增加資料庫連線。
會做這個快取，是因為健康檢查端點刻意排除在 IP 白名單之外，`/healthz/ready` 因此是全站
唯一一個不需任何憑證就能觸發資料庫動作的入口——對 SQL Server 每次都是一次真實連線。
失敗結果同樣快取：資料庫掛掉時若每次探測仍照打，這個保護就白做了。代價是資料庫恢復後
最多晚 5 秒才回報就緒，對監控輪詢的時間尺度無感。

`Edge` 模式沒有本機資料庫，`/healthz/ready` 直接回 200——那台主機的「就緒」本來就不依賴
資料庫，統一回 200 才能讓監控用同一份設定涵蓋所有模式。

監控設定建議：用 `/healthz` 判斷行程存活（失敗就重啟站台），用 `/healthz/ready` 判斷是否
能承接流量（失敗通常是資料庫或連線字串的問題，重啟站台無濟於事）。

---

## Part I：備份與還原

### I1. SQL Server

標準 SQL Server 備份即可（完整備份＋日誌備份，依 RPO 需求排程），沒有這個專案特有的
額外步驟。還原後直接啟動站台，`Database:AutoMigrate`（若開著）會自動確認 schema 版本
一致；若還原到的備份版本落後於目前部署的程式版本，啟動時會自動補跑缺的 migrations
（SQLite／SQL Server 都是同一套機制）。

### I2. SQLite

`messages.db`／`outbox.db` 預設放在站台目錄下的 `Db\` 資料夾（或你改指到的絕對路徑，見 D3）。
各自是**單一檔案**，但啟用 WAL 模式後（本專案預設開啟，見
[README.md](../README.md)）實際上是三個檔案一組：`messages.db`、`messages.db-wal`、
`messages.db-shm`（`outbox.db` 同理）。**只複製主檔案而漏了 `-wal`，還原出來的資料庫會
遺失最近尚未 checkpoint 的異動**——WAL 模式下最新的寫入可能還停留在 `-wal` 檔案裡，
沒有合併回主檔案。

安全備份 SQLite 的兩種做法：

1. **停機備份**：停掉站台（或至少確保沒有寫入行為）後，三個檔案（主檔＋`-wal`＋`-shm`，
   若後兩者存在）一起複製。最簡單可靠，代價是備份當下服務中斷。
2. **線上備份**（不想停機）：先執行一次 checkpoint 把 `-wal` 的內容合併回主檔案，
   再單獨複製主檔案即可（此時 `-wal`/`-shm` 即使還在也只是空殼，不複製也沒關係）：
   ```sql
   PRAGMA wal_checkpoint(TRUNCATE);
   ```
   可以透過 `sqlite3` CLI、或任何能對這個資料庫下 SQL 的工具執行；執行期間會短暫要求
   獨佔存取，跟正常讀寫爭用的機率很低但非零，避開尖峰時段執行。

還原時把備份出來的檔案放回 `ConnectionStrings` 指定的路徑，啟動站台即可。

### I3. 加密金鑰的備份（僅開啟加密時適用）

`Encryption:Key` **必須跟資料庫備份分開保管，但兩者要能同時取得**——這是這個專案備份
策略裡最容易被忽略、後果也最嚴重的一點：

- 只備份資料庫、弄丟金鑰＝**所有已加密的訊息與媒體永久無法讀取**，沒有任何復原機制
  （AES-256-GCM 沒有後門，這是設計目的）
- 只備份金鑰、弄丟資料庫備份＝金鑰本身沒有意義，但至少沒有訊息被鎖死的風險
- 兩者放在同一個地方備份（例如金鑰寫在跟資料庫備份同一個檔案伺服器上）＝**等於沒有分開
  保管的意義**，那個位置一旦外洩，資料庫備份與金鑰一起被拿走，加密形同虛設

建議：金鑰另外存進密碼管理器或密鑰保管服務，不要跟著資料庫備份的例行排程一起處理；
一次金鑰輪替或人員異動後，記得同步更新保管處的副本。

### I4. 建議的還原演練頻率

備份沒有實際還原驗證過，等同沒有備份。建議至少：

- 新環境上線時做一次「從備份完整還原」的演練，確認流程本身可行、耗時多久
- 之後每半年到一年重複一次，尤其是資料量成長之後——還原耗時會隨資料量增加，
  過時的耗時估計會讓真正需要還原時的 RTO（復原時間目標）評估失準

---

## 保留期清除與磁碟空間（SQLite）

`RetentionCleanupService` 每天依 `ViewerSettings.RetentionDays`（預設 3 年）刪除逾期訊息，
連帶 CASCADE 帶走媒體內容。**SQLite 刪資料只會把 page 標成 free，資料庫檔案不會縮小**——
釋出的空間會被之後寫入的資料重複使用，但磁碟上看到的檔案大小維持不變。三年保留期到期、
清掉數十 GB 影片之後，磁碟可用空間一個 byte 都不會回來。

清除完成且真的刪到資料時，log 會記一則 Warning 說明這件事並附上目前的資料庫檔案大小，
不會讓人以為「清過了空間就會回來」。

> 另一則要注意的 Warning：`Deleted N orphaned MessageContentBlob(s)`。刪訊息之後 blob 子列
> 是靠 `GroupMessages → MessageContents → MessageContentBlobs` 兩層 FK cascade 一起消失的，
> 清除流程每輪結束會補一句孤兒回收當安全網。**正常情況這裡永遠刪 0 列、不會有 log**；
> 出現非 0 表示 cascade 沒有作用（例如資料庫是用不建外鍵的工具還原的），要去查，
> 否則「刪了訊息但空間沒還回來」會一路靜默到硬碟滿。

要實際回收空間只能人工執行 `VACUUM`：

```bash
sqlite3 messages.db "VACUUM;"
```

執行前必須知道的三件事：

- **要停站台**。`VACUUM` 會整個重建資料庫檔案並持有寫入鎖，執行期間任何寫入都會失敗。
- **需要暫存空間**。重建過程中磁碟上會同時存在新舊兩份，至少要準備等同目前資料庫大小的
  可用空間。
- **耗時隨檔案大小成長**。數十 GB 的資料庫可能要跑很久，排在維護時段。

`PRAGMA incremental_vacuum` 不適用：它要求資料庫**建立時**就設定 `auto_vacuum=INCREMENTAL`，
既有的資料庫改不了這個屬性。

SQL Server 沒有這個問題，不需要對應的步驟。

---

## 常見部署錯誤

| 症狀 | 原因 |
|---|---|
| 網頁全部 500 | SQLite：資料夾權限不夠、migrations 沒跑成功（看 log）。SQL Server：忘記跑 `dotnet ef database update` |
| 檢視端連不進去 | `Viewer:AllowedClientIps` 沒設或 IP 不在名單內（空陣列＝全拒，是刻意設計） |
| Webhook Verify 失敗 | Channel Secret 錯、防火牆沒開對外 port、或憑證無效；完整排查見 [LINE-BOT-SETUP.md](LINE-BOT-SETUP.md#疑難排解) |
| 群組訊息完全收不到 | 沒開 Allow bot to join group chats，或沒開 Use webhook |
| 訊息卡在 Pending 不會變 Completed | Channel Access Token 錯誤／過期；拆機模式檢查 `Ingest:ApiKey` 兩端是否一致 |
| SQLite 寫入失敗（EACCES 之類） | IIS 應用程式集區帳號沒有資料夾寫入權限 |
| 拆機模式大檔案上傳失敗 | `web.config` 的 `maxAllowedContentLength` 跟 `Ingest:MaxContentBytes` 沒對齊（見 [Part E2](#e2-iis-上傳大小限制容易漏掉的一步)） |
| 加密後訊息變亂碼／媒體全部消失 | 各主機的 `Encryption:Key` 不一致 |
| 半夜的保留期清除／outbox 排空沒有動靜 | 應用程式集區被閒置逾時或固定間隔回收殺掉——確認 [Part D4](#d4-設定應用程式集區常駐執行重要容易漏掉) 的常駐設定有套用 |
| Edge 端 log 出現找不到 events-batch（404）的警告 | Core 端還沒升級到支援批次端點，功能仍正常（自動退回逐筆模式），升級 Core 後警告會自動消失 |
| 明明填了 SQL Server 連線字串，AllInOne 站台卻在用本機 SQLite | 啟動時 SQL Server 探測失敗，觸發了救場（`Database:SqliteFallback`，預設開啟）——看 log 裡的 Error 訊息找失敗原因，或打開檢視端設定頁「主機狀態」分頁看救援模式警告；修好 SQL Server 後重啟即可切回，見 [部署前要決定的三件事](#部署前要決定的三件事) |

---

## 參考文件

- [LINE-BOT-SETUP.md](LINE-BOT-SETUP.md) — LINE Bot 建立與本機測試的完整逐步教學（含疑難排解）
- [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) — AllInOne／Edge／Core／Viewer／EdgeProxy 五種角色的架構與設計理由
- [ENCRYPTION.md](ENCRYPTION.md) — 應用層加密設定
- [README.md](../README.md) — 完整設定鍵清單、資料表結構
