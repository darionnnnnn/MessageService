# LINE Bot 建立與串接指南

從零把一個 LINE Bot 接上本專案的收錄端（MessageService）並完成實機測試。四個階段：**建立 Bot → 綁定金鑰 → 設定 Webhook → 測試驗證**。

> 全程只會用到收錄端 `MessageService`。檢視端 `MessageService.Web` 不需要接 LINE，它只讀資料庫。

---

## 事前準備

| 項目 | 說明 |
|---|---|
| LINE 帳號 | 一般個人 LINE 帳號即可，需能收簡訊驗證 |
| 一個測試用 LINE 群組 | 建議另開一個測試群組，不要直接用正式工作群組 |
| .NET 10 SDK | 本專案的 target framework 是 `net10.0` |
| 對外 HTTPS 網址 | LINE 只接受 HTTPS 且必須是公開可連的網址。本機開發用 dev tunnel 或 ngrok 打通道（見步驟三） |

---

## 步驟一：在 LINE 平台建立 Bot

### 1-1. 登入 LINE Developers Console

前往 <https://developers.line.biz/console/>，用你的 LINE 帳號登入。第一次登入會要求建立開發者帳號（填名稱與 email）。

### 1-2. 建立 Provider（提供者）

Provider 是「開發者／公司」層級的容器，底下才掛 channel。

1. 在 Console 首頁點 **Create a new provider**
2. 輸入名稱（例如公司或團隊名稱，之後不易更改）
3. 點 **Create**

### 1-3. 建立 Messaging API Channel

Channel 就是實際的 Bot。

1. 在剛建立的 Provider 頁面點 **Create a Messaging API channel**
   - 若看到要你先建立 LINE Official Account 的引導，照著走完再回來；新版流程會先在 LINE Official Account Manager 建立官方帳號，再回到 Console 綁定
2. 依序填寫：
   - **Channel name**：Bot 顯示名稱（群組中會看到這個名字）
   - **Channel description**：用途說明
   - **Category / Subcategory**：選最接近的分類
   - **Email address**：聯絡信箱
3. 勾選同意條款 → **Create**

### 1-4. 開啟群組功能（本專案必做）

**這是最容易漏掉、漏掉就完全收不到訊息的一步。** LINE 預設不允許 Bot 加入群組。

1. 進入剛建立的 channel → **Messaging API** 分頁
2. 找到 **Allow bot to join group chats**，切換成 **啟用**

沒開這項的話，Bot 根本無法被邀請進群組，自然一則訊息都收不到。

### 1-5. 關閉自動回覆（建議）

本專案只收訊息、不回訊息。LINE 官方帳號預設會自動回覆罐頭訊息，留著會在群組裡洗版。

1. 在 **Messaging API** 分頁找到 **LINE Official Account features**
2. 點 **Auto-reply messages** 旁的 **Edit** → 會轉跳到 LINE Official Account Manager
3. 將 **自動回應訊息（Auto-reply messages）** 設為 **停用**
4. 同一頁的 **加入好友的歡迎訊息（Greeting messages）** 也建議停用

---

## 步驟二：把金鑰綁定到專案

專案需要兩個值，**分別在不同分頁**，很多人會找錯地方：

| 設定鍵 | LINE Console 位置 | 用途 |
|---|---|---|
| `Line:ChannelSecret` | **Basic settings** 分頁 → Channel secret | 驗證 webhook 簽章（確認請求真的來自 LINE） |
| `Line:ChannelAccessToken` | **Messaging API** 分頁 → Channel access token | 下載圖片/影片/語音/檔案原檔、查詢群組與成員名稱 |

### 2-1. 取得 Channel secret

1. channel → **Basic settings** 分頁
2. 找到 **Channel secret**，點 **Issue**（若尚未產生）或直接複製既有值

### 2-2. 取得 Channel access token

1. channel → **Messaging API** 分頁
2. 捲到 **Channel access token (long-lived)**
3. 點 **Issue** 產生，複製整串（很長，注意複製完整）

