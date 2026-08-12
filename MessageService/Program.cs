using MessageService.Data;
using MessageService.Middleware;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
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

// 同樣是「容器建好之前就要知道」的原始讀取——ingest API 的 controller 要不要存在
// （見 DeploymentModeConvention）取決於金鑰有沒有配置，不是取決於模式本身
var ingestOptionsRaw = builder.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>()
    ?? new IngestOptions();
var ingestApiEnabled = !string.IsNullOrWhiteSpace(ingestOptionsRaw.ApiKey);

// Add services to the container.

builder.Services.AddControllers(options =>
    options.Conventions.Add(new DeploymentModeConvention(deploymentMode, ingestApiEnabled)));
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

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";

// 直連資料庫（Full／Db 模式）才需要的一切：主資料庫本身、內容下載／頭貼快取用到的佇列與
// LINE 用戶端、三個背景服務、把 outbox 落地用的 DirectIngestSink
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

    builder.Services.AddSingleton<IContentDownloadQueue, ContentDownloadQueue>();
    builder.Services.AddSingleton<IProfileRefreshQueue, ProfileRefreshQueue>();
    builder.Services.AddScoped<ILineContentClient, LineContentClient>();
    builder.Services.AddScoped<ILineProfileClient, LineProfileClient>();
    // 影片/檔案原檔可達數百 MB，預設 100 秒 timeout 不夠
    builder.Services.AddHttpClient(LineContentClient.HttpClientName,
        client => client.Timeout = TimeSpan.FromMinutes(10));
    builder.Services.AddHttpClient(LineProfileClient.HttpClientName);

    builder.Services.AddScoped<IIngestSink, DirectIngestSink>();

    builder.Services.AddHostedService<ContentDownloadService>();
    builder.Services.AddHostedService<RetentionCleanupService>();
    builder.Services.AddHostedService<ProfileRefreshService>();
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
}

if (hasDatabaseAccess && databaseProvider == "Sqlite")
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
    dbContext.Database.EnsureCreated();
}

if (receivesWebhook)
{
    using var scope = app.Services.CreateScope();
    var outboxDbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
    outboxDbContext.Database.EnsureCreated();

    // EnsureCreated() 只在資料庫檔案完全不存在時建表——Stage 1 就已經部署過的既有 outbox.db
    // 補上本次新增的 DeadLetteredAt 欄位要在這裡另外處理，見 OutboxSchemaUpgrader 的說明
    var outboxConnectionString = builder.Configuration.GetConnectionString("Outbox") ?? "Data Source=outbox.db";
    OutboxSchemaUpgrader.EnsureDeadLetterColumn(outboxConnectionString);

    // 死信不會自動消失，只會在這裡的啟動 log 被看到——沒有專用的重送介面，量大時要靠這行
    // log 提醒維運人員去查，避免累積到某天才被發現有一批訊息早就不再重試了
    var deadLetterCount = outboxDbContext.Entries.Count(e => e.DeadLetteredAt != null);
    if (deadLetterCount > 0)
    {
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        startupLogger.LogWarning(
            "Outbox has {Count} dead-lettered entries awaiting manual review (see LastError column in outbox.db)",
            deadLetterCount);
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// ingest API 的守門只掛在 /api/ingest 路徑，不影響 LINE webhook 端點（webhook 靠簽章驗證，
// 兩者是完全獨立的防護層）。只在 hasDatabaseAccess（IngestController 可能存在的模式）
// 才註冊——Line 模式的主機永遠不會收到這類請求，讓它的 IngestIpAllowlistMiddleware
// 在啟動時印「AllowedClientIps 是空的」警告只會誤導維運人員以為設定漏了
if (hasDatabaseAccess)
{
    app.UseWhen(
        context => context.Request.Path.StartsWithSegments("/api/ingest"),
        ingestPipeline =>
        {
            ingestPipeline.UseMiddleware<IngestIpAllowlistMiddleware>();
            ingestPipeline.UseMiddleware<IngestApiKeyMiddleware>();
        });
}

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
