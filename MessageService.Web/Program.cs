using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Middleware;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Web.Middleware;
using MessageService.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseNLog();

// 部署角色：見 Options/DeploymentOptions.cs 與 docs/DEPLOYMENT-MODES.md。原始讀取（不透過
// DI）是因為下面的 AddControllers 需要在容器建好之前就知道要不要掛路由 convention。
var deploymentOptions = builder.Configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>()
    ?? new DeploymentOptions();
var deploymentMode = deploymentOptions.Mode;
var hasDatabaseAccess = deploymentMode is DeploymentMode.Full or DeploymentMode.Db;
var receivesWebhook = deploymentMode is DeploymentMode.Full or DeploymentMode.Line;
// Stage 1（專案合併）：檢視端掛載條件暫時沿用 hasDatabaseAccess——這正是合併前
// MessageService.Web 原本就唯一能部署的拓撲（Web 只會查本機 DB，從不透過 ingest API 遠端查）。
// Stage 2 會把它換成獨立的 Viewer capability（見 docs/CONSOLIDATION-PLAN.md 階段 2），
// 好讓「Core 端關掉檢視端、另外一台 Viewer 端開」這種三台拓撲也能表達。
var viewerEnabled = hasDatabaseAccess;

// 同樣是「容器建好之前就要知道」的原始讀取——ingest API 的 controller 要不要存在
// （見 DeploymentModeConvention）取決於金鑰有沒有配置，不是取決於模式本身
var ingestOptionsRaw = builder.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>()
    ?? new IngestOptions();
var ingestApiEnabled = !string.IsNullOrWhiteSpace(ingestOptionsRaw.ApiKey);

// 這台主機要不要對外呼叫 LINE API（媒體下載＋頭貼快取）。拆機後 Line 端也可能是負責連
// LINE 的那一端，所以獨立看這個設定，不跟 hasDatabaseAccess 綁在一起
var lineOptionsRaw = builder.Configuration.GetSection(LineOptions.SectionName).Get<LineOptions>()
    ?? new LineOptions();
var outboundHere = lineOptionsRaw.OutboundHere;

// Add services to the container.

// MVC 檢視（Home/Error 頁＋靜態資源管線）只有檢視端要跑——純 Line／Edge 主機不需要、
// 也不該暴露這些頁面。ingest／webhook 用的 attribute-routed API controller 兩種註冊方式都會掛，
// 差別只在有沒有多帶 Razor 檢視引擎與靜態資源那一套
var modeConvention = new DeploymentModeConvention(deploymentMode, ingestApiEnabled);
if (viewerEnabled)
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
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.Configure<EncryptionOptions>(builder.Configuration.GetSection(EncryptionOptions.SectionName));
// 單例：金鑰是固定設定值，跟請求無關；MessageDbContext 的建構子也靠 DI 注入同一份實例，
// 見 MessageDbContextModelCacheKeyFactory 對「模型依 cipher 狀態分開快取」的說明。合併前
// 收錄端與檢視端各自持有一份，現在單一行程只有一份，跨行程金鑰不一致的風險本身也隨之消失
builder.Services.AddSingleton<FieldCipher>();

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";

// 直連資料庫（Full／Db 模式）才需要的一切：主資料庫本身、把 outbox 落地用的
// DirectIngestSink、保留期清除。媒體下載／頭貼快取的背景服務不在這裡——它們現在跟著
// Line:OutboundHere 走（見下面），拆機後 Line 端也可能是負責連 LINE 的那一端
if (hasDatabaseAccess)
{
    builder.Services.AddDbContext<MessageDbContext>(options =>
    {
        if (databaseProvider == "SqlServer")
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer"));
        }
        else
        {
            options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite"));
        }
    });

    builder.Services.AddScoped<IIngestSink, DirectIngestSink>();
    builder.Services.AddHostedService<RetentionCleanupService>();
}

// 檢視端專屬服務：都依賴 MessageDbContext，只在 viewerEnabled 時註冊——Development 環境下
// ASP.NET Core 預設會在 Build() 當下驗證所有已註冊服務的相依性都能解析，沒資料庫的模式
// 若仍註冊這些會直接啟動失敗
if (viewerEnabled)
{
    builder.Services.AddScoped<ContentStreamService>();
    builder.Services.AddScoped<IMaskingService, MaskingService>();
    builder.Services.AddScoped<IAnonymousIdentityService, AnonymousIdentityService>();
}

