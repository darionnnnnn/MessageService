using MessageService.Web.Services;

namespace MessageService.Web.Tests.Services;

public class ReadinessCacheTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task IsReadyAsync_WithinTtl_ReturnsCachedResultWithoutProbingAgain()
    {
        // 1. TTL 內連續呼叫兩次，probe 只被呼叫 1 次，兩次都回 true（用計數器變數斷言）。
        var timeProvider = new FakeTimeProvider();
        var cache = new ReadinessCache(timeProvider);
        var probeCount = 0;

        var result1 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        timeProvider.Now = timeProvider.Now.AddSeconds(2);

        var result2 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task IsReadyAsync_AfterTtlExpires_ProbesAgain()
    {
        // 2. 時間前進 5 秒以上後再呼叫，probe 被呼叫第 2 次。
        var timeProvider = new FakeTimeProvider();
        var cache = new ReadinessCache(timeProvider);
        var probeCount = 0;

        var result1 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        timeProvider.Now = timeProvider.Now.AddSeconds(5);

        var result2 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, probeCount);
    }

    [Fact]
    public async Task IsReadyAsync_WhenProbeReturnsFalse_CachesFalseResult()
    {
        // 3. probe 回 false 時同樣被快取：連續兩次呼叫，probe 只被呼叫 1 次、兩次都回 false。
        var timeProvider = new FakeTimeProvider();
        var cache = new ReadinessCache(timeProvider);
        var probeCount = 0;

        var result1 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(false);
        }, CancellationToken.None);

        timeProvider.Now = timeProvider.Now.AddSeconds(3);

        var result2 = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(false);
        }, CancellationToken.None);

        Assert.False(result1);
        Assert.False(result2);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task IsReadyAsync_WhenProbeThrowsException_PropagatesExceptionAndDoesNotCacheErrorState()
    {
        // 4. probe 拋例外時，例外會往上傳（Assert.ThrowsAsync），而且下一次呼叫仍會重新探測（不能把例外狀態當成快取結果）。
        var timeProvider = new FakeTimeProvider();
        var cache = new ReadinessCache(timeProvider);
        var probeCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.IsReadyAsync(ct =>
        {
            probeCount++;
            throw new InvalidOperationException("資料庫連線逾時");
        }, CancellationToken.None));

        Assert.Equal(1, probeCount);

        // 下一次呼叫（即使在 5 秒內）仍會重新探測
        var result = await cache.IsReadyAsync(ct =>
        {
            probeCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, probeCount);
    }
}
