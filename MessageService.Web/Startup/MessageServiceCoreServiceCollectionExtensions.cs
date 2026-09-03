using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Web.Middleware;
using MessageService.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Startup;

/// <summary>Program.cs 里純機械式拆出來的結果——不改任何邏輯／順序／條件，只是把同一段
/// DI 註冊矩陣搬進這個檔案，讓 Program.cs 本身可以只剩組裝呼叫。資料庫 provider 推導與
/// SQLite 救場探測（需求2）放在這裡而不是獨立一個檔案，是因為這段決定了要註冊哪個
/// DbContext，跟註冊本身密不可分，拆開反而要多傳一堆參數。
///
/// provider 相關的欄位一律透過 DatabaseStartupDecision 取用，不在這個 record 另存副本——
/// 兩份必須永遠同步的欄位正是「改共用欄位漏改讀取端」那類 bug 的溫床（終檢輪收斂）。
/// 這裡只放 decision 沒有的東西：兩條已解析的 Sqlite 連線字串、AutoMigrate 旗標與 SqliteBusyTimeoutMs。</summary>
public record MessageServiceCoreRegistration(
    DatabaseStartupDecision DatabaseStartupDecision,
    bool AutoMigrate,
    string? SqliteConnectionString,
    string? OutboxConnectionString,
    int SqliteBusyTimeoutMs);