// 媒體下載／頭貼快取的資料來源：有資料庫就直接查（Full／Db，包含 Db:OutboundHere=true
// 這種「Db 端自己也連 LINE」的少見拓撲），沒有資料庫就打 ingest API（純 Line 模式）。
// 這裡刻意不看 outboundHere 或 ingestApiEnabled——IngestController（服務 Line 端請求）
// 與 ContentDownloadService／ProfileRefreshService（本機真的要下載）兩種消費者，
// 只要 hasDatabaseAccess 相同就永遠會選到同一種實作，見 docs/DEPLOYMENT-MODES.md 的推導。
if (hasDatabaseAccess)
{
    builder.Services.AddScoped<IContentWorkSource, DbContentWorkSource>();
    builder.Services.AddScoped<IProfileStore, DbProfileStore>();
}
else
{
    builder.Services.AddScoped<IContentWorkSource, ApiContentWorkSource>();
    builder.Services.AddScoped<IProfileStore, ApiProfileStore>();

    // 只有純 Line 模式（沒有本機資料庫）才需要打這兩支具名 HttpClient；Db 端就算日後
    // Db:OutboundHere=true，走的也是上面的 DbContentWorkSource，不會用到它們。
    // X-Ingest-Key 在這裡當預設標頭設一次，而不是要求 ApiContentWorkSource／ApiProfileStore
    // 每個方法自己記得加——這是端到端演練實際踩到的 bug：一開始沒設，兩個類別的
    // 所有請求都被 IngestApiKeyMiddleware 擋成 401，只有真的起兩個行程互打才測得出來
    var ingestApiKeyForClient = ingestOptionsRaw.ApiKey ?? "";
    builder.Services.AddHttpClient("ingest", client =>
    {
        var baseUrl = ingestOptionsRaw.BaseUrl
            ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Line.");
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-Ingest-Key", ingestApiKeyForClient);
    });
    builder.Services.AddHttpClient("ingest-content", client =>
    {
        var baseUrl = ingestOptionsRaw.BaseUrl
            ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Line.");
        client.BaseAddress = new Uri(baseUrl);
        // blob 上傳可達數百 MB，比照 LineContentClient 對大檔放寬 timeout 的理由
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("X-Ingest-Key", ingestApiKeyForClient);
    });
}

// 媒體下載／頭貼刷新的入列佇列：這台主機要不要真的做這兩件事只看 Line:OutboundHere，
// 跟模式或資料庫存取權無關（Db 端也可能 OutboundHere=true）。沒有消費者時换成 Null 實作，
// 不然 ContentDownloadQueue 的 Channel.CreateUnbounded 會在沒人消費的情況下無上限累積
// （見 NullContentDownloadQueue／NullProfileRefreshQueue 說明）
if (outboundHere)
{
    builder.Services.AddSingleton<IContentDownloadQueue, ContentDownloadQueue>();
    builder.Services.AddSingleton<IProfileRefreshQueue, ProfileRefreshQueue>();

    builder.Services.AddScoped<ILineContentClient, LineContentClient>();
    builder.Services.AddScoped<ILineProfileClient, LineProfileClient>();
    // 影片/檔案原檔可達數百 MB，預設 100 秒 timeout 不夠
    builder.Services.AddHttpClient(LineContentClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromMinutes(10));
    builder.Services.AddHttpClient(LineProfileClient.HttpClientName);

    builder.Services.AddHostedService<ContentDownloadService>();
    builder.Services.AddHostedService<ProfileRefreshService>();
}
else
{
    builder.Services.AddSingleton<IContentDownloadQueue, NullContentDownloadQueue>();
    builder.Services.AddSingleton<IProfileRefreshQueue, NullProfileRefreshQueue>();
}

// Line 模式：把 outbox 排出的事件推去 Db 端主機的 ingest API，取代上面 hasDatabaseAccess
// 分支註冊的 DirectIngestSink——兩者互斥（依模式二選一），是 IIngestSink 唯一有兩套實作的地方
if (deploymentMode == DeploymentMode.Line)
{
    builder.Services.AddHttpClient<IIngestSink, HttpIngestSink>(client =>
    {
        // DeploymentValidator 會在啟動時確保 Ingest:BaseUrl 已設定；這裡的例外只在
        // validator 檢查之前就有東西搶著解析 HttpClient 的異常路徑上才有意義
        var baseUrl = ingestOptionsRaw.BaseUrl
            ?? throw new InvalidOperationException("Ingest:BaseUrl must be set when Deployment:Mode=Line.");
        client.BaseAddress = new Uri(baseUrl);
        // payload 只有訊息中繼資料（無媒體 blob），不需要比照 LineContentClient 的長 timeout
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}

// 收 webhook（Full／Line 模式）才需要的一切：簽章驗證、事件處理器、本機 outbox 與排空服務。
// outbox 跟上面的 MessageDbContext 完全獨立——即使這台連不到主資料庫／ingest API，
// webhook 收進來的事件也不會遺失
if (receivesWebhook)
{
    builder.Services.AddScoped<ILineSignatureValidator, LineSignatureValidator>();
    builder.Services.AddScoped<IWebhookEventHandler, WebhookEventHandler>();

    builder.Services.AddDbContext<OutboxDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("Outbox") ?? "Data Source=outbox.db"));
    builder.Services.AddSingleton<IOutboxSignal, OutboxSignal>();
    builder.Services.AddScoped<IOutboxWriter, SqliteOutboxWriter>();
    builder.Services.AddHostedService<OutboxForwarderService>();
}

