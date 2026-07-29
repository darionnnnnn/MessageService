using MessageService.Data;
using MessageService.Options;
using MessageService.Services;
using Microsoft.EntityFrameworkCore;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseNLog();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<LineOptions>(builder.Configuration.GetSection(LineOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<ContentDownloadOptions>(builder.Configuration.GetSection(ContentDownloadOptions.SectionName));
builder.Services.Configure<ProfileCacheOptions>(builder.Configuration.GetSection(ProfileCacheOptions.SectionName));

var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
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
builder.Services.AddScoped<ILineSignatureValidator, LineSignatureValidator>();
builder.Services.AddScoped<IWebhookEventHandler, WebhookEventHandler>();
builder.Services.AddScoped<ILineContentClient, LineContentClient>();
builder.Services.AddScoped<ILineProfileClient, LineProfileClient>();
// 影片/檔案原檔可達數百 MB，預設 100 秒 timeout 不夠
builder.Services.AddHttpClient(LineContentClient.HttpClientName,
    client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient(LineProfileClient.HttpClientName);

builder.Services.AddHostedService<ContentDownloadService>();
builder.Services.AddHostedService<RetentionCleanupService>();
builder.Services.AddHostedService<ProfileRefreshService>();

var app = builder.Build();

if (databaseProvider == "Sqlite")
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
    dbContext.Database.EnsureCreated();
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
