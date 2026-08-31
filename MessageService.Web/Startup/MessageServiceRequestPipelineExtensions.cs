using System.Net;
using MessageService.Data;
using MessageService.Middleware;
using MessageService.Options;
using MessageService.Services;
using MessageService.Web.Middleware;
using MessageService.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;

namespace MessageService.Web.Startup;

/// <summary>Program.cs 里純機械式拆出來的結果——不改任何邏輯／順序／條件，只是把 HTTP 請求
/// 管線組裝這段搬進這個檔案。</summary>
public static class MessageServiceRequestPipelineExtensions
{
    public static void UseMessageServicePipeline(
        this WebApplication app, DeploymentCapabilities capabilities, DeploymentMode deploymentMode)
    {
        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        else
        {
            // /Home/Error 頁只有檢視端才存在（HomeController 在 ViewerEnabled=false 的模式下被
            // DeploymentModeConvention 移除）；純 ingest／webhook 主機交給預設的例外處理行為即可
            if (capabilities.ViewerEnabled)
            {
                app.UseExceptionHandler("/Home/Error");
            }
            app.UseHsts();
        }

        // IIS／反向代理直接處理 TLS 時不需要應用層再轉址一次——多這一跳在 webhook 收錄端會導致
        // LINE 收到 307 而不是預期的回應（LINE 不跟隨轉址，等於完全收不到訊息，而且 log 上看不出
        // 異常）。裸 Kestrel 沒有前端 TLS 才需要開，預設關。
        if (app.Configuration.GetValue<bool>("Http:UseHttpsRedirection"))
        {
            app.UseHttpsRedirection();
        }

        // 部署在反向代理後面時才需要開啟，讓下面的白名單中介層看到的是真實來源 IP 而非代理 IP。
        // ASP.NET Core 的 ForwardedHeadersOptions 預設只信任 loopback 送來的轉發標頭——單開這個
        // 開關不設 KnownProxies／KnownNetworks 等於沒開（中介層看到的仍是代理 IP，白名單全擋），
        // 這正是之前留給「有真實需求再做」的坑，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次B
        if (app.Configuration.GetValue<bool>("UseForwardedHeaders"))
        {
            var forwardedHeadersOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor
            };

            var knownProxies = app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            foreach (var entry in knownProxies)
            {
                if (!IPAddress.TryParse(entry, out var address))
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownProxies 有一個位址解析失敗：\"{entry}\"。請確認是合法的 IP 位址" +
                        "（單一位址，不接受 CIDR——網段請填到 ForwardedHeaders:KnownNetworks）。");
                }
                forwardedHeadersOptions.KnownProxies.Add(address);
            }

            var knownNetworks = app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
            foreach (var entry in knownNetworks)
            {
                // System.Net.IPNetwork（KnownIPNetworks 用的型別）不是 ForwardedHeadersOptions.KnownNetworks
                // 舊版用的 Microsoft.AspNetCore.HttpOverrides.IPNetwork——後者已標記 Obsolete，改用新版；
                // 這裡刻意用完整型別名稱，避免這個檔案同時 using 兩個命名空間時解析到錯的那個 IPNetwork
                if (!System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    // IPNetwork 要求主機位元全為 0（嚴格 CIDR），跟 IpAllowlistMiddleware 對白名單的
                    // 解析原則一致——這是安全設定，寧可啟動失敗也不要一條規則悄悄失效
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownNetworks 有一條 CIDR 網段解析失敗：\"{entry}\"。IPNetwork 要求主機位元" +
                        "全為 0，例如 \"10.1.0.5/24\" 請改成 \"10.1.0.0/24\"（若只要允許單一位址則改成 \"10.1.0.5/32\"）。");
                }
                forwardedHeadersOptions.KnownIPNetworks.Add(network);
            }

            if (forwardedHeadersOptions.KnownProxies.Count == 0 && forwardedHeadersOptions.KnownIPNetworks.Count == 0)
            {
                app.Logger.LogWarning(
                    "UseForwardedHeaders 已開啟，但 ForwardedHeaders:KnownProxies／KnownNetworks 都是空的——" +
                    "ASP.NET Core 預設只信任 loopback，上游代理送來的 X-Forwarded-For 會被忽略，此開關目前等於沒開。" +
                    "請設定其中一項為實際反向代理的位址或網段。");
            }

            app.UseForwardedHeaders(forwardedHeadersOptions);
        }

        // 檢視端 IP 白名單（沒有登入機制時的最低防護）：只包住非 webhook／非 ingest／非健康檢查的路徑——
        // 前兩類端點各自有自己的防護層（簽章驗證／IP 白名單＋金鑰），且來源網段通常跟辦公室
        // LAN（檢視端使用者）完全不同，不能共用同一份清單；健康檢查端點（/healthz 與 /healthz/ready）
        // 同樣必須排除，因為監控系統與負載平衡器的來源 IP 通常不在檢視端的白名單內。
        if (capabilities.ViewerEnabled)
        {
            app.UseWhen(
                context => !context.Request.Path.StartsWithSegments("/api/line")
                    && !context.Request.Path.StartsWithSegments("/api/ingest")
                    && !context.Request.Path.StartsWithSegments("/api/edge")
                    && !context.Request.Path.StartsWithSegments("/healthz"),
                viewerPipeline => viewerPipeline.UseMiddleware<IpAllowlistMiddleware>(
                    new IpAllowlistOptions("Viewer:AllowedClientIps", "viewer")));
        }

        // 要包住後面所有中介層與 controller，才能攔到它們拋出的請求取消例外
        app.UseMiddleware<CancelledRequestMiddleware>();

        // ingest API 的守門只掛在 /api/ingest 路徑，不影響 LINE webhook 端點（webhook 靠簽章驗證，
        // 兩者是完全獨立的防護層）。掛載條件用 HasDatabaseAccess 而非更窄的 IngestApiEnabled，
        // 是為了讓 AllInOne／Core 在沒設 Ingest:ApiKey 時，IngestApiKeyMiddleware 能顯式回 404
        // （見該類別）——沒有金鑰時如果連中介層都不掛，請求會落到端點路由的靜態資源後援上，
        // 對非 GET/HEAD 方法回應 405 而不是 404，行為不一致。但 Viewer 模式排除在外：
        // IngestApiEnabled 的推導式排除了 Viewer（見 DeploymentCapabilities），不像 AllInOne／Core
        // 是「看有沒有設金鑰」才決定，Viewer 是結構上永遠不會有 ingest 路由——掛這層中介層對它
        // 只有壞處：白名單空清單的啟動 Warning 對 Viewer 主機是誤導（ingest 根本用不到），
        // 命中 /api/ingest/* 回的 403 也只是白名單擋下的假象，不是「路由真的不存在」該有的 404/405
        if (capabilities.HasDatabaseAccess && deploymentMode is not DeploymentMode.Viewer)
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

        if (capabilities.EdgePullApiEnabled)
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/api/edge"),
                edgePipeline =>
                {
                    edgePipeline.UseMiddleware<IpAllowlistMiddleware>(
                        new IpAllowlistOptions("Ingest:AllowedClientIps", "edge"));
                    edgePipeline.UseMiddleware<IngestApiKeyMiddleware>();
                });
        }

        if (deploymentMode is DeploymentMode.EdgeProxy)
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/line"),
                lineProxyPipeline =>
                {
                    lineProxyPipeline.UseMiddleware<IpAllowlistMiddleware>(
                        new IpAllowlistOptions("EdgeProxy:AllowedClientIps", "line-proxy"));
                    lineProxyPipeline.UseMiddleware<EdgeProxyLineForwarder>();
                });

            app.UseMiddleware<EdgeProxyForwarderMiddleware>();
        }

        app.UseRouting();
        app.UseAuthorization();

        if (capabilities.ViewerEnabled)
        {
            app.MapStaticAssets();
        }

        // 存活探針：只證明行程還在跑、還能接請求，刻意不碰資料庫也不碰任何服務。
        // 回應不帶任何內容——健康檢查端點不吃 IP 白名單，不能讓它洩漏版本、主機名或設定。
        app.MapGet("/healthz", () => Results.Ok());

        // 就緒探針：依據部署模式的能力旗標決定檢查邏輯。
        // - 有資料庫能力時：從請求服務取得 MessageDbContext 並檢查資料庫連線能力，
        //   無法連線或發生例外時皆回傳 503 Service Unavailable，避免未處理例外變成 500。
        //   探測結果快取 5 秒，監控輪詢間隔低於 5 秒也不會增加資料庫連線。
        // - 無資料庫能力時（Line／Edge 模式）：直接回傳 200 OK。該主機的就緒狀態本來就不依賴
        //   本機資料庫；若此時回傳 404，監控系統會把「該模式沒有資料庫概念」誤判成「服務故障」，
        //   統一回傳 200 才能讓監控用同一份設定涵蓋所有部署模式。
        app.MapGet("/healthz/ready", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!capabilities.HasDatabaseAccess)
            {
                return Results.Ok();
            }

            try
            {
                var dbContext = context.RequestServices.GetRequiredService<MessageDbContext>();
                var readinessCache = context.RequestServices.GetRequiredService<ReadinessCache>();
                var canConnect = await readinessCache.IsReadyAsync(
                    ct => dbContext.Database.CanConnectAsync(ct), cancellationToken);
                return canConnect ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapControllers();

        if (capabilities.ViewerEnabled)
        {
            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}")
                .WithStaticAssets();
        }
    }
}