var app = builder.Build();

using (var validationScope = app.Services.CreateScope())
{
    var validationLogger = validationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var lineOptions = validationScope.ServiceProvider.GetRequiredService<IOptions<LineOptions>>().Value;
    var ingestOptions = validationScope.ServiceProvider.GetRequiredService<IOptions<IngestOptions>>().Value;
    DeploymentValidator.Validate(deploymentOptions, lineOptions, ingestOptions, validationLogger);

    // FieldCipher 是單例，第一次被解析時才會驗證 Encryption:Key（Enabled=true 但金鑰缺漏／
    // 格式錯誤會在建構子裡丟例外）——這裡強制在啟動當下就解析一次，壞設定要讓服務直接
    // 啟動失敗，不要等到第一則訊息進來才在背景任務裡炸開
    validationScope.ServiceProvider.GetRequiredService<FieldCipher>();
}

if (hasDatabaseAccess && databaseProvider == "Sqlite")
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
    dbContext.Database.EnsureCreated();

    // EnsureCreated() 只在資料庫檔案完全不存在時建表——既有的 messages.db 補上本輪新增的
    // 欄位／索引要在這裡另外處理，見 MessageDbSchemaUpgrader 的說明
    var messageDbConnectionString = builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=messages.db";
    MessageDbSchemaUpgrader.EnsureSchema(messageDbConnectionString);
}

if (receivesWebhook)
{
    using var scope = app.Services.CreateScope();
    var outboxDbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
    outboxDbContext.Database.EnsureCreated();

    // EnsureCreated() 只在資料庫檔案完全不存在時建表——既有的 outbox.db 補上新增的
    // DeadLetteredAt 欄位要在這裡另外處理，見 OutboxSchemaUpgrader 的說明
    var outboxConnectionString = builder.Configuration.GetConnectionString("Outbox") ?? "Data Source=outbox.db";
    OutboxSchemaUpgrader.EnsureDeadLetterColumn(outboxConnectionString);

    // webhook 執行緒寫、forwarder 執行緒讀刪；rollback journal 模式下兩邊會互相 block
    // （busy_timeout 預設 30 秒，遠超 LINE 的 webhook 逾時），WAL 讓讀寫不互相阻塞
    OutboxSchemaUpgrader.EnableWalMode(outboxConnectionString);

    // 死信不會自動消失，只會在 OutboxForwarderService 的 log 被看到（啟動時先報一次、
    // 之後每小時再報一次）——沒有專用的重送介面，量大時要靠那行 log 提醒維運人員去查
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // /Home/Error 頁只有檢視端才存在（HomeController 在非 viewerEnabled 的模式下被
    // DeploymentModeConvention 移除）；純 ingest／webhook 主機交給預設的例外處理行為即可
    if (viewerEnabled)
    {
        app.UseExceptionHandler("/Home/Error");
    }
    app.UseHsts();
}

app.UseHttpsRedirection();

// 部署在反向代理後面時才需要開啟，讓下面的白名單中介層看到的是真實來源 IP 而非代理 IP
if (builder.Configuration.GetValue<bool>("UseForwardedHeaders"))
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor
    });
}

// 檢視端 IP 白名單（沒有登入機制時的最低防護）：只包住非 webhook／非 ingest 的路徑——
// 這兩類端點各自有自己的防護層（簽章驗證／IP 白名單＋金鑰），且來源網段通常跟辦公室
// LAN（檢視端使用者）完全不同，不能共用同一份清單
if (viewerEnabled)
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/api/line")
            && !context.Request.Path.StartsWithSegments("/api/ingest"),
        viewerPipeline => viewerPipeline.UseMiddleware<IpAllowlistMiddleware>(
            new IpAllowlistOptions("Viewer:AllowedClientIps", "viewer")));
}

// 要包住後面所有中介層與 controller，才能攔到它們拋出的請求取消例外
app.UseMiddleware<CancelledRequestMiddleware>();

// ingest API 的守門只掛在 /api/ingest 路徑，不影響 LINE webhook 端點（webhook 靠簽章驗證，
// 兩者是完全獨立的防護層）。只在 hasDatabaseAccess（IngestController 可能存在的模式）
// 才註冊——Line 模式的主機永遠不會收到這類請求，讓它的白名單中介層在啟動時印
// 「AllowedClientIps 是空的」警告只會誤導維運人員以為設定漏了
if (hasDatabaseAccess)
{
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api/ingest"),
        ingestPipeline =>
        {
            ingestPipeline.UseMiddleware<IpAllowlistMiddleware>(
                new IpAllowlistOptions("Ingest:AllowedClientIps", "ingest"));
            ingestPipeline.UseMiddleware<IngestApiKeyMiddleware>();
        });
}

app.UseRouting();
app.UseAuthorization();

if (viewerEnabled)
{
    app.MapStaticAssets();
}

app.MapControllers();

if (viewerEnabled)
{
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets();
}

app.Run();

public partial class Program;
