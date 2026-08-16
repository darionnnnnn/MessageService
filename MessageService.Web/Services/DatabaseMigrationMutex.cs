namespace MessageService.Services;

/// <summary>用 Global\ 具名 mutex 包住一段資料庫 migration 相關操作，避免同機多個行程
/// （IIS 重疊回收過渡期、同機多站台、或啟動時的 SQL Server 探測與正式 migration 之間）同時對
/// 同一顆資料庫跑 DDL 而互相打架——兩處呼叫端（Program.cs 的正式 migration、
/// DatabaseStartupProbe 的啟動探測）共用同一個具名鎖，確保彼此也會排隊而不是打架。
///
/// Global\ 命名空間的核心物件預設 DACL 只授權建立者——同一台機器上兩個不同應用程式集區身分
/// （例如 Core／Viewer 各自一個站台）的行程，第二個啟動的那個連 new Mutex(...) 都會被
/// UnauthorizedAccessException 拒絕。
///
/// 拿不到跨行程鎖時要不要照跑，由呼叫端用 <paramref name="runWithoutLock"/> 決定：
/// 探測（只要確認連得上）照跑無妨；真正的 schema migration 則寧可跳過也不要無鎖硬跑——
/// 無鎖硬跑等於兩個站台同時對同一顆資料庫下 DDL，正是這把鎖要防的事。回傳值代表
/// action 是否真的執行過，讓呼叫端可以據此記錄。</summary>
public static class DatabaseMigrationMutex
{
    private const string MutexName = @"Global\MessageService.Migrate";

    /// <returns>action 是否有被執行（拿不到鎖且 runWithoutLock=false 時為 false）。</returns>
    public static bool RunExclusive(Action action, Action? onLockUnavailable = null, bool runWithoutLock = true)
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

            if (!runWithoutLock)
            {
                return false;
            }
        }

        try
        {
            action();
            return true;
        }
        finally
        {
            mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }
}
