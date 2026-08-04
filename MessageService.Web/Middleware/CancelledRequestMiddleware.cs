namespace MessageService.Web.Middleware;

/// <summary>
/// 瀏覽器換頁/切換群組/輪詢逾時都會讓進行中的請求被中止，下游（EF 查詢、內容串流）
/// 因此拋出 OperationCanceledException 是正常現象，不是錯誤。放在管線最前面攔截，
/// 避免它被 DeveloperExceptionPageMiddleware/UseExceptionHandler 當成 ERROR 記錄，
/// 把真正的例外淹沒在雜訊裡。只吞掉「請求真的被取消」這種情況，其他取消（例如逾時）照樣往外拋。
/// </summary>
public class CancelledRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }
}
