# MessageService

LINE bot webhook 訊息收錄服務。bot 加入群組後，透過 LINE webhook 接收群組內的訊息並寫入資料庫。

本專案職責只有三件事：**收 webhook → 落地資料庫 → 每日清除逾期資料**。不提供查詢 API、不做 UI，資料表 schema 設計上假設會有其他應用程式直接連同一顆資料庫讀取顯示。

## 架構

```
LINE Platform ──POST──▶ LineWebhookController
                            │ 1. 驗證 X-Line-Signature（HMAC-SHA256）
                            │ 2. 反序列化 webhook events
                            ▼
                     WebhookEventHandler
                            │ 過濾群組訊息、防重送、依型別分流
                            ▼
                ┌── GroupMessages / MessageContents(Pending) ──┐
                │                                              │
                ▼ 入列                                          │
     ContentDownloadQueue（Channel）                            │
                ▼                                              ▼
     ContentDownloadService（背景下載原檔）          RetentionCleanupService
       影片先等 LINE 轉檔，失敗重試，                  （每日刪除超過保留年限的訊息，
        完成後寫回 MessageContents）                   內容檔案靠 CASCADE 一併刪除）
```

## 訊息型別處理

| 型別 | Text 欄位 | 內容下載 |
|---|---|---|
| 文字 | 訊息內文 | — |
| 貼圖 | `(貼圖)` | — |
| 圖片 | null | 原圖（非縮圖），背景下載 |
| 影片 | null | 原檔，先輪詢 LINE transcoding 完成才下載 |
| 語音 | null | 原檔，同影片先等 transcoding 完成才下載 |
| 檔案 | null（檔名存於內容表） | 原檔，背景下載 |
| 其他（位置等） | 略過不存 | — |

## 資料表

**GroupMessages**

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| WebhookEventId | nvarchar(450) | LINE 事件 ID，唯一索引（防 redelivery 重複寫入） |
| LineMessageId | nvarchar(max) | LINE 的 message id |
| GroupId | nvarchar(max) | 來源群組 |
| UserId | nvarchar(max), null | 發言者（未加 bot 好友時可能為 null） |
| MessageType | nvarchar(max) | text / sticker / image / video / file |
| Text | nvarchar(max), null | 文字內容或 `(貼圖)` |
| EventTimestamp | datetimeoffset | LINE 事件時間 |
| ReceivedAt | datetimeoffset | 本服務收到時間 |

**MessageContents**（1:1 對 GroupMessages，ON DELETE CASCADE）

| 欄位 | 型別 | 說明 |
|---|---|---|
| Id | bigint PK | |
| GroupMessageId | bigint FK | |
| FileName | nvarchar(max), null | 檔案訊息的原始檔名 |
| ContentType | nvarchar(max), null | 下載完成後的 MIME type |
| DownloadStatus | nvarchar(max) | Pending / Completed / Failed |
| Content | varbinary(max), null | 原始檔案內容，Pending/Failed 時為 null |
| CompletedAt | datetimeoffset, null | 下載完成時間 |

外部應用程式可依 `DownloadStatus` 呈現三種狀態：抓取中（Pending）、可檢視（Completed）、抓取失敗（Failed）。若要修改欄位，注意這是外部應用程式讀取的既有 schema，異動需評估相容性。

## 設定

| 設定鍵 | 說明 |
|---|---|
| `Database:Provider` | `SqlServer`（正式，appsettings.json 預設）或 `Sqlite`（開發，appsettings.Development.json 預設） |
| `ConnectionStrings:SqlServer` / `Sqlite` | 連線字串 |
| `Line:ChannelSecret` | 簽章驗證用。**勿進版控**，開發用 user-secrets、正式用環境變數 |
| `Line:ChannelAccessToken` | 內容下載 API 用，同上 |
| `Retention:Years` | 保留年限（預設 3） |
| `Retention:CleanupTimeOfDay` | 每日清除時間（本地時間，預設 03:00:00） |
| `ContentDownload:MaxRetries` | 下載重試次數（預設 3） |
| `ContentDownload:RetryDelayMilliseconds` | 重試間隔基數，逐次遞增（預設 2000） |
| `ContentDownload:TranscodingPollSeconds` / `TranscodingMaxPolls` | 影片轉檔輪詢間隔與次數上限（預設 5 秒 × 24 次） |