> LINE 官方建議正式環境改用有效期 30 天的短期 token 或 v2.1 token 以策安全。本專案目前的 `LineProfileClient` / `LineContentClient` 是在啟動時把 token 讀進 `HttpClient` 的 Authorization 標頭，**不支援自動換發**，所以先用 long-lived token。若日後改用短期 token，需要另外實作換發機制。

### 2-3. 寫入專案設定

**絕對不要**把這兩個值填進 `appsettings.json` 再 commit——那是公開 repo，等於把 Bot 的控制權公開。專案裡那兩個欄位刻意留空就是這個原因。

**開發環境**用 user-secrets（存在專案外的使用者設定檔，不會進版控）：

```bash
cd MessageService
dotnet user-secrets init
dotnet user-secrets set "Line:ChannelSecret" "貼上你的 channel secret"
dotnet user-secrets set "Line:ChannelAccessToken" "貼上你的 access token"
```

確認有寫進去：

```bash
dotnet user-secrets list
```

**正式環境**用環境變數（`__` 兩個底線代表設定階層的冒號）：

```bash
setx Line__ChannelSecret "你的 channel secret"
setx Line__ChannelAccessToken "你的 access token"
```

正式環境同時要設定資料庫連線（預設 `Database:Provider` 已是 `SqlServer`）：

```bash
setx ConnectionStrings__SqlServer "Server=...;Database=MessageService;..."
```

---

## 步驟三：設定 Webhook

LINE 只會把訊息推送到**公開的 HTTPS 網址**，所以本機測試需要一條對外通道。

### 3-1. 啟動收錄端服務

**用 HTTP-only 啟動**，這點很重要：

```bash
dotnet run --project MessageService --urls http://localhost:5072
```

> **為什麼要指定 HTTP-only？** 專案管線裡有 `app.UseHttpsRedirection()`。如果同時掛了 HTTPS port，它會把進來的 HTTP 請求回 307 轉址——而 **LINE 的 webhook 不會跟隨轉址**，會直接判定失敗。只掛 HTTP 網址時，這個中介層找不到 HTTPS port 會自動失效（log 會出現一行 `Failed to determine the https port for redirect.` 的警告，那是預期的、可以忽略），請求就能正常進到 controller。TLS 交由通道那一端處理。

開發環境預設用 SQLite（`appsettings.Development.json` 指定），啟動時會自動建好資料庫檔 `messages.db`，不需要手動建表。

### 3-2. 開一條對外通道

**方式 A：Dev Tunnels**（Visual Studio 內建，也有 CLI）

```bash
devtunnel host -p 5072 --allow-anonymous
```

`--allow-anonymous` 必加，否則通道會要求登入驗證，LINE 連不進來。

**方式 B：ngrok**

```bash
ngrok http 5072
```

兩者都會給你一個 `https://xxxxx.xxx` 的公開網址，複製起來。

> 免費方案的網址**每次重啟都會變**，換了就要回 LINE Console 重設 webhook URL。

### 3-3. 在 LINE Console 設定 Webhook URL

1. channel → **Messaging API** 分頁
2. 找到 **Webhook settings** → **Webhook URL** 點 **Edit**
3. 填入通道網址加上本專案的端點路徑：

   ```
   https://你的通道網址/api/line/webhook
   ```

   路徑 `/api/line/webhook` 是固定的（定義在 `LineWebhookController`），不能改。
4. 點 **Update**
5. 把 **Use webhook** 切換成 **啟用**（沒開的話 LINE 不會推送任何事件）

### 3-4. 用 Verify 按鈕確認連通

點 Webhook URL 旁的 **Verify**。

- 顯示 **Success** → 通道與簽章驗證都正常，可以進入測試
- 顯示 **Error** → 見下方疑難排解

> Verify 會送出一個 `events` 為空陣列的測試請求，並且**帶有正確簽章**。所以 Verify 成功代表「網址通得到」而且「channel secret 設定正確」——這兩件事一次驗完。若 channel secret 沒設或設錯，會回 401 而 Verify 失敗。