public static class MessageServiceCoreServiceCollectionExtensions
{
    public static MessageServiceCoreRegistration AddMessageServiceCore(
        this WebApplicationBuilder builder,
        DeploymentCapabilities capabilities,
        DeploymentMode deploymentMode,
        IngestOptions ingestOptionsRaw)
    {
        // HeartbeatService 需要在建構子直接拿到能力推導結果，不必每次都自己重新 Derive 一次
        builder.Services.AddSingleton(capabilities);

        // Add services to the container.

        // MVC 檢視（Home/Error 頁＋靜態資源管線）只有檢視端要跑——純 Edge 主機不需要、也不該暴露
        // 這些頁面。ingest／webhook 用的 attribute-routed API controller 兩種註冊方式都會掛，差別只在
        // 有沒有多帶 Razor 檢視引擎與靜態資源那一套
        var modeConvention = new DeploymentModeConvention(capabilities);
        if (capabilities.ViewerEnabled)
        {
            builder.Services.AddControllersWithViews(options => options.Conventions.Add(modeConvention));
        }
        else
        {
            builder.Services.AddControllers(options => options.Conventions.Add(modeConvention));
        }

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.Configure<LineOptions>(builder.Configuration.GetSection(LineOptions.SectionName));
        builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
        builder.Services.Configure<ContentDownloadOptions>(builder.Configuration.GetSection(ContentDownloadOptions.SectionName));
        builder.Services.Configure<ProfileCacheOptions>(builder.Configuration.GetSection(ProfileCacheOptions.SectionName));
        builder.Services.Configure<DeploymentOptions>(builder.Configuration.GetSection(DeploymentOptions.SectionName));
        builder.Services.Configure<ViewerOptions>(builder.Configuration.GetSection(ViewerOptions.SectionName));
        builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
        builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
        builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
        builder.Services.Configure<HeartbeatOptions>(builder.Configuration.GetSection(HeartbeatOptions.SectionName));
        builder.Services.Configure<EdgeProxyOptions>(builder.Configuration.GetSection(EdgeProxyOptions.SectionName));
        builder.Services.Configure<WebhookSourceOptions>(builder.Configuration.GetSection(WebhookSourceOptions.SectionName));
        builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
        // 單例：金鑰是固定設定值，跟請求無關；MessageDbContext 的建構子也靠 DI 注入同一份實例，
        // 見 MessageDbContextModelCacheKeyFactory 對「模型依 cipher 狀態分開快取」的說明。合併前
        // 收錄端與檢視端各自持有一份，現在單一行程只有一份，跨行程金鑰不一致的風險本身也隨之消失
        builder.Services.AddSingleton<FieldCipher>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ReadinessCache>();
        // 診斷用的 DNS 解析：快取有狀態，必須是單例
        builder.Services.AddSingleton<IDnsLookup, SystemDnsLookup>();
        builder.Services.AddSingleton<OutboundTargetResolver>();
        builder.Services.AddSingleton(ProcessOwnerId.Instance);
        builder.Services.AddTransient<IngestApiKeyHandler>();
        builder.Services.AddTransient<LineAuthorizationHandler>();

        // 需求2：Database:Provider 顯式設定永遠優先；未設定時依 ConnectionStrings:SqlServer 有沒有值
        // 推導（純推導邏輯見 DatabaseProviderResolver，可單元測試）
        var configuredDatabaseProvider = builder.Configuration["Database:Provider"];
        var sqlServerConnectionStringRaw = builder.Configuration.GetConnectionString("SqlServer");
        var hasSqlServerConnectionString = !string.IsNullOrWhiteSpace(sqlServerConnectionStringRaw);
        var (resolvedDatabaseProvider, databaseProviderWasInferred) =
            DatabaseProviderResolver.Resolve(configuredDatabaseProvider, hasSqlServerConnectionString);
        var databaseProvider = resolvedDatabaseProvider; // 可能被下面的救場邏輯覆寫成 "Sqlite"

        var autoMigrate = builder.Configuration.GetValue("Database:AutoMigrate", true);
        var sqliteFallbackConfigured = builder.Configuration["Database:SqliteFallback"] is not null;
        var sqliteFallbackEnabled = builder.Configuration.GetValue("Database:SqliteFallback", true);
        var sqliteFallbackTriggered = false;
        string? sqliteFallbackReason = null;
        var sqliteBusyTimeoutMs = builder.Configuration.GetValue("Database:SqliteBusyTimeoutMs", SqliteBusyTimeoutInterceptor.DefaultBusyTimeoutMs);

        // SQLite 救場：僅 AllInOne。執行中 SQL Server 斷線已由 outbox 緩衝保護（暫時性失敗退避重試、
        // 永不死信，見 OutboxForwarderService），不會掉資料；真正會掉訊息的缺口是「啟動時連不上／
        // schema 不對 → 站台整個起不來 → 連 webhook 都收不到，LINE redelivery 重試有限」。探測（含視
        // AutoMigrate 而定的 schema 驗證）只在啟動這一刻跑一次，決定後行程存續期間不再改變——不做
        // 執行中動態切換：那會讓救場期間寫入的資料跟主資料庫分裂，且 EF provider 在 DI 裡是啟動時
        // 就固定註冊的架構，執行中換不動
        if (databaseProvider == "SqlServer" && deploymentMode is DeploymentMode.AllInOne
            && sqliteFallbackEnabled && hasSqlServerConnectionString)
        {
            sqliteFallbackReason = DatabaseStartupProbe.TryPrepareSqlServer(sqlServerConnectionStringRaw!, autoMigrate);
            if (sqliteFallbackReason is not null)
            {
                sqliteFallbackTriggered = true;
                databaseProvider = "Sqlite";
            }
        }

        var databaseStartupDecision = new DatabaseStartupDecision(
            configuredDatabaseProvider, databaseProvider, databaseProviderWasInferred, hasSqlServerConnectionString,
            sqliteFallbackConfigured, sqliteFallbackEnabled, sqliteFallbackTriggered, sqliteFallbackReason);
        // 給設定頁「主機狀態」曝露目前實際生效的 provider 與救場狀態，見 SettingsController
        builder.Services.AddSingleton(databaseStartupDecision);

        // EdgeProxy 只做一件事：把 webhook 原封轉發給 Edge。下面所有註冊（資料庫、收錄、
        // 媒體下載、名稱／頭貼、心跳、輪詢器、outbox）它一項都用不到，而且**不能**讓它掉進
        // 下面那個為 Edge 寫的 else 分支——那裡會註冊 Ingest:BaseUrl 未設就會炸的具名 client，
        // 以及相依 EdgeChannelState（只在 ReceivesWebhook 時註冊）的 HttpHeartbeatReporter，
        // 兩者都會讓 EdgeProxy 站台啟動失敗。提早返回是唯一安全的做法：與其在下面六個區塊
        // 各加一個排除條件（漏一個就炸），不如在這裡一次切乾淨
        if (deploymentMode is DeploymentMode.EdgeProxy)
        {
            var edgeProxyOptions = builder.Configuration
                .GetSection(EdgeProxyOptions.SectionName).Get<EdgeProxyOptions>() ?? new EdgeProxyOptions();

            builder.Services.AddHttpClient(EdgeProxyOptions.HttpClientName, client =>
            {
                // DeploymentValidator 會在啟動時擋掉沒設或格式錯誤的 TargetBaseUrl；這裡不給
                // 任何預設值——fallback 成 localhost 之類的值等於把 webhook 轉發給自己，
                // 會被同一個中介層再攔再轉，自我遞迴到耗盡連線。寧可在異常路徑丟例外
                var targetBaseUrl = edgeProxyOptions.TargetBaseUrl
                    ?? throw new InvalidOperationException("EdgeProxy:TargetBaseUrl must be set when Deployment:Mode=EdgeProxy.");
                client.BaseAddress = HttpBaseAddress.Create(targetBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, edgeProxyOptions.TimeoutSeconds));
            });

            builder.Services.AddHttpClient(EdgeProxyLineForwarder.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(10);
            })
            // **絕對不可以自動跟隨 redirect**：轉發器的三道 host 檢查只驗第一次請求的 URL，
            // 跟隨 302 等於讓上游決定最終連到哪裡——一個 open redirect 就能讓這台公網主機
            // 去打內網位址並把回應原樣吐回來，允許清單形同虛設。狀態碼本來就會透傳，
            // 3xx 交給呼叫端自己決定要不要跟
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