開發環境設定機密（在 `MessageService/` 目錄下）：

```bash
dotnet user-secrets init
dotnet user-secrets set "Line:ChannelSecret" "<你的 channel secret>"
dotnet user-secrets set "Line:ChannelAccessToken" "<你的 access token>"
```

## 資料庫初始化

- **SQLite（開發）**：啟動時自動 `EnsureCreated()`，免手動。
- **SQL Server（正式）**：需套用 migration：

```bash
ASPNETCORE_ENVIRONMENT=Production dotnet ef database update --project MessageService
```

> 注意：`dotnet ef` 指令會讀 `launchSettings.json` 的 `ASPNETCORE_ENVIRONMENT=Development` 而套到 Sqlite 設定，所以操作 SQL Server migration 時必須顯式指定 `ASPNETCORE_ENVIRONMENT=Production`。

## 本機串接 LINE 測試

1. LINE Developers Console 建立 Messaging API channel，取得 Channel Secret / Access Token
2. `dotnet run` 啟動本機服務
3. 用 dev tunnel 或 ngrok 將本機 port 開成 HTTPS URL
4. 在 LINE console 設定 Webhook URL 為 `https://<tunnel>/api/line/webhook` 並啟用
5. 將 bot 拉進測試群組，發文字/貼圖/圖片/影片/檔案各一，確認 SQLite 落地與 `DownloadStatus` 流轉

## 設計決策備忘

- **圖片/影片/檔案存 DB（varbinary）而非磁碟**：外部應用程式只要連 DB 就能讀到；保留期清除靠 CASCADE 一次帶走，不會產生孤兒檔案。代價是 DB 容量成長快，若量大屆時再評估 FILESTREAM 或磁碟存放（內容獨立一表已為搬遷留好最小改動面）
- **webhook 一律回 200**（簽章合法後）：回非 2xx 會讓 LINE 重送並可能判定 webhook 失效；個別事件失敗只記 log
- **下載走背景佇列**：影片要等 LINE 轉檔、檔案可達數百 MB，不能在 webhook 請求內同步處理；服務重啟會自動撈回殘留的 Pending 接續下載
- **SQLite 的 DateTimeOffset 限制**：SQLite 只支援相等比較，`<`/`>` 無法轉譯。`MessageDbContext` 在 SQLite 環境對 `EventTimestamp` 套 `DateTimeOffsetToBinaryConverter`；SQL Server 維持原生 `datetimeoffset`（保持型別對其他工具可讀）
- **BackgroundService 例外一律就地捕捉**：.NET 6+ 預設未捕捉例外會停掉整個 host，清除或下載失敗只能記 log 等下輪，不能讓 webhook 服務跟著死

## 日誌（NLog）

設定檔為 [MessageService/nlog.config](MessageService/nlog.config)，輸出到 Console 與**執行檔目錄下的 `logs/messageservice-{日期}.log`**（每日一檔，保留 30 天）。`Microsoft.*` 的雜訊只留 Warning 以上。

關鍵事件都有紀錄：收到並存檔的每則訊息（型別/群組/是否排入下載）、內容下載完成（大小/MIME）、下載重試與最終失敗、影片轉檔失敗、啟動接續補跑數量、保留期清除筆數與下次排程時間、簽章驗證拒絕。

## 測試

```bash
dotnet test
```

測試使用 SQLite（in-memory 或暫存檔），涵蓋簽章驗證、五種型別分流、防重送、背景下載（成功/轉檔輪詢/轉檔失敗/重試耗盡/啟動接續）、保留期清除（含 CASCADE 驗證）、Controller 整合測試（401/200/畸形 body 仍 200）。
