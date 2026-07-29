using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageService.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MessageService.Tests.Controllers;

public class LineWebhookControllerTests : IDisposable
{
    private const string ChannelSecret = "integration-test-secret";

    private readonly string _dbPath;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public LineWebhookControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-test-{Guid.NewGuid():N}.db");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("Line:ChannelSecret", ChannelSecret);
        });

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.EnsureCreated();
        }

        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static string ComputeSignature(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(ChannelSecret), Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public async Task Post_WithoutSignature_Returns401()
    {
        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/line/webhook", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithInvalidSignature_Returns401()
    {
        var content = new StringContent("{\"destination\":\"d\",\"events\":[]}", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", "wrong-signature");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSignatureButMalformedBody_StillReturns200()
    {
        const string body = "{not valid json";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", ComputeSignature(body));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidSignature_Returns200()
    {
        const string body = "{\"destination\":\"d\",\"events\":[]}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/line/webhook") { Content = content };
        request.Headers.Add("X-Line-Signature", ComputeSignature(body));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