            return new MessageServiceCoreRegistration(
                databaseStartupDecision, autoMigrate,
                SqliteConnectionString: null, OutboxConnectionString: null, sqliteBusyTimeoutMs);
        }

        // 只在真的要用 Sqlite（HasDatabaseAccess 且 provider!=SqlServer）時才解析並建立 Db\ 目錄——
        // 避免 SqlServer 部署平白多出一個沒人用的空資料夾。Migrate() 那段（見 MigrateMessageServiceDatabase）
        // 跟這裡共用同一份已解析好的連線字串，不重複解析／重複 CreateDirectory
        string? sqliteConnectionString = null;

        // 直連資料庫（AllInOne／Core／Viewer）才需要的一切：主資料庫本身、把 outbox 落地用的
        // DirectIngestSink。媒體下載／頭貼快取的背景服務不在這裡——它們跟著 Line:OutboundHere 走
        // （見下面）；保留期清除與貼圖回填等維護背景服務也不在這裡，它們只在 capabilities.RunsRetention 為真時註冊（比
        // HasDatabaseAccess 窄——貼圖回填與保留清除屬於維護工作，三台拓撲下只由 Core 負責，Viewer 純讀不回填，不該跟 Core 搶著清同一張表或回填貼圖）
        if (capabilities.HasDatabaseAccess)
        {
            // 用衍生類別（實作型別）而非 MessageDbContext 本身註冊，純粹是為了讓 EF migrations
            // 工具能依 CLR 型別區分「這是 SQLite 的 migrations 集合」還是「SQL Server 的」（見
            // Data/Migrations/Sqlite 與 Data/Migrations/SqlServer）。DI 只會把它們解析成
            // MessageDbContext，全站其他地方完全不用知道衍生型別存在。
            if (databaseProvider == "SqlServer")
            {
                builder.Services.AddDbContext<MessageDbContext, SqlServerMessageDbContext>(options =>
                    options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer")));
            }
            else
            {
                sqliteConnectionString = SqliteConnectionStringResolver.Resolve(
                    builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=Db/messages.db",
                    builder.Environment.ContentRootPath);
                builder.Services.AddDbContext<MessageDbContext, SqliteMessageDbContext>(options =>
                {
                    options.UseSqlite(sqliteConnectionString);
                    options.AddInterceptors(new SqliteBusyTimeoutInterceptor(sqliteBusyTimeoutMs));
                });
            }

            builder.Services.AddScoped<IIngestSink, DirectIngestSink>();
        }

        if (capabilities.RunsRetention)
        {
            builder.Services.AddHostedService<RetentionCleanupService>();
            builder.Services.AddHostedService<StickerContentBackfillService>();
        }

        // 檢視端專屬服務：都依賴 MessageDbContext，只在 ViewerEnabled 時註冊——Development 環境下
        // ASP.NET Core 預設會在 Build() 當下驗證所有已註冊服務的相依性都能解析，沒開檢視端的模式
        // 若仍註冊這些會直接啟動失敗
        if (capabilities.ViewerEnabled)
        {
            builder.Services.AddMemoryCache();
            builder.Services.AddScoped<ContentStreamService>();
            builder.Services.AddScoped<IMaskingService, MaskingService>();
            builder.Services.AddScoped<IAnonymousIdentityService, AnonymousIdentityService>();
            // 刪除是不可逆操作，稽核 log 要記來源 IP（本站沒有登入機制，那是唯一的識別）
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddScoped<GroupDeletionService>();
        }

        // 媒體下載／頭貼快取的資料來源：有資料庫就直接查（AllInOne／Core／Viewer，包含
        // Core:OutboundHere=true 這種「Core 端自己也連 LINE」的少見拓撲），沒有資料庫就打 ingest API
        // （Edge）。這裡刻意不看 OutboundHere 或 IngestApiEnabled——IngestController（服務 Edge 端請求）
        // 與 ContentDownloadService／ProfileRefreshService（本機真的要下載）兩種消費者，
        // 只要 HasDatabaseAccess 相同就永遠會選到同一種實作，見 docs/DEPLOYMENT-MODES.md 的推導。
        if (capabilities.HasDatabaseAccess)
        {
            builder.Services.AddScoped<IContentWorkSource, DbContentWorkSource>();
            builder.Services.AddScoped<IProfileStore, DbProfileStore>();
        }
        else
        {
            // 拉取模式下 Edge 不能主動連 Core：待辦由 Core 每次 poll 派下來、下載完放記憶體
            // 暫存等 Core 來取（見 StagingContentWorkSource／EdgeContentStaging）
            // 兩組實作都註冊為具體型別，由 ChannelAware* 依目前通道方向二選一——推送不通時
            // 媒體與名稱／頭貼要跟著反向，不能只有訊息與心跳反轉（見 EdgeChannelState.UsePullResources）
            builder.Services.AddScoped<StagingContentWorkSource>();
            builder.Services.AddScoped<StagingProfileStore>();
            if (ingestOptionsRaw.Channel is not IngestChannel.Pull)
            {
                builder.Services.AddScoped<ApiContentWorkSource>();
                builder.Services.AddScoped<ApiProfileStore>();
            }
            builder.Services.AddScoped<IContentWorkSource, ChannelAwareContentWorkSource>();
            builder.Services.AddScoped<IProfileStore, ChannelAwareProfileStore>();

            // 只有 Edge（沒有本機資料庫）才需要打這兩支具名 HttpClient；Core 端就算日後
            // Core:OutboundHere=true，走的也是上面的 DbContentWorkSource，不會用到它們。
            // X-Ingest-Key 透過 IngestApiKeyHandler 自動附加，確保金鑰更新後即時生效。
            // Pull 模式下 Ingest:BaseUrl 允許留空，這兩支 client 也不會被用到——註冊了反而會在
            // 第一次 CreateClient 時 new Uri("") 炸掉（ChannelAware* 會避開，但心跳等路徑不保證）
            if (ingestOptionsRaw.Channel is not IngestChannel.Pull)
            {
                builder.Services.AddHttpClient("ingest", client =>
                {
                    var baseUrl = ingestOptionsRaw.BaseUrl
                        ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Edge.");
                    client.BaseAddress = HttpBaseAddress.Create(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                }).AddHttpMessageHandler<IngestApiKeyHandler>();
                builder.Services.AddHttpClient("ingest-content", client =>
                {
                    var baseUrl = ingestOptionsRaw.BaseUrl
                        ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Edge.");
                    client.BaseAddress = HttpBaseAddress.Create(baseUrl);
                    // blob 上傳可達數百 MB，比照 LineContentClient 對大檔放寬 timeout 的理由
                    client.Timeout = TimeSpan.FromMinutes(10);
                }).AddHttpMessageHandler<IngestApiKeyHandler>();
            }
        }

        // 需求4：Web 端要能看到另外幾台服務是否正常運作——有資料庫就直接寫 HostHeartbeats，
        // 沒有資料庫（Edge）就打上面已經註冊好的 "ingest" 具名 HttpClient 代寫，見
        // HeartbeatService／IHeartbeatReporter 的兩種實作說明。IHeartbeatStore 額外只在有資料庫時
        // 註冊——IngestController 的 heartbeat 端點（只存在於 AllInOne／Core）跟 DbHeartbeatReporter
        // 共用同一份 upsert 邏輯。
        if (capabilities.HasDatabaseAccess)
        {
            builder.Services.AddScoped<IHeartbeatStore, DbHeartbeatStore>();
            builder.Services.AddScoped<IHeartbeatReporter, DbHeartbeatReporter>();

            // 反向通道：記錄推送心跳的到達時間，決定 EdgePullService 要不要接手輪詢
            builder.Services.AddSingleton<PushHeartbeatTracker>();
        }
        else
        {
            builder.Services.AddScoped<IHeartbeatReporter, HttpHeartbeatReporter>();
        }
        // 測試主機（WebAppFactoryFixture）會把這個關掉，見 HeartbeatOptions.Enabled 說明
        // Pull 模式下 Edge 從不主動連 Core，心跳改由 poll 回應即時計算（見 EdgeController.Poll），
        // 不註冊這個背景服務——註冊了只會每個週期對打不通的 Core 送一次、留下無意義的失敗 log
        var pushesHeartbeat = capabilities.HasDatabaseAccess || ingestOptionsRaw.Channel is not IngestChannel.Pull;
        if (builder.Configuration.GetValue("Heartbeat:Enabled", true) && pushesHeartbeat)
        {
            builder.Services.AddHostedService<HeartbeatService>();
        }

        // Core 端的反向通道輪詢器：只在有資料庫（落地端）且設定了 Edge 位址時才存在——
        // 沒設 Ingest:EdgeBaseUrl 就完全不註冊，行為與沒有這個功能時一致
        if (capabilities.HasDatabaseAccess && !string.IsNullOrWhiteSpace(ingestOptionsRaw.EdgeBaseUrl))
        {
            builder.Services.AddHttpClient(EdgePullService.HttpClientName, client =>
            {
                client.BaseAddress = HttpBaseAddress.Create(ingestOptionsRaw.EdgeBaseUrl!);
                // poll／ack 都是小 JSON 往返，逾時要短——blob 取回另有長逾時的 client，不可共用
                client.Timeout = TimeSpan.FromSeconds(5);
            }).AddHttpMessageHandler<IngestApiKeyHandler>();
            builder.Services.AddHttpClient(EdgePullService.ContentHttpClientName, client =>
            {
                client.BaseAddress = HttpBaseAddress.Create(ingestOptionsRaw.EdgeBaseUrl!);
                // blob 可達數百 MB，比照既有 "ingest-content" 對大檔放寬 timeout 的理由
                client.Timeout = TimeSpan.FromMinutes(10);
            }).AddHttpMessageHandler<IngestApiKeyHandler>();
            builder.Services.AddHostedService<EdgePullService>();
        }

        // 媒體下載／頭貼刷新的入列佇列：這台主機要不要真的做這兩件事只看 OutboundHere，
        // 跟模式或資料庫存取權無關（Core 端也可能 OutboundHere=true）。沒有消費者時换成 Null 實作，
        // 不然 ContentDownloadQueue 的 Channel.CreateUnbounded 會在沒人消費的情況下無上限累積
        // （見 NullContentDownloadQueue／NullProfileRefreshQueue 說明）
        if (capabilities.OutboundHere)
        {
            builder.Services.AddSingleton<IContentDownloadQueue, ContentDownloadQueue>();
            builder.Services.AddSingleton<IProfileRefreshQueue, ProfileRefreshQueue>();

            builder.Services.AddScoped<ILineContentClient, LineContentClient>();
            builder.Services.AddScoped<ILineProfileClient, LineProfileClient>();
            static void ConfigureLineClient(IServiceProvider sp, HttpClient client, string relativeSegment)
            {
                var options = sp.GetRequiredService<IOptionsMonitor<LineOptions>>().CurrentValue;
                if (options.OutboundVia is LineOutboundVia.EdgeProxy && !string.IsNullOrWhiteSpace(options.OutboundProxyBaseUrl))
                {
                    var proxyBaseUri = HttpBaseAddress.Create(options.OutboundProxyBaseUrl);
                    client.BaseAddress = new Uri(proxyBaseUri, relativeSegment);
                }
            }

            // 影片/檔案原檔可達數百 MB，預設 100 秒 timeout 不夠
            builder.Services.AddHttpClient(LineContentClient.HttpClientName, (sp, client) =>
            {
                ConfigureLineClient(sp, client, "line/data/");
                client.Timeout = TimeSpan.FromMinutes(10);
            }).AddHttpMessageHandler<LineAuthorizationHandler>();
            builder.Services.AddHttpClient(LineContentClient.StickerHttpClientName, (sp, client) =>
            {
                ConfigureLineClient(sp, client, "line/sticker/");
            });
            builder.Services.AddHttpClient(LineProfileClient.HttpClientName, (sp, client) =>
            {
                ConfigureLineClient(sp, client, "line/api/");
            }).AddHttpMessageHandler<LineAuthorizationHandler>();
            builder.Services.AddHttpClient(LineProfileClient.ImageHttpClientName,
                client => client.MaxResponseContentBufferSize = LineProfileClient.MaxImageSize);

            builder.Services.AddHostedService<ContentDownloadService>();
            builder.Services.AddHostedService<ProfileRefreshService>();
            // 補刷的產物是「丟進刷新佇列的工作」，只有真的會消費佇列、也真的能打 LINE 的
            // 那台跑才有意義；資料來源由 IProfileStore 決定，AllInOne 查本機資料庫、Edge 打 Core 的 ingest API。
            builder.Services.AddHostedService<ProfileBackfillService>();
        }
        else
        {
            builder.Services.AddSingleton<IContentDownloadQueue, NullContentDownloadQueue>();
            builder.Services.AddSingleton<IProfileRefreshQueue, NullProfileRefreshQueue>();
        }

        // Edge 模式：把 outbox 排出的事件推去 Core 端主機的 ingest API，取代上面 HasDatabaseAccess
        // 分支註冊的 DirectIngestSink——兩者互斥（依模式二選一），是 IIngestSink 唯一有兩套實作的地方
        if (deploymentMode is DeploymentMode.Edge)
        {
            builder.Services.AddHttpClient<IIngestSink, HttpIngestSink>(client =>
            {
                // DeploymentValidator 會在啟動時確保 Ingest:BaseUrl 已設定；這裡的例外只在
                // validator 檢查之前就有東西搶著解析 HttpClient 的異常路徑上才有意義
                var baseUrl = ingestOptionsRaw.BaseUrl
                    ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Edge.");
                client.BaseAddress = HttpBaseAddress.Create(baseUrl);
                // payload 只有訊息中繼資料（無媒體 blob），不需要比照 LineContentClient 的長 timeout
                client.Timeout = TimeSpan.FromSeconds(30);
            }).AddHttpMessageHandler<IngestApiKeyHandler>();

            builder.Services.AddHttpClient("edge-proxy-errors", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(5);
            });
        }

        // outbox 一律是 SQLite（收錄端本機緩衝，跟主資料庫 provider 無關），Migrate() 那段（見
        // MigrateMessageServiceDatabase）共用這份已解析好的連線字串
        string? outboxConnectionString = null;

        // 收 webhook（AllInOne／Edge）才需要的一切：簽章驗證、事件處理器、本機 outbox 與排空服務。
        // outbox 跟上面的 MessageDbContext 完全獨立——即使這台連不到主資料庫／ingest API，
        // webhook 收進來的事件也不會遺失
        if (capabilities.ReceivesWebhook)
        {
            builder.Services.AddScoped<ILineSignatureValidator, LineSignatureValidator>();
            builder.Services.AddScoped<IWebhookEventHandler, WebhookEventHandler>();

            outboxConnectionString = SqliteConnectionStringResolver.Resolve(
                builder.Configuration.GetConnectionString("Outbox") ?? "Data Source=Db/outbox.db",
                builder.Environment.ContentRootPath);
            builder.Services.AddDbContext<OutboxDbContext>(options =>
            {
                options.UseSqlite(outboxConnectionString);
                options.AddInterceptors(new SqliteBusyTimeoutInterceptor(sqliteBusyTimeoutMs));
            });
            builder.Services.AddSingleton<IOutboxSignal, OutboxSignal>();
            builder.Services.AddScoped<IOutboxWriter, SqliteOutboxWriter>();

            // 通道狀態：Auto 模式下推送失敗要暫停轉發、每隔一個探測週期再試（見 EdgeChannelState）
            builder.Services.AddSingleton<EdgeChannelState>();

            // 拉取模式的暫存區：Core 派工進來、產出的結果等 Core 取走
            builder.Services.AddSingleton<EdgeContentStaging>();
            builder.Services.AddSingleton<EdgeProfileStaging>();

            // Pull 模式下 Edge 從不主動連 Core，轉發器整個不註冊——webhook 照收、寫進 outbox，
            // 由 Core 端輪詢取走
            if (ingestOptionsRaw.Channel is not IngestChannel.Pull)
            {
                builder.Services.AddHostedService<OutboxForwarderService>();
            }
        }

        return new MessageServiceCoreRegistration(
            databaseStartupDecision, autoMigrate, sqliteConnectionString, outboxConnectionString, sqliteBusyTimeoutMs);
    }
}
