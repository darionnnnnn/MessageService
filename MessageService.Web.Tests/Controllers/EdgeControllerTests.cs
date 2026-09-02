using MessageService.Controllers;
using MessageService.Models;
using MessageService.Options;
using MessageService.Outbox;
using MessageService.Services;
using MessageService.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using Xunit;

namespace MessageService.Web.Tests.Controllers;

public class EdgeControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly OutboxDbContext _dbContext;
    private readonly FakeContentDownloadQueue _downloadQueue = new();
    private readonly FakeProfileRefreshQueue _profileRefreshQueue = new();

    public EdgeControllerTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
        var options = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new OutboxDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private EdgeController CreateController(
        EdgeContentStaging? staging = null,
        EdgeProfileStaging? profileStaging = null,
        DeploymentCapabilities? capabilities = null)
    {
        return new EdgeController(
            _dbContext,
            capabilities ?? DeploymentCapabilities.Derive(DeploymentMode.Edge, new LineOptions(), new ViewerOptions(), new IngestOptions()),
            staging ?? new EdgeContentStaging(OptionsFactory.Create(new IngestOptions())),
            profileStaging ?? new EdgeProfileStaging(),
            _profileRefreshQueue,
            _downloadQueue,
            OptionsFactory.Create(new DeploymentOptions { Mode = DeploymentMode.Edge }),
            OptionsFactory.Create(new OutboxOptions()));
    }

    [Fact]
    public async Task Poll_WithContentWork_EnqueuesNewlyAcceptedItems()
    {
        var controller = CreateController();
        var request = new EdgePollRequest(
            ContentWork: [new ContentWorkItem(1L, "msg-1", "image"), new ContentWorkItem(2L, "msg-2", "image")],
            ProfileWork: []);

        var actionResult = await controller.Poll(request);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var response = Assert.IsType<EdgePollResponse>(okResult.Value);

        Assert.Equal([1L, 2L], response.AcceptedContentWork);
        Assert.Equal([1L, 2L], _downloadQueue.Enqueued);
    }

    [Fact]
    public async Task Poll_DuplicateContentId_DoesNotEnqueueTwice_ButIncludedInAccepted()
    {
        var controller = CreateController();
        var request1 = new EdgePollRequest(
            ContentWork: [new ContentWorkItem(1L, "msg-1", "image")],
            ProfileWork: []);
        var request2 = new EdgePollRequest(
            ContentWork: [new ContentWorkItem(1L, "msg-1", "image")],
            ProfileWork: []);

        var res1 = await controller.Poll(request1);
        var res2 = await controller.Poll(request2);

        var response1 = Assert.IsType<EdgePollResponse>(Assert.IsType<OkObjectResult>(res1.Result).Value);
        var response2 = Assert.IsType<EdgePollResponse>(Assert.IsType<OkObjectResult>(res2.Result).Value);

        Assert.Equal([1L], response1.AcceptedContentWork);
        Assert.Equal([1L], response2.AcceptedContentWork);
        Assert.Equal([1L], _downloadQueue.Enqueued);
    }

    [Fact]
    public async Task Poll_WhenStagingFull_RejectsWork_DoesNotEnqueue_AndNotIncludedInAccepted()
    {
        var staging = new EdgeContentStaging(OptionsFactory.Create(new IngestOptions
        {
            PullStagingMaxBytes = 100,
            MaxContentBytes = 100,
        }));
        var controller = CreateController(staging: staging);

        // 先塞滿暫存區
        staging.AcceptDispatch([new ContentWorkItem(1L, "msg-1", "image")]);
        staging.TryStage(1L, new byte[100], "image/png");

        var request = new EdgePollRequest(
            ContentWork: [new ContentWorkItem(2L, "msg-2", "image")],
            ProfileWork: []);

        var actionResult = await controller.Poll(request);
        var response = Assert.IsType<EdgePollResponse>(Assert.IsType<OkObjectResult>(actionResult.Result).Value);

        Assert.Empty(response.AcceptedContentWork);
        Assert.Empty(_downloadQueue.Enqueued);
    }

    [Fact]
    public async Task Poll_WithProfileWork_EnqueuesProfileRefreshTasks()
    {
        var controller = CreateController();
        var request = new EdgePollRequest(
            ContentWork: [],
            ProfileWork: [new EdgeProfileWorkItem("G1", "U1", new ProfileStaleness(true, true))]);

        var actionResult = await controller.Poll(request);
        var response = Assert.IsType<EdgePollResponse>(Assert.IsType<OkObjectResult>(actionResult.Result).Value);

        var task = Assert.Single(_profileRefreshQueue.Enqueued);
        Assert.Equal("G1", task.GroupId);
        Assert.Equal("U1", task.UserId);
    }
}
