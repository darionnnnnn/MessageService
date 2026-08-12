# 部署指南

從零把 MessageService 部署上線的操作手冊，面向沒碰過這個專案的人。只講「怎麼做」；
「為什麼這樣設計」查 [README.md](../README.md)、[DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md)、
[ENCRYPTION.md](ENCRYPTION.md)。

---

## 部署前要決定的三件事

1. **Full 模式（單機）還是 Line/Db 模式（跨主機）？**
   - 收 webhook 的主機碰得到資料庫 → **Full**（多數情況選這個）
   - webhook 要對外曝露在 DMZ、資料庫在內網連不到 → **Line/Db 拆機**
2. **SQL Server 還是 SQLite？**
   - 正式環境、多人同時查詢 → SQL Server
   - 單機小規模、省成本 → SQLite（單一檔案，寫入序列化，本文件兩種都會教）
3. **要不要開應用層加密？**（選用，跳到 [Part G](#part-g-選用開啟應用層加密)）

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

兩種部署方式都要做，先做這步。完整教學（含疑難排解）見
[LINE-BOT-SETUP.md](LINE-BOT-SETUP.md)，這裡只列必要動作：

1. [LINE Developers Console](https://developers.line.biz/console/) 建立 Provider → 建立 Messaging API Channel
2. **Messaging API** 分頁開啟 **Allow bot to join group chats**（**必做**，不開完全收不到群組訊息）
3. （建議）**LINE Official Account features** → 停用自動回覆、停用加好友歡迎訊息
4. 取得兩把金鑰，**分別在不同分頁**：
   - **Basic settings** 分頁 → Channel secret
   - **Messaging API** 分頁 → Channel access token (long-lived) → 按 Issue
5. （建議）**Messaging API** 分頁開啟 **Webhook redelivery**（預設關閉）：讓收錄端在本機
   outbox 寫入失敗時，能靠 LINE 重送把事件補回來

先把兩把金鑰記下來，**不要填進 `appsettings.json`**（會進版控）。

---

## Part B：資料庫設定

### B1. SQL Server

1. 建一個空白資料庫（名稱自訂，例如 `MessageService`）
2. 準備連線字串，例如：
   ```
   Server=your-sql-host;Database=MessageService;User Id=...;Password=...;TrustServerCertificate=True
   ```
3. 建表（見下方「建表指令」）——**只要在一台能連到這顆資料庫的機器上跑一次**即可

### B2. SQLite

1. 不用另外裝資料庫服務。**只有收錄端**啟動時會在執行目錄自動建立 `messages.db`
   並補齊最新欄位／索引，檢視端完全不建表
2. **部署或升級順序永遠先動收錄端、再動檢視端**：順序顛倒的話，檢視端連到還沒補
   欄位的 `messages.db`，對話頁、側欄、搜尋、設定會全部 500
3. `messages.db` 所在資料夾要給 IIS 應用程式集區的執行帳號（預設是
   `ApplicationPoolIdentity`）完整讀寫權限，否則寫不進去

### 建表指令（只有 SQL Server 要手動跑，SQLite 是自動的）

```bash
cd MessageService.sln 所在目錄
set ASPNETCORE_ENVIRONMENT=Production
dotnet ef database update --project MessageService.Data --startup-project MessageService
```

> 一定要設 `ASPNETCORE_ENVIRONMENT=Production`，否則 `dotnet ef` 會讀到開發環境設定
> 套用 SQLite 語法。

---

## Part C：發布程式

兩個專案都要各自 `publish` 成獨立資料夾：

```bash
dotnet publish MessageService -c Release -o C:\Deploy\MessageService
dotnet publish MessageService.Web -c Release -o C:\Deploy\MessageService.Web
```

---

## Part D：部署方式一 — Full 模式（單機）

收 webhook 的主機同時碰得到資料庫。收錄端與檢視端可以放同一台，也可以分開放，
只要都連得到同一顆資料庫即可。

### D1. 安裝 IIS 與 .NET Hosting Bundle

1. Windows Server「新增角色及功能」→ 開啟 **Web 伺服器 (IIS)**
2. 下載安裝 [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0)
3. 安裝完重啟 IIS：`net stop was /y && net start w3svc`（重載才吃得到新模組）
4. 準備好對外網域的 SSL 憑證，之後綁在 IIS 網站上（LINE 只認可信任的憑證，自簽的不行）

### D2. 建立收錄端站台

1. IIS 管理員 → **應用程式集區** → 新增：
   - **.NET CLR 版本**：沒有 Managed Code
   - **受控管線模式**：整合式
2. IIS 管理員 → **網站** → 新增網站：
   - 實體路徑指到 `C:\Deploy\MessageService`
   - 繫結：HTTPS，port 443（或自訂），選好剛才準備的憑證
   - 套用剛建立的應用程式集區

### D3. 設定收錄端正式環境變數

編輯站台目錄下的 `web.config`，在 `<aspNetCore>` 節點內加：

```xml
<aspNetCore ...>
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="Line__ChannelSecret" value="你的 channel secret" />
    <environmentVariable name="Line__ChannelAccessToken" value="你的 channel access token" />
    <environmentVariable name="ConnectionStrings__SqlServer" value="你的連線字串" />
    <environmentVariable name="Database__Provider" value="SqlServer" />
  </environmentVariables>
</aspNetCore>
```

> 用 SQLite 的話拿掉 `ConnectionStrings__SqlServer` 那行、把 `Database__Provider` 改
> `Sqlite`（或直接不設，SQLite 只是開發環境的預設，正式環境要顯式指定）。

### D4. 建立檢視端站台

同 D2 手法，實體路徑改 `C:\Deploy\MessageService.Web`，另開一個 port／子網域。

### D5. 設定檢視端正式環境變數

```xml
<environmentVariables>
  <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
  <environmentVariable name="ConnectionStrings__SqlServer" value="跟收錄端同一顆資料庫" />
  <environmentVariable name="Database__Provider" value="SqlServer" />
  <environmentVariable name="AllowedClientIps__0" value="你們公司內網網段，例如 10.1.0.0/24" />
</environmentVariables>
```

**`AllowedClientIps` 一定要設**——檢視端沒有登入機制，這是唯一防護，留空等於
誰都連不進去（是刻意的安全預設，不是 bug）。要放行多個 IP／網段就依序加
`AllowedClientIps__1`、`__2`……

### D6. 啟動順序

先啟動收錄端站台（SQLite 環境會自動建表／升級），確認 log 正常後再啟動檢視端站台。

---

## Part E：部署方式二 — Line/Db 拆機模式

適用情境：webhook 要對外曝露（DMZ），資料庫在內網連不到對外主機，兩者分開部署。

```
LINE ──▶ [Line 端主機｜對外 DMZ]  只跑收錄端，不連資料庫
              │ 轉送事件
              ▼
        [Db 端主機｜內網]  收錄端 + 檢視端，連得到資料庫
```

### E1. Db 端設定

1. 照 D1／D2／D3 建立收錄端站台，環境變數在 D3 的基礎上再加：
   ```xml
   <environmentVariable name="Deployment__Mode" value="Db" />
   <environmentVariable name="Ingest__ApiKey" value="自訂一串夠長的隨機字串" />
   <environmentVariable name="AllowedClientIps__0" value="Line 端主機的對外 IP" />
   ```
   `Ingest:ApiKey` 兩端必須逐字相同，這是收發雙方的共用密鑰。
2. 照 D4／D5 建立檢視端站台，跟 Full 模式完全一樣

**IIS 上傳大小限制（容易漏掉的一步）**：Db 端會接收 Line 端轉來的媒體檔案上傳
（最大到 `Ingest:MaxContentBytes`，預設 300MB），但 IIS 的 `requestFiltering`
預設只放行約 28.6MB，程式碼裡調高 Kestrel 的限制擋不住 IIS 這一層。
要在收錄端站台的 `web.config` 加：

```xml
<system.webServer>
  <security>
    <requestFiltering>
      <requestLimits maxAllowedContentLength="314572800" />
    </requestFiltering>
  </security>
</system.webServer>
```

（`314572800` = 300MB，要跟 `Ingest:MaxContentBytes` 對齊。）

### E2. Line 端設定

照 D1／D2 建立收錄端站台（**這台不用建檢視端**），環境變數：

```xml
<environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
<environmentVariable name="Deployment__Mode" value="Line" />
<environmentVariable name="Ingest__BaseUrl" value="https://db端主機網域/" />
<environmentVariable name="Ingest__ApiKey" value="跟 Db 端相同的值" />
<environmentVariable name="Line__ChannelSecret" value="同 Part A 取得的值" />
<environmentVariable name="Line__ChannelAccessToken" value="同 Part A 取得的值" />
<environmentVariable name="Line__OutboundHere" value="true" />
```

`Line:OutboundHere=true` 表示媒體下載／頭貼快取由這台對外主機負責打 LINE API
（通常設 true，因為這台本來就對外）。**這台不需要任何資料庫連線字串。**

### E3. Webhook URL 指向 Line 端

LINE Console 設定的 Webhook URL 要指向**對外的 Line 端主機**，不是 Db 端。

---

## Part F：設定 Webhook（兩種部署方式都要做）

1. 決定 URL：Full 模式指向收錄端主機、拆機模式指向 Line 端主機。固定路徑：
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

要加密訊息內容與媒體檔案的話，**收錄端與檢視端都要設定**，金鑰產生指令、格式、
注意事項見 [ENCRYPTION.md](ENCRYPTION.md)，這裡不重複。核心規則只有一條：
**兩邊的 `Encryption:Key` 必須逐字相同**，不一致的話文字會顯示成 `ENC1:` 亂碼、
媒體會整個 404。

---

## Part H：部署後驗收清單

- [ ] 收錄端啟動無錯誤（看 `logs/messageservice-{日期}.log`）
- [ ] LINE Console 的 Webhook **Verify** 顯示 Success
- [ ] Bot 加入測試群組後發一則文字，收錄端 log 出現 `Saved text message`
- [ ] 檢視端網頁看得到剛才那則訊息
- [ ] 發一張圖片，幾秒內從 Pending 變成能點開看
- [ ] 用白名單外的 IP 連檢視端，應該被拒絕（驗證 `AllowedClientIps` 真的生效）
- [ ] （拆機模式）暫時關掉 Db 端，Line 端連續發幾則訊息，確認 webhook 仍回 200
      且沒有出現簽章錯誤；重新啟動 Db 端後，觀察 Line 端的 outbox 自動排空、
      訊息補齊且不重複
- [ ] 隔天檢查 `RetentionCleanupService` 的排程 log 有沒有正常跑

---

## 常見部署錯誤

| 症狀 | 原因 |
|---|---|
| 網頁全部 500 | SQLite：忘記先啟動收錄端做 schema 升級。SQL Server：忘記跑 `dotnet ef database update` |
| 檢視端連不進去 | `AllowedClientIps` 沒設或 IP 不在名單內（空陣列＝全拒，是刻意設計） |
| Webhook Verify 失敗 | Channel Secret 錯、防火牆沒開對外 port、或憑證無效；完整排查見 [LINE-BOT-SETUP.md](LINE-BOT-SETUP.md#疑難排解) |
| 群組訊息完全收不到 | 沒開 Allow bot to join group chats，或沒開 Use webhook |
| 訊息卡在 Pending 不會變 Completed | Channel Access Token 錯誤／過期；拆機模式檢查 `Ingest:ApiKey` 兩端是否一致 |
| SQLite 寫入失敗（EACCES 之類） | IIS 應用程式集區帳號沒有資料夾寫入權限 |
| 拆機模式大檔案上傳失敗 | 忘記調高 Db 端 IIS 的 `maxAllowedContentLength`（見 [Part E1](#e1-db-端設定)） |
| 加密後訊息變亂碼／媒體全部消失 | 收錄端與檢視端的 `Encryption:Key` 不一致 |

---

## 參考文件

- [LINE-BOT-SETUP.md](LINE-BOT-SETUP.md) — LINE Bot 建立與本機測試的完整逐步教學（含疑難排解）
- [DEPLOYMENT-MODES.md](DEPLOYMENT-MODES.md) — Full／Line／Db 三種模式的架構與設計理由
- [ENCRYPTION.md](ENCRYPTION.md) — 應用層加密設定
- [README.md](../README.md) — 完整設定鍵清單、資料表結構
