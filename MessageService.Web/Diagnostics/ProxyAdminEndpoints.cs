using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MessageService.Web.Diagnostics;

/// <summary>
/// EdgeProxy 錯誤查詢 minimal API 端點註冊。
/// 僅在 EdgeProxy 模式下由 MessageServiceRequestPipelineExtensions 呼叫註冊。
/// </summary>
public static class ProxyAdminEndpoints
{
    public static void MapProxyAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/proxy-admin/errors", (
            LogRingBuffer ringBuffer,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            var processStartTimeUtc = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);
            var response = new ProxyAdminErrorsResponse(
                MachineName: Environment.MachineName,
                ProcessStartTimeUtc: processStartTimeUtc,
                Entries: ringBuffer.Snapshot());

            return Results.Ok(response);
        });
    }
}

/// <summary>
/// EdgeProxy 錯誤查詢 API 回應模型。
/// </summary>
public sealed record ProxyAdminErrorsResponse(
    string MachineName,
    DateTimeOffset ProcessStartTimeUtc,
    IReadOnlyList<LogBufferEntry> Entries);