# deploy/

> 修改歷程見 [../docs/history/](../docs/history/)，非必要不需要讀，避免浪費 token。

部署用的樣板與指令稿，不參與編譯。完整部署流程見 `docs/DEPLOYMENT-GUIDE.md`；這裡只列這個
目錄底下每個檔案的用途。

## appsettings.Production.*.json

四個拓撲各一份樣板，對應 `Deployment:Mode` 的四個值：

| 檔案 | 拓撲 | 用在哪台主機 |
|---|---|---|
| `appsettings.Production.AllInOne.json` | 單機部署 | 一台主機收 webhook＋直連資料庫＋檢視端全包 |
| `appsettings.Production.Edge.json` | 拆機：Edge | 只收 webhook，透過 ingest API 轉送給 Core |
| `appsettings.Production.Core.json` | 拆機：Core | 直連資料庫＋ingest API＋檢視端（兩台拆機時） |
| `appsettings.Production.Viewer.json` | 三台拓撲、或兩台拓撲切法 A：Viewer | 純檢視端，不收 webhook、不開 ingest API（見 `docs/DEPLOYMENT-MODES.md` 的兩台拓撲兩種切法） |

複製樣板、改名成 `appsettings.Production.json`、填機密的完整步驟（含三台拓撲時 Core 端
要加的 `Viewer:Enabled=false` override）見 `docs/DEPLOYMENT-GUIDE.md` 的 Part C，
這裡不重複寫一份——兩處各寫一份日後會失同步。

這些檔案帶 `//` 註解是刻意的——.NET 的設定載入器（`JsonCommentHandling.Skip`）支援帶註解的
JSON，不是格式錯誤。編輯器或 JSON 驗證工具可能會標紅，請忽略，不要「順手清乾淨」。

## Set-AppPool.ps1

在目標 Windows Server 上（系統管理員身分）設定 IIS 應用程式集區常駐執行，避免閒置逾時／
固定間隔回收把背景服務（保留期清除、貼圖內容回填、outbox 排空、媒體下載、頭貼刷新、主機心跳）殺掉。
用法見指令稿內的 `.EXAMPLE`。這是 applicationHost.config 層級的設定，進不了專案裡的 `web.config`，
只能用這支指令稿或 IIS 管理員手動設定固化下來。
