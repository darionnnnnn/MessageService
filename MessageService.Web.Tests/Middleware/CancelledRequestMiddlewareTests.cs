using MessageService.Web.Middleware;
using Microsoft.AspNetCore.Http;

namespace MessageService.Web.Tests.Middleware;

public class CancelledRequestMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_NextThrowsAfterRequestAborted_SwallowsException()
    {
        using var cts = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cts.Token };
        cts.Cancel();
        var middleware = new CancelledRequestMiddleware(_ => throw new OperationCanceledException(context.RequestAborted));

        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task InvokeAsync_NextThrowsButRequestNotAborted_Rethrows()
    {
        var context = new DefaultHttpContext();
        var middleware = new CancelledRequestMiddleware(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_NextSucceeds_PassesThrough()
    {
        var context = new DefaultHttpContext();
        var called = false;
        var middleware = new CancelledRequestMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }
}
