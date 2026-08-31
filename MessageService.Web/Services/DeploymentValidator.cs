using MessageService.Options;

namespace MessageService.Services;

/// <summary>設定沒對齊部署模式時，讓程式啟動就失敗——帶著錯誤設定悄悄跑起來（例如缺了
/// ChannelSecret 卻一直收 webhook、永遠 401）遠比啟動失敗更難發現，尤其是網段分離的部署，
/// 出問題時往往沒辦法立刻連上去看 log。</summary>
public static class DeploymentValidator
{
    public static void Validate(
        DeploymentOptions deployment, LineOptions line, ViewerOptions viewer, IngestOptions ingest,
        EdgeProxyOptions edgeProxy, ILogger logger,
        DatabaseStartupDecision? database = null)
    {
        var mode = deployment.Mode;
        var capabilities = DeploymentCapabilities.Derive(mode, line, viewer, ingest);
        var db = database ?? DatabaseStartupDecision.Default;

        // EdgeProxy 驗證完自己的設定就結束——後面全部是其他模式的規則，讓它繼續跑下去會被
        // 「複製整份 appsettings 過來」的殘留設定誤擋（EdgeBaseUrl 缺 ApiKey、OutboundHere 缺
        // token、SqlServer 缺連線字串都會 throw），錯誤訊息還指向 EdgeProxy 根本沒有的功能，
        // 公網那台部署當天就起不來
        if (mode is DeploymentMode.EdgeProxy)
        {
            if (string.IsNullOrWhiteSpace(edgeProxy.TargetBaseUrl))
            {
                throw new InvalidOperationException(
                    "Deployment:Mode=EdgeProxy 需要設定 EdgeProxy:TargetBaseUrl（Edge 主機的位址，" +
                    "例如 http://192.0.2.10/MSLine），否則轉發無處可送。");
            }

            // 只驗非空不夠：漏打 http:// 這種手滑（例如 "192.0.2.10/MSLine"）不會在啟動時
            // 出事，而是第一則 webhook 進來時 CreateClient 才丟 UriFormatException、被轉發的
            // 通用 catch 吃掉回 502——訊息靜默全掉、log 只剩一則被節流的警告，非常難查
            if (!Uri.TryCreate(edgeProxy.TargetBaseUrl.Trim(), UriKind.Absolute, out var target)
                || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"EdgeProxy:TargetBaseUrl（{edgeProxy.TargetBaseUrl}）不是合法的 http/https 位址——" +
                    "請確認有帶 scheme，例如 http://192.0.2.10/MSLine。");
            }

            // 殘留設定只提醒不擋：EdgeProxy 只做轉發，不會用到 Line／Ingest／檢視端／資料庫設定。
            // 資料庫只查得到 SQL Server 連線字串（Sqlite 那條的解析在服務註冊階段，EdgeProxy
            // 已提早返回不會走到）——涵蓋「整份設定檔複製過來」這個主要情境就夠了
            if (!string.IsNullOrWhiteSpace(line.ChannelSecret) || !string.IsNullOrWhiteSpace(line.ChannelAccessToken) ||
                !string.IsNullOrWhiteSpace(ingest.BaseUrl) || !string.IsNullOrWhiteSpace(ingest.ApiKey) ||
                !string.IsNullOrWhiteSpace(ingest.EdgeBaseUrl) || viewer.Enabled == true ||
                db.HasSqlServerConnectionString)
            {
                logger.LogWarning(
                    "Deployment:Mode=EdgeProxy 只做轉發，不會用到 Line／Ingest／檢視端／資料庫設定，但偵測到有值——" +
                    "可能是從其他主機複製 appsettings 時忘記清掉，請確認是否為刻意保留。");
            }

            return;
        }

        if (mode is DeploymentMode.Edge)
        {
            if (ingest.Channel is IngestChannel.Pull)
            {
                if (string.IsNullOrWhiteSpace(ingest.ApiKey))
                {
                    throw new InvalidOperationException(
                        "Deployment:Mode=Edge 且 Ingest:Channel=Pull：Pull 模式仍需 ApiKey 驗證 Core 進來的輪詢請求。");
                }
            }
            else if (string.IsNullOrWhiteSpace(ingest.BaseUrl) || string.IsNullOrWhiteSpace(ingest.ApiKey))
            {
                throw new InvalidOperationException(
                    "Deployment:Mode=Edge 需要設定 Ingest:BaseUrl（Core 模式主機的 ingest API 位址）與 " +
                    "Ingest:ApiKey（雙邊共用密鑰，須與 Core 端一致），否則 outbox 排出的事件無處可送。");
            }
        }