---

## 步驟四：測試

### 4-1. 把 Bot 加入測試群組

1. channel → **Messaging API** 分頁，找到 **Bot basic ID** 或 QR code
2. 用你的 LINE 手機 App 搜尋該 ID 或掃 QR code，加為好友
3. 建立一個測試群組，**邀請這個 Bot 進群組**

> 建議也讓所有測試參與者都把 Bot 加為好友。成員顯示名稱是靠 LINE profile API 取得的，非好友的成員有機會抓不到名稱，畫面上就只會顯示 `U` 開頭的原始 ID（程式有 fallback，不會壞，只是不好看）。

### 4-2. 在群組發各種訊息

依序發送，每種至少一則：

| 型別 | 預期結果 |
|---|---|
| 文字 | `GroupMessages.Text` 存原文 |
| 貼圖 | `Text` 存 `(貼圖)`，不下載任何檔案 |
| 圖片 | 建立 `MessageContents` 一列，狀態 Pending → 背景下載完轉 Completed |
| 影片 | 同上，但會先等 LINE 轉檔完成才下載（可能數十秒） |
| 語音 | 同影片流程 |
| 檔案 | 同上，且 `FileName` 會存原始檔名 |
| 位置 | **刻意不收**，資料庫不會有任何紀錄 |

### 4-3. 看 log 確認

log 同時輸出到 Console 與**執行檔目錄**下的 `logs/messageservice-{日期}.log`（注意是 `bin/Debug/net10.0/logs/`，不是專案根目錄）。

正常會看到：

```
INFO|WebhookEventHandler|Saved text message <id> from group <groupId>
INFO|WebhookEventHandler|Saved image message <id> from group <groupId> (content download queued)
INFO|ContentDownloadService|Downloaded content 3 for message <id> (152340 bytes, image/jpeg)
INFO|RetentionCleanupService|Next retention cleanup scheduled at 2026-07-30 03:00
```

需要注意的訊息：

| log 訊息 | 意義 |
|---|---|
| `Rejected webhook request with invalid signature` | channel secret 設錯或沒設 |
| `Transcoding did not succeed ...` | 影片/語音在 LINE 端轉檔失敗，該筆內容標記 Failed |
| `All 3 download attempts failed ...` | 下載重試耗盡，多半是 access token 錯誤或網路問題 |
| `Group summary unavailable for group ...` | 群組名稱抓不到，畫面會 fallback 顯示 GroupId |
| `Member profile unavailable for group ... user ...` | 成員名稱抓不到（常見於未加 Bot 好友者） |
| `Skipping duplicate webhook event ...` | LINE 重送了同一事件，已正確去重，屬正常 |

### 4-4. 查資料庫確認

開發環境的 SQLite 檔在 `MessageService/messages.db`（或執行目錄下）。

```sql
-- 收到的訊息
SELECT Id, MessageType, Text, UserId, EventTimestamp FROM GroupMessages ORDER BY Id DESC;

-- 媒體下載狀態（重點看 DownloadStatus 有沒有卡在 Pending）
SELECT c.Id, m.MessageType, c.FileName, c.ContentType, c.DownloadStatus, LENGTH(c.Content) AS Bytes
FROM MessageContents c JOIN GroupMessages m ON m.Id = c.GroupMessageId
ORDER BY c.Id DESC;

-- 群組與成員名稱快取
SELECT * FROM Groups;
SELECT * FROM GroupMembers;
```

驗收標準：

- 每則發送的訊息都有一列，`MessageType` 正確
- 圖片/影片/語音/檔案的 `DownloadStatus` 最終都是 `Completed`，`Bytes` 大於 0
- `Groups` 有群組名稱、`GroupMembers` 有成員顯示名稱

### 4-5. 用檢視端看畫面（端到端驗收）

讓檢視端指向同一個資料庫：

