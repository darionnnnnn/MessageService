using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MessageService.Tests.TestSupport;

/// <summary>模擬 SaveChanges 階段的失敗：ThrowOnce 模擬暫時性儲存失敗（斷線、逾時）；
/// BeforeSaveOnce 可在 SaveChanges 真正執行前插隊做事（例如用另一個 context 先插入
/// 同一筆資料，讓後續的儲存真的撞上唯一索引）。</summary>
public class SaveFailureInterceptor : SaveChangesInterceptor
{
    public bool ThrowOnce { get; set; }

    public Func<Task>? BeforeSaveOnce { get; set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (BeforeSaveOnce is { } callback)
        {
            BeforeSaveOnce = null;
            await callback();
        }

        if (ThrowOnce)
        {
            ThrowOnce = false;
            throw new DbUpdateException("simulated transient save failure");
        }

        return result;
    }
}
