# deploy/

部署用的樣板與指令稿，不參與編譯。完整部署流程見 `docs/DEPLOYMENT-GUIDE.md`；這裡只列這個
目錄底下每個檔案的用途。

## appsettings.Production.*.json

四個拓撲各一份樣板，對應 `Deployment:Mode` 的四個值：

| 檔案 | 拓撲 | 用在哪台主機 |
|---|---|---|
| `appsettings.Production.AllInOne.json` | 單機部署 | 一台主機收 webhook＋直連資料庫＋檢視端全包 |
| `appsettings.Production.Edge.json` | 拆機：Edge | 只收 webhook，透過 ingest API 轉送給 Core |
| `appsettings.Production.Core.json` | 拆機：Core | 直連資料庫＋ingest API＋檢視端（兩台拆機時） |
| `appsettings.Production.Viewer.json` | 三台拓撲：Viewer | 純檢視端，不收 webhook、不開 ingest API |

部署到某台主機時：把對應的樣板複製到該主機的站台目錄，改名成 `appsettings.Production.json`，
填上機密（`Line:ChannelSecret`／`ChannelAccessToken`、`Ingest:ApiKey`、連線字串、
`Encryption:Key`）與該主機實際的白名單網段。這個檔案本身不進版控（見根目錄 `.gitignore`），
`ASPNETCORE_ENVIRONMENT=Production`（`web.config` 裡設的）會讓 ASP.NET Core 自動載入它、
疊加在 repo 內 `appsettings.json` 的開發預設值之上。

三台拓撲（Edge + Core + Viewer 各一台）時，Core 那台要另外在複製出來的
`appsettings.Production.json` 裡加一行 `"Viewer": { "Enabled": false }`，把檢視端交給
獨立的 Viewer 主機負責——樣板檔本身沒有內建這個 override，因為兩台／三台拓撲哪個更常見
依部署而定。

## Set-AppPool.ps1

在目標 Windows Server 上（系統管理員身分）設定 IIS 應用程式集區常駐執行，避免閒置逾時／
固定間隔回收把背景服務（保留期清除、outbox 排空、媒體下載、頭貼刷新）殺掉。用法見指令稿
內的 `.EXAMPLE`。這是 applicationHost.config 層級的設定，進不了專案裡的 `web.config`，
只能用這支指令稿或 IIS 管理員手動設定固化下來。
