using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Configuration;
using MessageService.Web.Startup;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

// 合併前 AllowedClientIps 是檢視端與 ingest API 各自一份 appsettings.json 裡的同名 key，
// 互不影響；合併成單一 appsettings.json 後，這個舊 key 一旦還有值，會被誤以為同時套用到
// 兩邊——寧可直接擋啟動，也不要讓拆機部署的白名單被悄悄共用（見 docs/history/CONSOLIDATION-PLAN.md）
var legacyAllowedClientIps = builder.Configuration.GetSection("AllowedClientIps").Get<string[]>();
if (legacyAllowedClientIps is { Length: > 0 })
{
    throw new InvalidOperationException(
        "偵測到舊版設定鍵 AllowedClientIps（合併前檢視端與 ingest API 各自的白名單）。這個 key 已拆成 " +
        "Viewer:AllowedClientIps（檢視端）與 Ingest:AllowedClientIps（ingest API），請把原本的值搬到" +
        "對應的新 key 並移除舊 key。");
}

builder.Logging.ClearProviders();
builder.Host.UseNLog();

// 部署角色：見 Options/DeploymentOptions.cs 與 docs/DEPLOYMENT-MODES.md。原始讀取（不透過
// DI）是因為 AddMessageServiceCore 內的 AddControllers 需要在容器建好之前就知道要不要掛路由
// convention。
var deploymentOptions = builder.Configuration.GetSection(DeploymentOptions.SectionName).Get<DeploymentOptions>()
    ?? new DeploymentOptions();
var deploymentMode = deploymentOptions.Mode;

// DeploymentMode.Full/Line/Db 是舊名，跟新名稱共用底層數值——.NET 設定綁定本身就能接受舊名
// （Enum.TryParse 認名稱、不認底層值），這裡另外偵測「用的是舊名」純粹是為了記一行提醒用的
// Warning（見 ValidateMessageServiceStartup），不影響任何實際行為
var rawModeValue = builder.Configuration["Deployment:Mode"];
var usedLegacyModeName = rawModeValue is not null
    && new[] { "Full", "Line", "Db" }.Contains(rawModeValue.Trim(), StringComparer.OrdinalIgnoreCase);

// 加密設定檔（Edge 專屬）：疊在 appsettings 之上，支援熱生效。
// 僅在 Deployment:Mode=Edge 時加入設定來源鏈最後面（優先權最高），並註冊 EdgeSettingsStore 單例供管理設定。
if (deploymentMode is DeploymentMode.Edge)
{
    var encryptedSettingsPath = EncryptedSettingsFile.ResolvePath(builder.Environment.ContentRootPath);
    // 這裡不掃 builder.Services 找已註冊的 ISettingsProtector——測試是在 WithWebHostBuilder
    // 的 callback 裡註冊的，那比這裡晚跑，掃了永遠命中不到。測試改用 EdgeSettingsStore
    // 建構時的 SetProtector 覆寫（見 EncryptedSettingsSource）。
    //
    // 非 Windows 沒有 DPAPI：這條路徑上機密會以明文落地，所以不靜默降級——
    // 除非顯式設定 EdgeAdmin:AllowPlaintextSettings=true，否則直接擋啟動
    ISettingsProtector encryptedSettingsProtector;
    if (OperatingSystem.IsWindows())
    {
        encryptedSettingsProtector = new DpapiSettingsProtector();
    }
    else if (builder.Configuration.GetValue("EdgeAdmin:AllowPlaintextSettings", false))
    {
        encryptedSettingsProtector = new PlaintextSettingsProtector();
    }
    else
    {
        throw new InvalidOperationException(
            "Deployment:Mode=Edge 的加密設定檔需要 Windows DPAPI，這個作業系統不支援。" +
            "若確定要讓設定以明文落地（僅限測試環境），請設定 EdgeAdmin:AllowPlaintextSettings=true。");
    }

    var encryptedSource = new EncryptedSettingsConfigurationSource(encryptedSettingsPath, encryptedSettingsProtector);
    ((IConfigurationBuilder)builder.Configuration).Add(encryptedSource);

    builder.Services.AddSingleton(encryptedSettingsProtector);
    builder.Services.AddSingleton(sp => new EdgeSettingsStore(
        encryptedSettingsPath,
        sp.GetRequiredService<ISettingsProtector>(),
        encryptedSource.Provider,
        sp.GetRequiredService<ILogger<EdgeSettingsStore>>()));
}

// 同樣是「容器建好之前就要知道」的原始讀取——各能力是否開啟只取決於模式與這些 override 設定，
// 不需要等 DI 容器建好；DeploymentCapabilities.Derive 是全站唯一的推導點（見該類別說明）
var ingestOptionsRaw = builder.Configuration.GetSection(IngestOptions.SectionName).Get<IngestOptions>()
    ?? new IngestOptions();
var lineOptionsRaw = builder.Configuration.GetSection(LineOptions.SectionName).Get<LineOptions>()
    ?? new LineOptions();
var viewerOptionsRaw = builder.Configuration.GetSection(ViewerOptions.SectionName).Get<ViewerOptions>()
    ?? new ViewerOptions();
var capabilities = DeploymentCapabilities.Derive(deploymentMode, lineOptionsRaw, viewerOptionsRaw, ingestOptionsRaw);

// DI 註冊矩陣（含資料庫 provider 推導與 SQLite 救場探測）全部收斂在這個 extension method，
// 見 MessageServiceCoreServiceCollectionExtensions 的說明
var registration = builder.AddMessageServiceCore(capabilities, deploymentMode, ingestOptionsRaw);

var app = builder.Build();

app.ValidateMessageServiceStartup(deploymentOptions, deploymentMode, usedLegacyModeName, rawModeValue, registration);
app.MigrateMessageServiceDatabase(capabilities, registration);
app.UseMessageServicePipeline(capabilities, deploymentMode);

app.Run();

public partial class Program;
