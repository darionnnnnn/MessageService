# 修改歷程 / 決策記錄

這個資料夾放的是點狀時間的規劃書、審查回饋、與已經併入現行文件的設計決策理由——
**不是**現行文件，讀取前請先確認真的需要（例如要知道某個設定為什麼長這樣、或要追溯
某次改版的取捨），單純想知道「現在系統長什麼樣子」請直接看 repo 根目錄的
[README.md](../../README.md) 與 `docs/` 底下的其他現行文件，不需要進來這裡，避免浪費 token。

## 目錄

| 檔案 | 內容 |
|---|---|
| `2026-08-31_OBS-1-PLAN.md` | 2026-08-31 可觀測性輪：主機狀態頁加訊息流活性訊號（`MAX(Groups.LastMessageAt)` ＋ `Monitoring:MessageSilenceWarnHours`，補「轉發鏈中斷時心跳全綠、積壓 0，畫面看不出訊息正在流失」的盲區）、加密設定檔三態與 `FileSystemWatcher`、縮放文件收斂。含「心跳加欄位改採資料面方案」的決策理由、終檢抓到的暫時性 IO 誤判為檔案毀損，以及一條被推翻重寫的部署指引（清空 appsettings 機密會讓搬機後無法從 UI 復原） |
| `2026-08-31_EDGEPROXY-1-PLAN.md` | 2026-08-31 EdgeProxy 輪：`Deployment:Mode` 第五種角色（借用既有 HTTPS 入口轉發 webhook）、LINE outbound 可改走 proxy 讓 Edge 無需對外網路、Edge 加密設定檔與熱生效、極簡設定頁、webhook 來源限制。含兩階段的定案 1~16、三次終檢（抓到 SSRF 繞過、redirect 繞過、陣列刪不掉三個高風險）與「明確不做」的觸發條件 |
| `2026-08-28_BIDIR-1-PLAN.md` | 2026-08-28 通道方向彈性化輪：Edge↔Core 四條流（訊息/心跳/媒體/名稱頭貼）支援單向防火牆任一方向、Auto 自動切換與心跳探測恢復的規劃、15+3 條定案、終檢與換模型體檢紀錄 |
| `CONSOLIDATION-PLAN.md` | 2026-08-13 部署收斂輪：兩專案合併成 `MessageService.Web`、模式改名 `AllInOne`/`Edge`/`Core`/`Viewer`、schema 改用 `Database.Migrate()` 的規劃與執行紀錄 |
| `POST-CONSOLIDATION-REVIEW-PLAN.md` | 部署收斂後的外部審查回饋輪（P0 批次 ingest bug、心跳監測、加密信封 key id 等） |
| `REVIEW-FEEDBACK-2-PLAN.md` | 部署收斂第二輪審查回饋（SQLite 路徑修正、provider 推導與救場、心跳強化等） |
| `REVIEW-FEEDBACK-3-PLAN.md` | 第三輪審查回饋：效能與體驗 16 項。含頭貼快取不改 `max-age` 的理由、SQLite Range 改用 `SqliteBlob`、頭貼刷新抑制窗口為何是 5 分鐘、以及全文檢索索引對中文不適用的實測數據 |
| `2026-08-16_REVIEW-FEEDBACK-5-PLAN.md` | 第五輪審查回饋：貼圖回填補出的內容沒人下載、下載回收改週期化、匿名代號 `(GroupId, Label)` 唯一索引與撞名重試。含委派 agy 的分段執行紀錄，以及 SqlServer 端 Label 連帶改 `nvarchar(450)` 的理由 |
| `2026-08-16_REVIEW-FEEDBACK-6-PLAN.md` | 第六輪審查回饋：三個 blob 欄位拆成獨立 1:1 資料表（`MessageContentBlobs`／`GroupPictures`／`GroupMemberPictures`）。含「為什麼修個別查詢治不了、非拆表不可」的理由、兩 provider 資料搬遷 migration 的注意事項（SQLite rowid 別名、兩倍空間）、以及體檢輪揪出的漏網頭貼查詢 |
| `2026-08-17_REVIEW-FEEDBACK-7-PLAN.md` | 第七輪審查回饋：升級路徑（SQLite baseline 橋接、SqlServer 探測階段先 migrate）與多主機同步（`ClaimedAt` 租約、fencing token、`onLockUnavailable` 改跳過、`SqliteBusyTimeoutInterceptor`、`Take` 上限與 `Contains` 分批）。含 P3b「無持久化 log」為審查誤判的查證 |
| `2026-08-17_REVIEW-FEEDBACK-8-PLAN.md` | 第八輪審查回饋：ownerId 由行程改為站台粒度（根因）＋租約 15 分鐘、migration 進度入 log、`MessageType` 篩選索引、掃描上限 Warning。含「重疊回收會不會交錯寫 blob」的兩輪相反判定與最終查證、`startupAgeSeconds` 的相容與上界、FailAsync 站台粒度誤標的已知取捨 |
| `2026-09-01_EDGEOPS-1-PLAN.md` | 部署模式改由 appsettings 檔名後綴判別（與 `Deployment:Mode` 共存、衝突擋啟動）、`/edge-admin` 三分頁化（設定／連線測試／錯誤排查）、記憶體環形緩衝與 EdgeProxy 的 `/proxy-admin/errors`。含終檢抓到的擋路級 bug（後綴模式沒寫回設定鏈，deploy 樣板剛好都寫了模式鍵而雙重掩蓋）與緩衝防灌、log 路徑分岔等修正 |
| `2026-09-01_LINEOUT-1-PLAN.md` | LINE outbound 取數診斷性修正：空 token 守門、Null 佇列出聲、失敗分類 log（401／403／DNS／路由不可達／逾時逐類）、頭貼缺圖自癒（staleness 缺圖條件＋永久／暫時失敗以 `PictureFetchedUrl` 分流）、proxy 設定熱讀、連線測試四網域化。含兩輪終檢與換模型收尾體檢的完整記錄 |
| `2026-09-01_EDGEOPS-2-PLAN.md` | edge-admin 表單與轉址改帶 `Request.PathBase` 前綴（修 IIS 子 application 下測試按鈕 404）、`OutboundTargetResolver`（DNS 取 IP、TTL 快取）、連線測試表格加「請求網址／IP」欄與失敗寫 log、outbound 失敗 log 補目標（LINE 轉發另帶 IP、內部通道只帶網址）。含 agy 委派驗收與 5 次突變測試記錄 |
| `2026-09-02_EDGEOPS-3-PLAN.md` | 單向拓撲（僅 Core→Edge）快速收斂：心跳失敗計入通道切換（安靜站台也會切換）、staleness 失敗標對內部通道並縮短冷卻、Core 保留名稱／頭貼待辦每 30 秒節流重派（上限 40 次）、媒體派工立即入列、outbox ack 競態放生降噪、連線測試逾時誤報與缺主詞修正。含體檢抓到的「`when (ex is not OperationCanceledException)` 讓 HttpClient 逾時穿出背景服務、預設 StopHost」潛在站台中止 bug 及規格寫錯的 WithHost 回歸 |
| `2026-09-02_VIEWER-1-PLAN.md` | 檢視端兩段共八個作業：側欄群組右鍵刪除群組／刪除歷史訊息（`GroupDeletionService` 逐表清理裸 GroupId 的關聯表，刪除＝重置）、訊息高亮（三張新表、八支 API、設定頁第五分頁、前端命中判定、頭貼右鍵人員規則）、字級下限 8px 與等比縮放、高亮強度與命中關鍵字粗體、頁面內全螢幕、載入更早只在頂部、跟隨模式釘底。含 agy 委派逐段驗收、兩次終檢與換模型體檢抓到的缺陷（跨批次漏清高亮規則、Bootstrap hide() 淡入中被忽略、ResizeObserver 初始回呼把載入更早拉回底部、補捲重設保護期讓使用者捲不上去、agy 寫成 Big5）與驗證環境節流限制 |
| `2026-09-03_PROFILE-1-PLAN.md` | 名稱與頭貼的即時同步與自癒：使用者回報「名稱／頭貼要重新整理才出現、群組照片永遠不出現」的十三條核對結果與根因、三段作業（資料層自癒＋背景補刷、頭貼 API 閘門與過大圖片分類與成員數、前端就地更新與 `profileResolved` 旗標）。含 agy 3.8 委派紀錄（背景服務註冊錯拓撲、無條件重拋取消例外）、兩個獨立終檢審查（匿名模式無限輪詢、GET 端點對外部輸入指派代號、補刷候選與過期判定分岔、殭屍群組餓死）、換模型體檢（`hasAvatar` 規劃落差、舊版 Core 404 洗 log）與拉取拓撲尚無補刷的定案 |
| `DEPLOYMENT-MODES-DECISIONS.md` | `docs/DEPLOYMENT-MODES.md` 移出的設計決策理由、雙行程端到端驗證紀錄、原始建置分期 |
| `WEB-UI-DESIGN-NOTES.md` | 檢視端 UI 歷次改版的設計決策理由、放棄的替代方案、已知限制 |

## 慣例

- 檔名沿用整理前的原檔名，方便對照 git log；自第五輪起搬進來時加 `YYYY-MM-DD_` 前綴（結案日），更早的檔案維持原名不追溯。
- 現行文件（README、`docs/DEPLOYMENT-*.md`、`docs/ENCRYPTION.md` 等）最上方都有一句指向這裡的提示；
  這裡的檔案不需要回頭複製現行文件的內容，只保留「為什麼」與「當時怎麼決定的」。
