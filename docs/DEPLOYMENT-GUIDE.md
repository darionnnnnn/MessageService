# 部署指南

從零把 MessageService 部署上線的操作手冊，面向沒碰過這個專案的人。只講「怎麼做」；
「為什麼這樣設計」查 [README.md](../README.md)、[DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md)、
[ENCRYPTION.md](ENCRYPTION.md)。

**只有一個可發佈專案（`MessageService.Web`），只需要 `dotnet publish` 一次**——不管部署成
一台還是拆成兩三台，成品完全一樣，差別只在每台主機的 `appsettings.Production.json` 內容。

---

## 部署前要決定的三件事

1. **一台包辦（AllInOne）還是拆機？**
   - 收 webhook 的主機碰得到資料庫 → **AllInOne**（多數情況選這個）
   - webhook 要對外曝露在 DMZ、資料庫在內網連不到 → **Edge + Core** 兩台拆機
   - 想讓檢視端獨立一台、Core 專職資料庫與 ingest → **Edge + Core + Viewer** 三台
   - 四種角色的能力矩陣見 [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md)
2. **SQL Server 還是 SQLite？**
   - 正式環境、多人同時查詢 → SQL Server
   - 單機小規模、省成本 → SQLite（單一檔案，本文件預設值）
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

---

## Part C：設定站台目錄下的 `appsettings.Production.json`

`deploy/` 目錄下有四份樣板，對應四種角色：

| 樣板 | 角色 | 用在哪台主機 |
|---|---|---|
| `deploy/appsettings.Production.AllInOne.json` | 單機部署 | 一台主機收 webhook＋直連資料庫＋檢視端全包 |
| `deploy/appsettings.Production.Edge.json` | 拆機：Edge | 只收 webhook，透過 ingest API 轉送給 Core |
| `deploy/appsettings.Production.Core.json` | 拆機：Core | 直連資料庫＋ingest API＋檢視端（兩台拆機時） |
| `deploy/appsettings.Production.Viewer.json` | 三台拓撲：Viewer | 純檢視端，不收 webhook、不開 ingest API |

部署到某台主機時：

1. 把對應的樣板**複製**到該主機的站台目錄，**改名成 `appsettings.Production.json`**
2. 填上機密：`Line:ChannelSecret`／`ChannelAccessToken`、`Ingest:ApiKey`、連線字串
   （`ConnectionStrings:Sqlite` 或 `SqlServer`）、要加密的話還有 `Encryption:Key`
3. 填上該主機實際的白名單網段：`Viewer:AllowedClientIps`（檢視端使用者的辦公室網段）
   與／或 `Ingest:AllowedClientIps`（Edge 主機的對外 IP）——**兩個是分開的設定，語意不同，
   不要填反**
4. 三台拓撲時，Core 那台額外加一行 `"Viewer": { "Enabled": false }`，把檢視端交給獨立的
   Viewer 主機負責

`ASPNETCORE_ENVIRONMENT=Production`（已內建在 `web.config`）會讓 ASP.NET Core 自動載入
`appsettings.Production.json`，疊加在成品裡 `appsettings.json` 的開發預設值之上。這個檔案
**不在發佈成品裡**，重新部署解壓新版本時不會被覆蓋——設定天然存活於重佈之間，不需要每次
重新填一次。

> `Deployment:Mode` 的合法值是 `AllInOne`／`Edge`／`Core`／`Viewer`；升級前的舊部署若還在用
> `Full`／`Line`／`Db`，不用急著改，程式會自動接受並在 log 記一則提醒，但新部署一律用新名稱。

### SQL Server：建表

只有 SQL Server 需要手動建表；SQLite 由程式啟動時自動跑 migrations（見下方 D3）。

```bash
cd MessageService.sln 所在目錄
set ASPNETCORE_ENVIRONMENT=Production
dotnet ef database update --project MessageService.Data --context SqlServerMessageDbContext --startup-project MessageService.Web
```

