using MessageService.Data;
using MessageService.Web.Middleware;
using MessageService.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Host.UseNLog();

// Add services to the container.
builder.Services.AddControllersWithViews();

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

builder.Services.AddScoped<ContentStreamService>();
builder.Services.AddScoped<IMaskingService, MaskingService>();

var app = builder.Build();

// 部署在反向代理後面時才需要開啟，讓 IpAllowlistMiddleware 看到的是真實來源 IP 而非代理 IP
if (builder.Configuration.GetValue<bool>("UseForwardedHeaders"))
{
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor
    });
}

// 全站沒有登入機制，IP 白名單是最低防護，必須放在管線最前面擋下所有請求
app.UseMiddleware<IpAllowlistMiddleware>();

// 要包住後面所有中介層與 controller，才能攔到它們拋出的請求取消例外
app.UseMiddleware<CancelledRequestMiddleware>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
