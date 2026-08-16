using MessageService.Services;

namespace MessageService.Tests.Services;

public class DatabaseMigrationMutexTests
{
    [Fact]
    public void RunExclusive_ExecutesAction()
    {
        var executed = false;

        DatabaseMigrationMutex.RunExclusive(() => executed = true);

        Assert.True(executed);
    }

    [Fact]
    public void RunExclusive_DoesNotCallOnLockUnavailable_WhenLockSucceeds()
    {
        var lockUnavailableCalled = false;

        DatabaseMigrationMutex.RunExclusive(() => { }, onLockUnavailable: () => lockUnavailableCalled = true);

        Assert.False(lockUnavailableCalled);
    }

    [Fact]
    public void RunExclusive_ReturnsTrue_WhenActionExecuted()
    {
        // 拿得到鎖時，runWithoutLock 的值不影響行為：action 照跑且回傳 true
        var executed = false;

        Assert.True(DatabaseMigrationMutex.RunExclusive(() => executed = true, runWithoutLock: false));
        Assert.True(executed);
    }

    [Fact]
    public void RunExclusive_PropagatesExceptionFromAction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrationMutex.RunExclusive(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void RunExclusive_CanBeCalledSequentially_WithoutDeadlock()
    {
        // 同一個行程內先取後還、再取再還——確定 ReleaseMutex 真的有放掉鎖，不會卡死下一次呼叫
        DatabaseMigrationMutex.RunExclusive(() => { });
        DatabaseMigrationMutex.RunExclusive(() => { });
    }
}
