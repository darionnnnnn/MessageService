namespace MessageService.Services;

/// <summary>用 Global\ 具名 mutex 包住一段資料庫 migration 相關操作，避免同機多個行程
/// （IIS 重疊回收過渡期、同機多站台、或啟動時的 SQL Server 探測與正式 migration 之間）同時對
/// 同一顆資料庫跑 DDL 而互相打架——兩處呼叫端（Program.cs 的正式 migration、
/// DatabaseStartupProbe 的啟動探測）共用同一個具名鎖，確保彼此也會排隊而不是打架。
///
/// Global\ 命名空間的核心物件預設 DACL 只授權建立者——同一台機器上兩個不同應用程式集區身分
/// （例如 Core／Viewer 各自一個站台）的行程，第二個啟動的那個連 new Mutex(...) 都會被
/// UnauthorizedAccessException 拒絕。這種情況下拿不到跨行程鎖不該擋住啟動：單站台部署根本
/// 沒有競爭對手，多站台情境退化成不加鎖執行，Migrate() 本身的冪等性仍能自然收斂，只是失去
/// 排隊保護（風險由呼叫端透過 onLockUnavailable 決定要不要記警告）。</summary>
public static class DatabaseMigrationMutex
{
    private const string MutexName = @"Global\MessageService.Migrate";

    public static void RunExclusive(Action action, Action? onLockUnavailable = null)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // 前一個持鎖行程沒釋放就死掉（IIS 回收逾時強殺、當機）——這個例外拋出時鎖其實
                // 「已經取得」，照常往下走即可
            }
        }
        catch (UnauthorizedAccessException)
        {
            mutex = null;
            onLockUnavailable?.Invoke();
        }

        try
        {
            action();
        }
        finally
        {
            mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }
}
