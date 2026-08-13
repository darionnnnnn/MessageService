using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Crypto;

// MessageDbContext 透過 EF ValueConverter 套用欄位加密——這組測試確認密文真的落地在原始
// SQL 欄位裡（不是只在 C# 端看起來加密），而且 LINQ 投影（不是只有完整實體）也會自動解密，
// 加密啟用前寫入的舊資料（沒有 ENC1: 前綴）仍然讀得到
public class MessageDbContextEncryptionTests : IDisposable
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private readonly SqliteConnection _connection;

    public MessageDbContextEncryptionTests()
    {
        _connection = SqliteTestDatabase.CreateOpenConnection();
    }

    public void Dispose() => _connection.Dispose();

    private FieldCipher EnabledCipher() =>
        new(OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = Key }), NullLogger<FieldCipher>.Instance);

    private MessageDbContext CreateContext(FieldCipher? cipher = null)
    {
        var options = new DbContextOptionsBuilder<MessageDbContext>().UseSqlite(_connection).Options;
        var context = new MessageDbContext(options, cipher);
        context.Database.EnsureCreated();
        return context;
    }

    private async Task<string?> ReadRawColumnAsync(string table, string column, string whereClause)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE {whereClause}";
        var result = await command.ExecuteScalarAsync();
        return result as string;
    }

    [Fact]
    public async Task GroupMessageText_StoredEncrypted_RawColumnDoesNotContainPlaintext()
    {
        var cipher = EnabledCipher();
        await using (var dbContext = CreateContext(cipher))
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "我的密碼是1234", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var raw = await ReadRawColumnAsync("GroupMessages", "Text", "WebhookEventId = 'e1'");
        Assert.NotNull(raw);
        Assert.StartsWith("ENC1:", raw);
        Assert.DoesNotContain("密碼", raw);
    }

    [Fact]
    public async Task GroupMessageText_ReadBackThroughEntity_IsDecrypted()
    {
        var cipher = EnabledCipher();
        long messageId;
        await using (var dbContext = CreateContext(cipher))
        {
            var message = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "我的密碼是1234", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            };
            dbContext.GroupMessages.Add(message);
            await dbContext.SaveChangesAsync();
            messageId = message.Id;
        }

        await using var reader = CreateContext(cipher);
        var reloaded = await reader.GroupMessages.SingleAsync(m => m.Id == messageId);
        Assert.Equal("我的密碼是1234", reloaded.Text);
    }

    [Fact]
    public async Task GroupMessageText_ReadBackThroughLinqProjection_IsDecrypted()
    {
        // MessagesController 用投影（Select 成匿名型別）而不是完整實體讀 Text——
        // 確認 ValueConverter 對投影也生效，不是只對完整實體物件化才生效
        var cipher = EnabledCipher();
        long messageId;
        await using (var dbContext = CreateContext(cipher))
        {
            var message = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "投影也要能解密", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            };
            dbContext.GroupMessages.Add(message);
            await dbContext.SaveChangesAsync();
            messageId = message.Id;
        }

        await using var reader = CreateContext(cipher);
        var projected = await reader.GroupMessages
            .Where(m => m.Id == messageId)
            .Select(m => new { m.Text })
            .SingleAsync();
        Assert.Equal("投影也要能解密", projected.Text);
    }

    [Fact]
    public async Task GroupMessageText_WrittenBeforeEncryptionEnabled_StillReadableAfterEnabling()
    {
        // 加密啟用前寫入的舊資料（明文，沒有 ENC1: 前綴）——啟用加密後這筆還是要讀得到，
        // 不需要一次性轉換作業
        long messageId;
        await using (var plainContext = CreateContext(cipher: null))
        {
            var message = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "加密啟用前的舊訊息", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            };
            plainContext.GroupMessages.Add(message);
            await plainContext.SaveChangesAsync();
            messageId = message.Id;
        }

        await using var encryptedContext = CreateContext(EnabledCipher());
        var reloaded = await encryptedContext.GroupMessages.SingleAsync(m => m.Id == messageId);
        Assert.Equal("加密啟用前的舊訊息", reloaded.Text);
    }

    [Fact]
    public async Task GroupMessageText_MixedOldAndNewRows_BothReadCorrectly()
    {
        long oldId, newId;
        await using (var plainContext = CreateContext(cipher: null))
        {
            var old = new GroupMessage
            {
                WebhookEventId = "e-old", LineMessageId = "m-old", GroupId = "G1", MessageType = "text",
                Text = "舊明文訊息", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            };
            plainContext.GroupMessages.Add(old);
            await plainContext.SaveChangesAsync();
            oldId = old.Id;
        }

        var cipher = EnabledCipher();
        await using (var encryptedContext = CreateContext(cipher))
        {
            var fresh = new GroupMessage
            {
                WebhookEventId = "e-new", LineMessageId = "m-new", GroupId = "G1", MessageType = "text",
                Text = "新加密訊息", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            };
            encryptedContext.GroupMessages.Add(fresh);
            await encryptedContext.SaveChangesAsync();
            newId = fresh.Id;
        }

        await using var reader = CreateContext(cipher);
        var all = await reader.GroupMessages.OrderBy(m => m.Id).ToListAsync();
        Assert.Equal("舊明文訊息", all.Single(m => m.Id == oldId).Text);
        Assert.Equal("新加密訊息", all.Single(m => m.Id == newId).Text);
    }

    [Fact]
    public async Task GroupNameAndPictureUrl_RoundTripEncrypted()
    {
        var cipher = EnabledCipher();
        await using (var dbContext = CreateContext(cipher))
        {
            dbContext.Groups.Add(new Group { GroupId = "G1", GroupName = "工作群組", PictureUrl = "https://example/g.jpg", UpdatedAt = DateTimeOffset.UtcNow });
            await dbContext.SaveChangesAsync();
        }

        var rawName = await ReadRawColumnAsync("Groups", "GroupName", "GroupId = 'G1'");
        Assert.StartsWith("ENC1:", rawName);

        await using var reader = CreateContext(cipher);
        var group = await reader.Groups.SingleAsync();
        Assert.Equal("工作群組", group.GroupName);
        Assert.Equal("https://example/g.jpg", group.PictureUrl);
    }

    [Fact]
    public async Task GroupMemberDisplayNameAndPictureUrl_RoundTripEncrypted()
    {
        var cipher = EnabledCipher();
        await using (var dbContext = CreateContext(cipher))
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = "G1", UserId = "U1", DisplayName = "小明", PictureUrl = "https://example/u1.jpg", UpdatedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var rawName = await ReadRawColumnAsync("GroupMembers", "DisplayName", "UserId = 'U1'");
        Assert.StartsWith("ENC1:", rawName);

        await using var reader = CreateContext(cipher);
        var member = await reader.GroupMembers.SingleAsync();
        Assert.Equal("小明", member.DisplayName);
        Assert.Equal("https://example/u1.jpg", member.PictureUrl);
    }

    [Fact]
    public async Task UserAlias_RoundTripEncrypted()
    {
        var cipher = EnabledCipher();
        await using (var dbContext = CreateContext(cipher))
        {
            dbContext.UserAliases.Add(new UserAlias { UserId = "U1", Alias = "老王" });
            await dbContext.SaveChangesAsync();
        }

        var raw = await ReadRawColumnAsync("UserAliases", "Alias", "UserId = 'U1'");
        Assert.StartsWith("ENC1:", raw);

        await using var reader = CreateContext(cipher);
        var alias = await reader.UserAliases.SingleAsync();
        Assert.Equal("老王", alias.Alias);
    }

    [Fact]
    public async Task MessageContentFileName_RoundTripEncrypted()
    {
        var cipher = EnabledCipher();
        long contentId;
        await using (var dbContext = CreateContext(cipher))
        {
            var message = new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "file",
                EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow,
                Content = new MessageContent { DownloadStatus = DownloadStatus.Pending, FileName = "報告.pdf" }
            };
            dbContext.GroupMessages.Add(message);
            await dbContext.SaveChangesAsync();
            contentId = message.Content!.Id;
        }

        var raw = await ReadRawColumnAsync("MessageContents", "FileName", $"Id = {contentId}");
        Assert.StartsWith("ENC1:", raw);

        await using var reader = CreateContext(cipher);
        var content = await reader.MessageContents.SingleAsync(c => c.Id == contentId);
        Assert.Equal("報告.pdf", content.FileName);
    }

    [Fact]
    public async Task GroupId_NeverEncrypted_StaysPlaintextForIndexingAndGrouping()
    {
        // GroupId／UserId 是索引鍵，必須保持明文——這組測試釘住「加密不會不小心波及它們」
        var cipher = EnabledCipher();
        await using (var dbContext = CreateContext(cipher))
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", UserId = "U1", MessageType = "text",
                Text = "hi", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var rawGroupId = await ReadRawColumnAsync("GroupMessages", "GroupId", "WebhookEventId = 'e1'");
        var rawUserId = await ReadRawColumnAsync("GroupMessages", "UserId", "WebhookEventId = 'e1'");
        Assert.Equal("G1", rawGroupId);
        Assert.Equal("U1", rawUserId);
    }

    [Fact]
    public async Task DisabledCipher_StoresPlaintext_UnaffectedByEncryptionInfrastructure()
    {
        await using (var dbContext = CreateContext(cipher: null))
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "一般明文訊息", EventTimestamp = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var raw = await ReadRawColumnAsync("GroupMessages", "Text", "WebhookEventId = 'e1'");
        Assert.Equal("一般明文訊息", raw);
    }
}
