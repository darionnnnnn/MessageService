using MessageService.Data;
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

// Add services to the container.

builder.Services.AddControllers(options =>
    options.Conventions.Add(new DeploymentModeConvention(deploymentMode)));
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
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
