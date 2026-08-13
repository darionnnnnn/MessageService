# 應用層欄位加密

`Encryption:Enabled=true` 時，訊息內容與可辨識個資欄位在寫入資料庫前先用 AES-256-GCM
加密，即使拿到資料庫連線或備份檔案也看不到明文。這是應用層加密（金鑰在應用程式手上），
跟 SQL Server TDE／磁碟加密是互補而非取代關係——TDE 防的是「檔案或備份被偷」，這裡防的是
「連 DBA 直查資料表也看不到」。

## 設定

**每一台直連資料庫的主機**（`AllInOne`／`Core`／`Viewer`）的 `Encryption:Key` **必須設成
完全一樣**——合併成單一專案之後消除的是 AllInOne 模式「收錄與檢視兩份設定各自維護」的
不一致風險，但多台拓撲下這個約束本質沒變：任何兩台各自加解密同一份資料的主機，金鑰
不一致都會讓其中一端寫入的密文另一端解不開。三台拓撲（Edge + Core + Viewer）時，
Core 與 Viewer 兩台都要設同一把金鑰；Edge 從不直連資料庫，不需要設定這個值（見下方
拆機部署的例外說明）。

```json
"Encryption": {
  "Enabled": true,
  "Key": "<base64 編碼的 32 bytes>",
  "SearchWindowDays": 14
}
```

