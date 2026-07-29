using System.Net;
using System.Net.Http.Headers;
using MessageService.Models;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class ContentStreamTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<long> SeedCompletedContentAsync(byte[] bytes, string contentType = "video/mp4", string? fileName = null)
    {
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = Guid.NewGuid().ToString(),
                LineMessageId = "m1",
                GroupId = "G1",
                MessageType = fileName is null ? "video" : "file",
                EventTimestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent
                {
                    DownloadStatus = DownloadStatus.Completed,
                    Content = bytes,
                    ContentType = contentType,
                    FileName = fileName,
                    CompletedAt = DateTimeOffset.UtcNow
                }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });
        return contentId;
    }

    [Fact]
    public async Task GetContent_NonExistentId_Returns404()
    {
        var response = await _fixture.Client.GetAsync("/api/messages/999999/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContent_PendingStatus_Returns404()
    {
        long contentId = 0;
        await _fixture.SeedAsync(async dbContext =>
        {
            var groupMessage = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "image",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent { DownloadStatus = DownloadStatus.Pending }
            };
            dbContext.GroupMessages.Add(groupMessage);
            await dbContext.SaveChangesAsync();
            contentId = groupMessage.Content.Id;
        });

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContent_Completed_NoRange_Returns200WithFullBody()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task GetContent_FileMessage_SetsContentDispositionFileName()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var contentId = await SeedCompletedContentAsync(bytes, "application/pdf", "report.pdf");

        var response = await _fixture.Client.GetAsync($"/api/messages/{contentId}/content");

        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task GetContent_WithValidRange_Returns206WithSlice()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(10, response.Content.Headers.ContentLength);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Skip(10).Take(10), body);
    }

    [Fact]
    public async Task GetContent_RangeWithoutEnd_ReturnsFromStartToEndOfFile()
    {
        var bytes = Enumerable.Range(0, 50).Select(i => (byte)i).ToArray();
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(40, null);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Skip(40).Take(10), body);
    }

    [Fact]
    public async Task GetContent_RangeStartBeyondLength_Returns416()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var contentId = await SeedCompletedContentAsync(bytes);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/messages/{contentId}/content");
        request.Headers.Range = new RangeHeaderValue(100, 200);

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */3", response.Content.Headers.ContentRange?.ToString());
    }
}
