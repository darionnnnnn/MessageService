# 審查回饋第八輪（多主機租約／migration 可觀測性／索引）

基底 `dev@1175dfd`（762 綠）→ 完工併入 `dev@4e92036`＋體檢修正（781 綠）。

## 0. 範圍與定案

輸入：外部審查四項——P1 租約啟動回收是死碼、P2 migration 進度不入 log、P3-1 `MessageType` 全表索引、P3-2 假承諾 log。全做。

| 決策 | 定案 | 理由 |
|---|---|---|
| P1 根因 | ownerId 語意錯：它該代表「同機同站台」而非「行程」。改為 `MachineName + BaseDirectory 雜湊前 8 碼`；`ClaimLeaseMinutes` 預設 60→15 當補充下限 | 帶行程隨機字尾時「啟動回收自己名下」永遠不成立，崩潰孤兒等滿租約 |
| P1 代價 | IIS 重疊回收／Web Garden 下新行程的啟動掃描會接手舊行程進行中的下載（重下載一次），**資料不會壞** | SQL Server 是單一原子 INSERT；SQLite 的舊 blob handle 在列被刪後回 `SQLITE_ABORT`。上一輪終檢曾主張「blob 位元組交錯」，體檢查證寫入機制後推翻 |
| P1 補強 | 啟動回收加「認領時間早於呼叫端行程啟動時刻」條件（作業 F） | 只擋得住兄弟行程在本行程啟動後才建立的認領（Web Garden）；對重疊回收中舊行程已進行的認領無效但無害。跨機時鐘不互信，呼叫端傳「已啟動多久」由查詢端換算 |
| fencing | `RevertClaimAsync` 改 `ClaimedAt == claimTime`；`FailAsync` 維持 `ClaimedBy == ownerId` | Revert 只在 `CompleteAsync` catch 內、手上有 claimTime；FailAsync 三個呼叫點都沒有。FailAsync 在站台粒度下極端可誤標兄弟行程認領為 Failed，後果是多一次失敗計數後由 Failed 重試補跑，記為已知取捨 |
| P2 | 不放寬 `nlog.config` 的 `Microsoft.*` 規則，改在 `ILogger<Program>` 記待套用清單／耗時／已是最新 | 放寬會把每句 SQL 放進檔案 |
| P3-1 | 篩選索引 `MessageType = 'sticker'`，兩 provider 各一支 `FilterMessageTypeIndex` | 全專案唯一低基數無 filter 索引；查詢述詞須維持字面值否則被參數化不走索引 |
| P3-2 | 只改 log 分流，不拿掉 `Take` 上限 | `Take` 是記憶體／`Contains` 分批保護；「啟動迴圈掃到見底」會死迴圈（Pending 每次都被撈到） |
| 遞延 | `HostHeartbeat` 主鍵 `(Role, MachineName)` 同機兩站台互相覆蓋 | 同類身分粒度問題，待決 |

## 1. 事實核對摘要

| 項 | 判定 | 補充 |
|---|---|---|
| P1 死碼 | ✅ | ownerId 不涉及 mutex／SQLite lock／心跳；Edge 走同類別。既有測試 `..._NotDifferentOwnerOnSameMachine` 斷言「同機不同 ownerId 互不干擾」，語意變更後改寫為「不同站台」 |
| P2 | ✅ | `web.config` 註解與 `DEPLOYMENT-GUIDE.md` 兩處叫人「看 log」但看不到 |
| P3-1 | ✅ | `MessagesController` 的 `MessageType == "text"` 查詢不受影響 |
| P3-2 | ✅ | 普查其他 log 一致 |

## 2. 作業與契約

委派模型 `gemini-3.7-flash-high`（A/B/C/F），D/E Claude。

| 作業 | 契約要點 | commit | 測試 |
|---|---|---|---|
| A | `ProcessOwnerId(string? siteKey = null)`；同站台鍵相同、不同站台鍵不同；超長時截機器名保雜湊；Lease 15 | `c857e8a`（截斷方向 `dd19f1e`） | +4 |
| B | 有待套用記「共 N 個：清單」＋完成耗時，已是最新記一則；`Migrate()` 單一呼叫點；nlog 不動 | `fd31e76` | +2 |
| C | DbContext 加 filter、兩 provider migration Up drop+create／Down 還原、snapshot 同步；不改既有 migration | `a0a9abf` | +3（含真實 up/down＋資料保存） |
| D | `RequeueIntervalMinutes <= 0` 時掃描達上限記 Warning，措辭不替呼叫端斷言（拆機時掃描由 Edge 發動） | `b11a30f` | +1 |
| F | `GetPendingIdsAsync(bool, TimeSpan? startupAge, string, ct)`；啟動回收 = `ClaimedBy == ownerId && ClaimedAt < UtcNow - startupAge`；ingest API 加 `startupAgeSeconds`（`> 0` 且 ≤ 一年，否則視同沒送）並保留 `isStartup` 相容（舊 Edge 只送它 → 不做啟動回收）；`RevertClaimAsync` fencing 改 claimTime | `a21ddef` | +8 |
| E | README 設定表／欄位表、DEPLOYMENT-GUIDE 升級段＋SQL Server 篩選索引 SET 選項、DEPLOYMENT-MODES 認領段＋部署前提、web.config 註解 | `dd19f1e`、`bf86a71` | — |

## 3. 驗收抓到什麼

**終檢（併回前）**
- 文件：README 欄位表 `ClaimedBy` 仍寫「行程隨機字尾」與同檔設定表打架——「改共用概念漏改另一處讀取端」再次重演；`web.config` 註解「沒有任何線索」與新增的「進度會記在 log」自相矛盾；DEPLOYMENT-GUIDE 誤寫「三則訊息」。
- 程式碼：ownerId 超長時截掉的是雜湊（同機各站台會撞成同一個）；`BaseDirectory` 當站台鍵的兩個前提（不可開 shadow copy、兩站台不可共用實體目錄）沒記錄；Warning 措辭在拆機下說錯。

**體檢（併回後）**
- 上一輪終檢主張「fencing 保護不到 blob 位元組」是推測，查寫入機制後推翻；F 的時間守衛對重疊回收無效但無害——兩者已改寫進 §0 與 DEPLOYMENT-MODES，不再宣稱「根本排除」。
- `startupAgeSeconds` 缺上界，`1e11`／`1e15` 會 500；`0` 語意上不該回收任何東西。改 `> 0 && <= 一年`。
- FailAsync 站台粒度下的誤標窗口，記為已知取捨（程式碼 XML 註解＋DEPLOYMENT-MODES）。

## 4. 坑

- **agy 三段都在「先跑整套測試建基準」時逾時、exit 0 無摘要**，工作其實做完了——驗收一律由 Claude 自跑，不讀它的敘事。
- EF 工具重寫 snapshot 會加 BOM，不回退。
- 兩份文件講同一個機制時，改一處要 grep 另一處（本輪在 README 同一份文件內都漏了一次）。

## 5. 待人工實測

- SQLite／SQL Server 各跑一次 `FilterMessageTypeIndex` migration；SQL Server 端若有外部 ETL 寫 `GroupMessages`，確認 SET 選項。
- IIS 重疊回收一次，看 log 中「claim was reclaimed by another process or host」Warning 出現次數是否只在回收窗口內。
