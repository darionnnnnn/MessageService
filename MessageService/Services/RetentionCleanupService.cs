using MessageService.Data;
using MessageService.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class RetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            logger.LogInformation("Next retention cleanup scheduled at {NextRun:yyyy-MM-dd HH:mm}", DateTime.Now + delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 清除失敗（例如 DB 暫時斷線）不能讓例外冒出 ExecuteAsync，
            // 否則預設的 BackgroundServiceExceptionBehavior.StopHost 會關掉整個服務
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention cleanup failed; will retry at next scheduled run");
            }
        }
    }

    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.Now;
        var todayRun = now.Date + _options.CleanupTimeOfDay;
        var nextRun = todayRun > now ? todayRun : todayRun.AddDays(1);
        return nextRun - now;
    }

    public async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();

        var cutoff = DateTimeOffset.UtcNow.AddYears(-_options.Years);
        var deletedCount = await dbContext.GroupMessages
            .Where(m => m.EventTimestamp < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation("Retention cleanup removed {Count} group messages older than {Cutoff:yyyy-MM-dd}", deletedCount, cutoff);
    }
}
