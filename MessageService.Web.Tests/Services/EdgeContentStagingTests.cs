using MessageService.Options;
using MessageService.Services;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class EdgeContentStagingTests
{
    // MaxContentBytes 一併調小：生效的上限會夾到至少等於它（見 EdgeContentStaging），
    // 只設 PullStagingMaxBytes 的話這些容量測試會被 300MB 的預設值架空
    private static EdgeContentStaging Create(long maxBytes = 1024) =>
        new(OptionsFactory.Create(new IngestOptions
        {
            PullStagingMaxBytes = maxBytes,
            MaxContentBytes = maxBytes,
        }));

    private static ContentWorkItem Work(long id) => new(id, $"msg-{id}", "image");

    [Fact]
    public void AcceptDispatch_WithinCapacity_AcceptsAll()
    {
        var staging = Create();

        var accepted = staging.AcceptDispatch([Work(1), Work(2)]);

        Assert.Equal([1L, 2L], accepted);
        Assert.Equal([1L, 2L], staging.GetPendingIds().Order());
    }

    [Fact]
    public void AcceptDispatch_SameIdTwice_IsIdempotent()
    {
        var staging = Create();
        staging.AcceptDispatch([Work(1)]);

        // poll 回應遺失時 Core 會重派同一批，不能因此下載兩次
        var accepted = staging.AcceptDispatch([Work(1)]);

        Assert.Equal([1L], accepted);
        Assert.Single(staging.GetPendingIds());
    }

    [Fact]
    public void AcceptDispatch_WhenFull_RejectsNewWorkAsBackpressure()
    {
        var staging = Create(maxBytes: 100);
        staging.AcceptDispatch([Work(1)]);
        Assert.True(staging.TryStage(1, new byte[100], "image/png"));

        // 暫存觸頂：新派工不收，留在 Core 端維持 Pending 下一輪再派
        var accepted = staging.AcceptDispatch([Work(2)]);

        Assert.Empty(accepted);
        Assert.Empty(staging.GetPendingIds());
    }

    [Fact]
    public void TryStage_ExceedingCapacity_RejectsAndKeepsAccounting()
    {
        var staging = Create(maxBytes: 100);
        staging.AcceptDispatch([Work(1)]);

        Assert.False(staging.TryStage(1, new byte[101], "image/png"));
        Assert.Equal(0, staging.StagedBytes);
        Assert.Empty(staging.GetReadyIds());
        // 收不下時派工必須留著，否則這筆永遠沒人再下載，還會一路走到 FailAsync
        // 去消耗 Core 端的正式重試次數
        Assert.Equal([1L], staging.GetPendingIds());
    }

    [Fact]
    public void TryStage_NotDispatched_IsRejected()
    {
        var staging = Create();

        Assert.False(staging.TryStage(99, new byte[10], null));
    }

    [Fact]
    public void TryStage_Again_ReplacesWithoutDoubleCounting()
    {
        var staging = Create(maxBytes: 100);
        staging.AcceptDispatch([Work(1)]);
        Assert.True(staging.TryStage(1, new byte[40], null));

        // 租約逾期重派後又下載一次：以新的取代，佔用量不得累加
        Assert.True(staging.TryStage(1, new byte[40], null));
        Assert.Equal(40, staging.StagedBytes);
    }

    [Fact]
    public void Release_FreesMemoryOnlyAfterAck()
    {
        var staging = Create();
        staging.AcceptDispatch([Work(1)]);
        staging.TryStage(1, new byte[64], "image/png");

        Assert.Equal(64, staging.StagedBytes);
        Assert.NotNull(staging.Get(1));

        Assert.True(staging.Release(1));
        Assert.Equal(0, staging.StagedBytes);
        Assert.Null(staging.Get(1));
    }

    [Fact]
    public void Release_UnknownId_ReturnsFalse()
    {
        var staging = Create();

        Assert.False(staging.Release(42));
    }

    [Fact]
    public void MarkFailed_MovesOutOfPendingAndIsReportedOnce()
    {
        var staging = Create();
        staging.AcceptDispatch([Work(1)]);

        staging.MarkFailed(1);

        Assert.Empty(staging.GetPendingIds());
        Assert.Equal([1L], staging.DrainFailedIds());
        // 回報過就不再重複回報
        Assert.Empty(staging.DrainFailedIds());
    }

    [Fact]
    public void StagedBytes_TracksMultipleItems()
    {
        var staging = Create(maxBytes: 1000);
        staging.AcceptDispatch([Work(1), Work(2)]);
        staging.TryStage(1, new byte[100], null);
        staging.TryStage(2, new byte[200], null);

        Assert.Equal(300, staging.StagedBytes);

        staging.Release(1);
        Assert.Equal(200, staging.StagedBytes);
    }

    [Fact]
    public void MaxBytes_IsClampedToAtLeastMaxContentBytes()
    {
        // 設得比單一檔案上限還小時，最大的那種檔案永遠塞不進去、只會不斷重新派工
        var staging = new EdgeContentStaging(OptionsFactory.Create(new IngestOptions
        {
            PullStagingMaxBytes = 10,
            MaxContentBytes = 500,
        }));
        staging.AcceptDispatch([Work(1)]);

        Assert.True(staging.TryStage(1, new byte[500], null));
    }
}