> 之後每次改動資料庫結構都要對兩個 provider（`SqliteMessageDbContext`／
> `SqlServerMessageDbContext`）各自跑一次 `dotnet ef migrations add`——這是給熟悉 EF 的維護者
> 看的，一般部署不需要碰這個。若不想在正式主機上安裝 SDK 執行 `dotnet ef`，可以把
> `Database:AutoMigrate` 留預設值 `true`，讓應用程式啟動時自己跑 migrations（SQLite／SQL Server
> 都適用；多實例同時啟動時有具名 mutex 防止互撞）。

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

`appsettings.Production.json` 所在資料夾、SQLite 檔案（`messages.db`／`outbox.db`）所在
資料夾、`logs/` 資料夾都要給 IIS 應用程式集區的執行帳號（預設 `ApplicationPoolIdentity`）
完整讀寫權限。

### D4. 設定應用程式集區常駐執行（重要，容易漏掉）

IIS 應用程式集區預設**閒置 20 分鐘沒有請求就回收**、**固定跑滿 1740 分鐘就強制回收**——
這台主機上跑的背景服務（保留期清除、outbox 排空、媒體下載、頭貼刷新）都不是靠 HTTP 請求
觸發的 `BackgroundService`，行程被回收就整個停掉，且不會有任何錯誤訊息，只是「該做的事
安靜地沒有發生」。

用 `deploy/Set-AppPool.ps1`（系統管理員身分執行）固化這幾項設定：

```powershell
.\Set-AppPool.ps1 -AppPoolName "你的集區名稱" -SiteName "你的網站名稱"
```

這支指令稿會把集區設成 `startMode=AlwaysRunning`、閒置逾時與固定間隔回收都歸零，並開啟
站台的 `preloadEnabled`。

### D5. 啟動站台

啟動後看 `logs/messageservice-{日期}.log` 確認沒有錯誤——SQLite 環境會在這時自動跑
migrations（含既有舊檔案的一次性橋接，見 [README.md](../README.md) 的 schema 管理段）。

三台拓撲或兩台拆機時，**建議先啟動 Core，再啟動 Edge**——Edge 的 outbox 排空會打
Core 的 ingest API，Core 沒起來的話 Edge 只是把事件暫存在本機 outbox 等待重試，不會遺失，
但先啟動 Core 可以避免一開始就看到一堆重試的 Warning log。

---

## Part E：拆機模式的額外設定

適用情境：webhook 要對外曝露（DMZ），資料庫在內網連不到對外主機。除了 Part D 的一般步驟，
拆機模式還要注意：

### E1. Edge ↔ Core 的共用設定

- `Ingest:ApiKey`：**兩端必須逐字相同**，這是收發雙方的共用密鑰
- `Ingest:BaseUrl`（只有 Edge 要設）：指向 Core 主機的網域，例如 `https://core-host.example/`
- `Ingest:AllowedClientIps`（只有 Core 要設）：Edge 主機的對外 IP，不是辦公室網段

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
- [ ] **應用程式集區常駐設定已套用**（`Set-AppPool.ps1` 跑過）——**隔天早上**檢查 log
      有沒有出現 `Retention cleanup removed ...`，確認保留期清除真的在半夜跑過，
      不是行程被 IIS 回收沒排到
- [ ] 站台目錄的存取權限只開放應用程式集區帳號與管理者——`appsettings.Production.json`
      含明文機密，不該讓其他人（或其他應用程式集區）讀得到
- [ ] 重佈演練：解壓一份新的發佈成品到同一個站台目錄，確認
      `appsettings.Production.json` 還在、內容沒被覆蓋、站台仍能正常啟動

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

---

## 參考文件

- [LINE-BOT-SETUP.md](LINE-BOT-SETUP.md) — LINE Bot 建立與本機測試的完整逐步教學（含疑難排解）
- [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) — AllInOne／Edge／Core／Viewer 四種角色的架構與設計理由
- [ENCRYPTION.md](ENCRYPTION.md) — 應用層加密設定
- [README.md](../README.md) — 完整設定鍵清單、資料表結構