        // 暫存區比單一檔案的上限還小的話，最大的那種檔案永遠塞不進去。不擋啟動：
        // MaxContentBytes 是既有可調鍵，調得比暫存預設值大的部署升級時不該啟動失敗——
        // 實際生效的上限會夾到至少等於 MaxContentBytes（見 EdgeContentStaging），這裡只提醒
        if (capabilities.EdgePullApiEnabled && ingest.PullStagingMaxBytes < ingest.MaxContentBytes)
        {
            logger.LogWarning(
                "Ingest:PullStagingMaxBytes（{Staging}）小於 Ingest:MaxContentBytes（{MaxContent}）：" +
                "實際生效的暫存上限會自動提高到 {Effective}，否則最大的單一檔案永遠無法暫存。" +
                "請確認這台主機的記憶體足以容納。",
                ingest.PullStagingMaxBytes, ingest.MaxContentBytes, ingest.MaxContentBytes);
        }

        // Ingest:Channel 只對 Edge 有意義。其他模式設了不會有作用，但看起來像有——
        // 尤其 AllInOne 設成 Pull 會讓人以為「這台改成被動接收」，實際上什麼都沒變
        if (mode is not DeploymentMode.Edge && ingest.Channel is not IngestChannel.Auto)
        {
            logger.LogWarning(
                "Ingest:Channel={Channel} 只在 Deployment:Mode=Edge 時有作用，這台主機（{Mode}）的設定" +
                "不會有任何效果。", ingest.Channel, mode);
        }

        // 設了 Edge 位址卻沒有金鑰：輪詢每一輪都會被 Edge 回 401，表現成「一直退避」很難查
        if (!string.IsNullOrWhiteSpace(ingest.EdgeBaseUrl) && string.IsNullOrWhiteSpace(ingest.ApiKey))
        {
            throw new InvalidOperationException(
                "設定了 Ingest:EdgeBaseUrl（要主動輪詢 Edge）就必須同時設定 Ingest:ApiKey，" +
                "否則每一輪輪詢都會被 Edge 端的金鑰驗證擋成 401。");
        }