產生金鑰（PowerShell）：

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
[Convert]::ToBase64String($bytes)
```

> **絕對不要用 `Get-Random` 產生金鑰。** 它底層是 `System.Random`——一個以 32-bit 種子驅動的
> 一般用途 PRNG，不是密碼學安全亂數源。用它產生的 32 bytes 完全由那個種子決定，實際強度
> 只有 2^32 而不是 2^256：拿到資料庫備份的人只要窮舉種子、各重算一次金鑰，再用任何一筆
> `ENC1:` 值的 GCM 認證標籤驗證是否命中即可，普通機器數小時內就能還原金鑰，本文件開頭
> 「即使拿到資料庫或備份也看不到明文」的保證會完全失效。若既有環境是用 `Get-Random`
> 產生的金鑰，請視同金鑰已外洩，換一把新金鑰並重新加密既有資料。

`Key` 缺漏或格式不對（不是合法 base64、解碼後不是 32 bytes）時，兩邊的服務都會在啟動當下
直接失敗（`FieldCipher` 建構子拋例外），不會等到第一則訊息進來才在背景任務裡出錯。

> **拆機部署（`Deployment:Mode=Edge`）的例外**：加解密只發生在**碰得到資料庫的那一端**
> （`AllInOne`／`Core` 模式，以及 `Viewer` 模式）。`Edge` 模式的主機沒有 `MessageDbContext`
> 也沒有 `DbContentWorkSource`，金鑰對它毫無用途——**請維持 `Encryption:Enabled=false`，
> 不要把金鑰放到這台直接對 LINE 曝露的最外緣主機上**，那只是白白多一個外洩面。反過來說，
> 在 `Edge` 端誤設 `Enabled=true` 卻沒給合法金鑰，服務會在啟動時直接失敗。

## 加密範圍

| 欄位 | 加密 |
|---|---|
| `GroupMessages.Text`（訊息內文） | ✓ |
| `MessageContents.Content`（圖片/影片/語音/檔案本體） | ✓（分塊加密，見下） |
| `MessageContents.FileName` | ✓ |
| `Groups.GroupName` / `PictureUrl` | ✓ |
| `GroupMembers.DisplayName` / `PictureUrl` | ✓ |
| `UserAliases.Alias` | ✓ |
| `GroupMessages.GroupId` / `UserId` | ✗（刻意不加密，見下） |
| `MessageContents.ContentType`（如 `image/jpeg`） | ✗ |
| `MaskKeywords.Keyword` / `Replacement` | ✗（**注意**：遮蔽關鍵字往往就是人名、案號這類敏感字串，等於一份敏感字典以明文留在 DB 裡。功能上要加密沒有障礙——`MaskingRuleSet` 是整批載進記憶體比對、沒有下推到 SQL——只是目前尚未納入範圍） |
| `AnonymousIdentities.Label`（如「小熊」） | ✗（代號本身不含個資） |
| `ViewerSettings` 的各項設定 | ✗ |

`GroupId`／`UserId` 保持明文：它們是 LINE 配發的隨機識別碼、本身不含個資，而檢視端幾乎
每一種查詢（側欄、未讀數、訊息視窗、搜尋）都要用它們做索引查詢或 `GROUP BY`。如果連這兩個
欄位都加密，AES-GCM 的隨機 nonce 會讓同一個 GroupId 每次加密結果都不同，索引與分組會
整個失效。

## 文字欄位：整值加密，透明讀寫

文字欄位透過 EF Core 的 `ValueConverter` 套用，寫入時自動加密、讀取時自動解密——包含完整
實體與 LINQ 投影（`Select(m => new { m.Text })` 這種只挑欄位的查詢一樣會自動解密），
所有 controller 幾乎不需要改程式碼。

存成 `ENC1:` 前綴 + base64(nonce + tag + ciphertext)。**加密啟用前寫入的舊資料（沒有這個
前綴）會原樣顯示，不需要一次性轉換作業**——新舊資料混存完全無痛，可以隨時開啟加密而不用
先跑資料遷移。

反過來：**如果先加密過一段時間、之後又把 `Encryption:Enabled` 改回 `false`，已經加密的
舊訊息會顯示成一串 `ENC1:...` 亂碼**（沒有金鑰可解），不會自動轉回明文。這是刻意的簡化——
「先啟用加密、之後又關掉」是不建議的操作路徑，真的要做的話得先手動把舊資料解密回明文
再關閉設定。

**媒體的情況比文字更嚴重**：已加密的 blob 在關掉金鑰後不是顯示成亂碼，而是被
`ContentStreamService` 判定為「內容不可用」直接回 **404**——圖片、影片、檔案會整個從
畫面上消失，不是變成看不懂的內容而已。

文字欄位的解密失敗（金鑰不對、內容損毀，或極端情況下有人真的在訊息裡打了 `ENC1:` 開頭的
文字）不會讓請求 500，只會原樣顯示該欄位並記一行 warning log。**blob 的解密失敗則是回
404**（見上），兩者行為不同——同一個成因（例如兩端金鑰不一致）在畫面上會同時表現成
「訊息變 `ENC1:` 亂碼」與「媒體全部不見」，排查時記得把這兩個症狀串在一起看。

## 內容 blob：分塊加密，保留 Range 拖進度能力

`MessageContents.Content` 不能像文字欄位那樣整值加密——影片／語音要支援瀏覽器的 Range
請求（拖進度），解密必須能只處理使用者實際要的那一小段位元組，不能每次都把整個檔案
解密一遍。格式：

```
[表頭 16 bytes：magic "MSE1"(4) + chunkSize(4) + 明文總長度(8)]
[chunk 0：nonce(12) + tag(16) + ciphertext]
[chunk 1：nonce(12) + tag(16) + ciphertext]
...
```

固定 1MB 明文塊、最後一塊可能較短。寫入端（`DbContentWorkSource`）邊讀來源串流邊加密邊寫
資料庫，一次只在記憶體放一個 chunk；讀取端（`ContentStreamService`）收到 Range 請求時，
只把涵蓋該區間的 chunk 解密、串流寫進回應，同樣不會整份解密進記憶體。

跟文字欄位一樣支援新舊資料混存：讀取端一律先偷看 blob 前 16 bytes 判斷是不是這個格式
（看資料本身的 magic，不是看 `Encryption:Enabled` 設定），沒有這個表頭就當成加密啟用前的
舊 blob，直接原樣提供。

## 訊息搜尋的限制

加密啟用時，SQL 端的 `LIKE` 沒辦法對密文做子字串比對（GCM 加密後，明文的子字串跟密文
完全沒有對應關係）。訊息內容搜尋因此改成：只在最近 `Encryption:SearchWindowDays`
（預設 14 天）內的文字訊息解密後在記憶體比對。超過這個天數的訊息內容搜不到（姓名搜尋
不受影響——那是先查 `GroupMembers` 全部成員，跟訊息內容無關）。

## 部署檢查清單

1. 產生一把金鑰，每一台直連資料庫的主機（`AllInOne`／`Core`／`Viewer`，見上方拓撲說明）的
   `appsettings.Production.json` 都設成完全一樣的 `Encryption:Key`。
2. 這些主機都設 `Encryption:Enabled=true`；`Edge` 主機維持 `false`。
3. 先在測試環境驗證：送一則訊息、上傳一張圖片，確認檢視端顯示正常、資料庫裡的
   `Text`／`Content` 欄位是看不懂的密文。
4. 妥善保管金鑰——遺失金鑰等於遺失所有已加密的訊息與媒體，沒有復原機制。
5. 金鑰不要進版本控制——`appsettings.Production.json` 本身就不進 repo（見
   `deploy/README.md`），把實際金鑰值直接寫在部署到各主機的那份 `appsettings.Production.json`
   裡即可，不需要額外透過環境變數或密鑰管理服務覆蓋。
