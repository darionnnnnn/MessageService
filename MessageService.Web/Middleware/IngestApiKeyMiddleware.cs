using System.Security.Cryptography;
using System.Text;
using MessageService.Options;
using Microsoft.Extensions.Options;

namespace MessageService.Middleware;

/// <summary>驗證 /api/ingest/* 請求帶的 X-Ingest-Key 標頭是否與設定的 Ingest:ApiKey 一致。
/// 只掛在 /api/ingest 路徑（見 Program.cs 用 UseWhen 限定的路徑群組），不影響 LINE webhook
/// 端點——webhook 靠簽章驗證，這裡是另一組完全獨立的憑證，服務對服務之間的共用密鑰。
/// 用固定長度＋固定時間比較，不讓「金鑰前幾個字元對不對」透過回應時間差異被推敲出來。</summary>
public class IngestApiKeyMiddleware(RequestDelegate next, IOptions<IngestOptions> options, ILogger<IngestApiKeyMiddleware> logger)
{
    private const string HeaderName = "X-Ingest-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        var expected = options.Value.ApiKey;

        // 理論上不會發生：DeploymentModeConvention 在金鑰未設定時已經把 controller 整個移除，
        // 請求根本不會有 endpoint 可以落地。留著這道防線只是不信任「路由層一定先擋到」。
        if (string.IsNullOrEmpty(expected))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (!FixedTimeEquals(provided, expected))
        {
            logger.LogWarning("Rejected ingest API request from {RemoteIp} to {Path}: missing or invalid {Header}",
                context.Connection.RemoteIpAddress, context.Request.Path, HeaderName);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        // 長度不同時提早回 false 會外洩「猜對的字元數」這類極細粒度的時序訊號，但這裡的威脅模型
        // （內部工具、金鑰事先透過設定分享，不是公開登入表單）不需要處理到這一層；
        // 用固定時間比較已經擋掉最直接的「逐字元比對提早 return」側通道
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