```bash
dotnet run --project MessageService.Web --ConnectionStrings:Sqlite="Data Source=完整路徑/messages.db"
```

開啟 <http://localhost:5106>，應該看到剛才發的訊息以 LINE 對話樣式呈現，圖片可點開、影片可播放、檔案可下載。

> 檢視端有 IP 白名單，本機開發用的 `appsettings.Development.json` 已預設放行 `127.0.0.1` 與 `::1`。正式環境記得設定 `AllowedClientIps`，**留空等於全部拒絕**。

---

## 疑難排解

### Verify 按鈕顯示 Error

| 可能原因 | 檢查方式 |
|---|---|
| 服務沒啟動 | 本機 `curl http://localhost:5072/api/line/webhook -X POST -d '{}'` 應回 401（回 401 代表服務活著且簽章驗證有在運作） |
| 通道網址失效／換過 | 免費方案重啟就換網址，回 Console 更新 |
| 通道要求驗證 | devtunnel 要加 `--allow-anonymous` |
| **被 HTTPS 轉址擋掉** | 確認是用 `--urls http://localhost:5072` 啟動，而不是同時掛了 HTTPS port |
| Webhook URL 少了路徑 | 必須是 `https://.../api/line/webhook`，只填網域不行 |

### Verify 成功，但群組發言收不到任何東西

1. **確認 `Allow bot to join group chats` 有啟用**（步驟 1-4）——這是最常見原因
2. 確認 **Use webhook** 已啟用
3. 確認 Bot 真的在群組裡（群組成員清單看得到）
4. 一對一聊天測試看看：若私訊收得到、群組收不到，幾乎可以確定是群組設定沒開
5. 注意本專案**只收群組訊息**，私訊會被 `WebhookEventHandler` 過濾掉不寫入資料庫——用私訊測試時看 log 有進來就好，不要期待資料庫有資料

### log 出現 `Rejected webhook request with invalid signature`

channel secret 不正確。重新從 **Basic settings** 分頁複製（不要複製到 access token），重設 user-secrets 後**重啟服務**（設定是啟動時讀取的）。

### 媒體一直卡在 Pending

- 看 log 有沒有 `Download attempt ... failed`：多半是 access token 錯誤或過期
- 影片/語音需要等 LINE 轉檔，預設最多輪詢 5 秒 × 24 次（2 分鐘）；大檔可能需要調高 `ContentDownload:TranscodingMaxPolls`
- 服務重啟時會自動把殘留的 Pending 重新排入下載佇列（log 會出現 `Requeued N pending content downloads from previous run`），所以中途斷掉不會永久遺失

### 訊息有進來，但發言者顯示成 `U` 開頭亂碼

該成員的 profile 抓不到，通常是沒把 Bot 加為好友。程式有 fallback 不會壞。快取有 7 天有效期（`ProfileCache:RefreshAfter`），成員加好友後，下次該成員發言且快取過期時會自動補上名稱。

### 正式環境部署注意

- 資料表要先建立：`ASPNETCORE_ENVIRONMENT=Production dotnet ef database update --project MessageService.Data --startup-project MessageService`
- Webhook URL 改成正式網域，一個 channel **只能設定一個** webhook URL（開發與正式不能共用同一個 channel，建議各自建立 channel）
- 若部署在反向代理（IIS/nginx）後面，檢視端要開 `UseForwardedHeaders`，否則 IP 白名單看到的會是代理的 IP 而不是真實來源

---

## 參考資料

- [LINE Developers Console](https://developers.line.biz/console/)
- [Group chats and multi-person chats | LINE Developers](https://developers.line.biz/en/docs/messaging-api/group-chats/)
- [Receive messages (webhook) | LINE Developers](https://developers.line.biz/en/docs/messaging-api/receiving-messages/)
- [Verify webhook signature | LINE Developers](https://developers.line.biz/en/docs/messaging-api/verify-webhook-signature/)
- [Messaging API reference | LINE Developers](https://developers.line.biz/en/reference/messaging-api/)