        if (mode is DeploymentMode.Core && string.IsNullOrWhiteSpace(ingest.ApiKey))
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Core 需要設定 Ingest:ApiKey 以驗證 /api/ingest/* 進來的請求。");
        }

        // Edge 沒有資料庫連線，檢視端整組服務都開不起來——顯式設 true 多半是從別台主機複製
        // appsettings 忘記清（跟下面 Viewer 模式殘留設定的警告同一種失誤），但這個設錯不是
        // 「多餘設定」而是「期待的功能不會存在」，寧可啟動失敗講清楚，不要讓人以為檢視端有開
        if (mode is DeploymentMode.Edge && viewer.Enabled == true)
        {
            throw new InvalidOperationException(
                "Deployment:Mode=Edge 沒有資料庫連線，無法啟用檢視端（Viewer:Enabled=true）。" +
                "請移除這個設定，或改用 AllInOne／Core／Viewer 模式。");
        }

        if (capabilities.ReceivesWebhook && string.IsNullOrWhiteSpace(line.ChannelSecret))
        {
            throw new InvalidOperationException(
                "這個模式會收 LINE webhook（Deployment:Mode=AllInOne 或 Edge），需要設定 Line:ChannelSecret 才能驗證簽章。");
        }

        // Line:OutboundHere 現在真的決定 ContentDownloadService／ProfileRefreshService 會不會在
        // 這台主機跑（見 Program.cs 的註冊矩陣，經 DeploymentCapabilities 推導）——OutboundHere
        // 判定為 true 卻沒有 ChannelAccessToken，這兩個背景服務會直接對 LINE profile／content API
        // 打 401，而且不是啟動就爆炸、是跑起來後才悄悄一直失敗，所以要擋在啟動關卡
        if (capabilities.OutboundHere && string.IsNullOrWhiteSpace(line.ChannelAccessToken))
        {
            throw new InvalidOperationException(
                "這台主機會對外呼叫 LINE API（Line:OutboundHere 判定為 true），需要設定 " +
                "Line:ChannelAccessToken，否則媒體下載與頭貼快取會在背景服務啟動後持續打 401。");
        }

        // Line:OutboundVia 與 OutboundProxyBaseUrl 驗證
        if (line.OutboundVia is LineOutboundVia.EdgeProxy)
        {
            if (string.IsNullOrWhiteSpace(line.OutboundProxyBaseUrl))
            {
                throw new InvalidOperationException(
                    "設定了 Line:OutboundVia=EdgeProxy，必須同時設定 Line:OutboundProxyBaseUrl（EdgeProxy 主機的位址，" +
                    "例如 http://192.0.2.10/MSLine），否則外送請求無處可送。");
            }

            if (!Uri.TryCreate(line.OutboundProxyBaseUrl.Trim(), UriKind.Absolute, out var proxyUri)
                || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Line:OutboundProxyBaseUrl（{line.OutboundProxyBaseUrl}）不是合法的 http/https 位址——" +
                    "請確認有帶 scheme，例如 http://192.0.2.10/MSLine。");
            }

            // 非 Edge／AllInOne 模式（也就是不會對外打 LINE 的模式）設了 OutboundVia=EdgeProxy
            // → 記 Warning 說明不會有作用（比照同檔既有「Ingest:Channel 只在 Edge 有作用」那段的寫法）
            if (mode is not (DeploymentMode.Edge or DeploymentMode.AllInOne))
            {
                logger.LogWarning(
                    "Line:OutboundVia=EdgeProxy 只在對外打 LINE API 的模式（Deployment:Mode=Edge 或 AllInOne）有作用，" +
                    "這台主機（{Mode}）的設定不會有任何效果。", mode);
            }
        }

        // Core／Viewer 顯式把 OutboundHere 開成 true 表示「由這台打 LINE 內容／profile API」，
        // 此時 Edge 端必須顯式設 false，否則兩台都會下載同一批媒體（LINE 內容 API 不冪等計費、
        // 也浪費頻寬）。這種跨主機的組合錯誤單機驗證不出來，只能提醒
        if (mode is DeploymentMode.Core or DeploymentMode.Viewer && line.OutboundHere == true)
        {
            logger.LogWarning(
                "Deployment:Mode={Mode} 顯式設定了 Line:OutboundHere=true：請確認 Edge 端主機已顯式設 " +
                "Line:OutboundHere=false，否則兩台會重複下載同一批媒體內容。", mode);
        }

        // AllInOne 模式關掉媒體下載／頭貼快取是可疑的設定組合（單機部署通常沒有理由要這樣做），
        // 但不是錯誤——只記警告，不擋啟動
        if (mode is DeploymentMode.AllInOne && !capabilities.OutboundHere)
        {
            logger.LogWarning(
                "Deployment:Mode=AllInOne 但 Line:OutboundHere 判定為 false：媒體下載與頭貼快取不會執行，" +
                "所有訊息內容會停在 Pending。如果這不是刻意的，請檢查設定。");
        }

        // Core 端 OutboundHere=false 是兩台拓撲的正常設定（對外由 Edge 負責），不是錯誤——但這代表
        // 「圖片載不出來／名稱與頭貼一直是空的」的成因有一半在另一台主機上，而那台的設定這裡驗證
        // 不到。實際踩過的情境是兩台都沒開：媒體永遠停在 Pending、Groups.GroupName 與
        // GroupMembers.DisplayName 永遠是 null，前台看到的就會是 LINE 的原始 ID，很容易被當成
        // 檢視端壞掉而在這台主機上白找。啟動時把這條線索留在 log 裡，省下那趟冤枉路
        if (mode is DeploymentMode.Core && !capabilities.OutboundHere)
        {
            logger.LogInformation(
                "Deployment:Mode=Core 且 Line:OutboundHere 判定為 false：本機不會對外呼叫 LINE API，" +
                "媒體下載與名稱／頭貼快取全部倚賴 Edge 端主機。若前台的圖片一直停在「內容抓取中」、" +
                "或名稱與頭貼始終空白，請先確認 Edge 端有在執行且它的 Line:OutboundHere 沒有被設成 false。");
        }

        // 檢視端啟用時預設會一併開（見 DeploymentCapabilities.ViewerEnabled）——空白名單雖然是
        // 「全拒」而非啟動失敗，但這種組合通常代表部署時漏設，值得提醒。不限 Core：AllInOne
        // 是最常見的拓撲，同樣會「檢視端啟用了卻全拒」而不自知
        if (capabilities.ViewerEnabled && viewer.AllowedClientIps.Length == 0)
        {
            logger.LogWarning(
                "Deployment:Mode={Mode} 且檢視端已啟用，但 Viewer:AllowedClientIps 是空的——檢視端會拒絕所有請求，" +
                "直到設定允許的來源網段為止。", mode);
        }

        // 顯式設定 SqlServer 卻沒有可用的連線字串——不管救場開不開，這都是「連探測都沒得做」
        // 的更基本錯誤，寧可啟動失敗講清楚，不要留給 EF 在 UseSqlServer(null) 時丟出難懂的例外
        if (db.EffectiveProvider == "SqlServer" && !db.HasSqlServerConnectionString)
        {
            throw new InvalidOperationException(
                "Database:Provider 是 SqlServer，但 ConnectionStrings:SqlServer 沒有設定值或是空字串，" +
                "無法連線。請設定連線字串，或移除 Database:Provider 讓程式依連線字串自動推導。");
        }

        // 需求2：Database:Provider 未設定時依 ConnectionStrings:SqlServer 有沒有值推導，顯式設定
        // 永遠優先（見 DatabaseProviderResolver）。推導路徑不會出現「Sqlite 但留著 SqlServer
        // 連線字串」這個組合（有連線字串就會被推導成 SqlServer）——只有使用者自己顯式蓋過推導
        // 結果、設成 "Sqlite"，才是典型的「想切換但忘記改」或「複製設定殘留」，直接檢查
        // ConfiguredProvider 本身而不是派生出來的旗標，跟救場有沒有觸發無關，語意才不會混淆
        if (string.Equals(db.ConfiguredProvider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            && db.HasSqlServerConnectionString)
        {
            logger.LogWarning(
                "Database:Provider 顯式設定為 Sqlite，但 ConnectionStrings:SqlServer 有設定值——這個連線" +
                "字串不會被使用。如果是想改用 SQL Server，請移除 Database:Provider 讓程式自動推導，或改成" +
                " \"SqlServer\"；如果只是複製設定殘留，可以移除這個連線字串。");
        }

        // Database:SqliteFallback 只有 AllInOne 會用到（見 Program.cs 的救場觸發條件）——其他
        // 模式設了這個鍵大概率是複製設定殘留，不是錯誤但值得提醒，免得誤以為這台也有救場
        if (mode is not DeploymentMode.AllInOne && db.SqliteFallbackConfigured)
        {
            logger.LogWarning(
                "Database:SqliteFallback 只在 Deployment:Mode=AllInOne 時有效，這台主機（{Mode}）的設定" +
                "不會有任何作用。", mode);
        }

        // 救場已經觸發：本機正在用 SQLite 撐著，不是原本設定要的 SQL Server——這是需要立刻處理
        // 的異常狀態，用 Error 等級確保被注意到，訊息帶上原始失敗原因方便直接定位問題
        if (db.SqliteFallbackTriggered)
        {
            logger.LogError(
                "Deployment:Mode=AllInOne 設定使用 SQL Server，但啟動時連線／schema 驗證失敗，已改用" +
                "本機 SQLite 運作（Database:SqliteFallback 預設開啟，設 false 可改成寧可啟動失敗）：" +
                "{Reason}。修好 SQL Server 後重新啟動即可切回；這段期間寫入本機 SQLite 的資料不會自動" +
                "搬到 SQL Server，見 docs/DEPLOYMENT-GUIDE.md。", db.SqliteFallbackReason);
        }

        // Viewer 模式不會用到 Line／Ingest 設定——多半是從別台主機複製 appsettings 忘記清掉，
        // 不是錯誤但值得提醒，免得誤以為這些設定在 Viewer 模式下也有作用
        if (mode is DeploymentMode.Viewer &&
            (!string.IsNullOrWhiteSpace(line.ChannelSecret) || !string.IsNullOrWhiteSpace(ingest.BaseUrl) || !string.IsNullOrWhiteSpace(ingest.ApiKey)))
        {
            logger.LogWarning(
                "Deployment:Mode=Viewer 不會用到 Line／Ingest 設定，但偵測到有值——" +
                "可能是從其他主機複製 appsettings 時忘記清掉，請確認是否為刻意保留。");
        }

    }
}
