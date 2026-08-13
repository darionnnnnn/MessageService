using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MessageService.Web.Tests.Crypto;

// 從 HTTP 端到端驗證：Encryption:Enabled=true 時，經 DI 注入的 FieldCipher 真的套用到
// MessageDbContext，controller 讀出來的是解密後的明文，底層 SQLite 檔案裡存的是密文
public class EncryptionEndToEndTests : IDisposable
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private readonly WebAppFactoryFixture _fixture = new(encryptionKey: Key);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetMessages_EncryptionEnabled_ReturnsDecryptedText()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", UserId = "U1", MessageType = "text",
                Text = "我的密碼是1234", EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G1/messages?days=3");

        var message = Assert.Single(page!.Messages);
        Assert.Equal("我的密碼是1234", message.Text);
    }

    [Fact]
    public async Task GetGroups_EncryptionEnabled_ReturnsDecryptedGroupNameAndPreview()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "工作群組", UpdatedAt = now });
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", UserId = "U1", MessageType = "text",
                Text = "哈囉", EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var groups = await _fixture.Client.GetFromJsonAsync<List<GroupDto>>("/api/groups");

        var group = Assert.Single(groups!);
        Assert.Equal("工作群組", group.DisplayName);
        Assert.Equal("哈囉", group.LastMessagePreview);
    }

    [Fact]
    public async Task Settings_Alias_EncryptionEnabled_RoundTripsCorrectly()
    {
        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("老王"));
        Assert.True(response.IsSuccessStatusCode);

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");

        var alias = Assert.Single(aliases!);
        Assert.Equal("老王", alias.Alias);
    }

    [Fact]
    public async Task RawSqliteFile_EncryptionEnabled_TextColumnIsNotPlaintext()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "不該在檔案裡看到這段明文", EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        var connectionString = _fixture.DbConnectionString;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Text FROM GroupMessages WHERE WebhookEventId = 'e1'";
        var raw = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(raw);
        Assert.StartsWith("ENC2:", raw);
        Assert.DoesNotContain("明文", raw);
    }

    [Fact]
    public async Task Search_EncryptionEnabled_FindsMatchWithinSearchWindow()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "今天去騎腳踏車", EventTimestamp = now.AddDays(-1), ReceivedAt = now.AddDays(-1)
            });
            await Task.CompletedTask;
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=腳踏車");

        var hit = Assert.Single(results!);
        Assert.Equal("今天去騎腳踏車", hit.Snippet);
    }

    [Fact]
    public async Task Search_EncryptionEnabled_DoesNotFindMatchOutsideSearchWindow()
    {
        // 預設 SearchWindowDays=14；15 天前的訊息不該被搜尋到
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "很久以前騎腳踏車的事", EventTimestamp = now.AddDays(-15), ReceivedAt = now.AddDays(-15)
            });
            await Task.CompletedTask;
        });

        var results = await _fixture.Client.GetFromJsonAsync<List<MessageSearchResultDto>>("/api/messages/search?q=腳踏車");

        Assert.Empty(results!);
    }
}
