using System.Net;
using System.Net.Http.Json;
using MessageService.Models;
using MessageService.Web.Dtos;
using MessageService.Web.Tests.TestSupport;

namespace MessageService.Web.Tests.Api;

public class SettingsControllerTests : IDisposable
{
    private readonly WebAppFactoryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetDisplaySettings_DefaultsToOriginal()
    {
        var settings = await _fixture.Client.GetFromJsonAsync<DisplaySettingsDto>("/api/settings/display");

        Assert.Equal(nameof(NameDisplayMode.Original), settings!.NameDisplayMode);
    }

    [Fact]
    public async Task UpdateDisplaySettings_PersistsNewMode()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/display", new DisplaySettingsDto(nameof(NameDisplayMode.Original)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<DisplaySettingsDto>("/api/settings/display");
        Assert.Equal(nameof(NameDisplayMode.Original), settings!.NameDisplayMode);
    }

    [Fact]
    public async Task UpdateDisplaySettings_InvalidMode_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/display", new DisplaySettingsDto("NotARealMode"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateDisplaySettings_WhenSingletonRowMissing_RecreatesItWithFixedId()
    {
        // 設定列是 migration 種下的，但若被人為刪掉，補建時必須仍用固定的 SingletonId，
        // 否則之後所有以 Id == SingletonId 為條件的讀取都會找不到而永遠退回預設值
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.ViewerSettings.RemoveRange(dbContext.ViewerSettings);
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/display", new DisplaySettingsDto(nameof(NameDisplayMode.Original)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<DisplaySettingsDto>("/api/settings/display");
        Assert.Equal(nameof(NameDisplayMode.Original), settings!.NameDisplayMode);
    }

    [Fact]
    public async Task CreateKeyword_ThenListIncludesIt()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("密碼", null, true, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");

        var found = Assert.Single(keywords!);
        Assert.Equal(created!.Id, found.Id);
        Assert.Equal("密碼", found.Keyword);
        Assert.True(found.ApplyToAllGroups);
        Assert.Empty(found.GroupIds);
    }

    [Fact]
    public async Task CreateKeyword_WithGroupScope_StoresGroupIds()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("secret", "[hidden]", false, ["G1", "G2"]));
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        Assert.False(created!.ApplyToAllGroups);
        Assert.Equal(["G1", "G2"], created.GroupIds.OrderBy(g => g));
        Assert.Equal("[hidden]", created.Replacement);
    }

    [Fact]
    public async Task CreateKeyword_BlankKeyword_ReturnsBadRequest()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("  ", null, true, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateKeyword_ReplacesGroupScope()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, false, ["G1"]));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var updateResponse = await _fixture.Client.PutAsJsonAsync(
            $"/api/settings/keywords/{created!.Id}", new UpsertMaskKeywordDto("word2", "X", false, ["G2", "G3"]));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        var updated = Assert.Single(keywords!);
        Assert.Equal("word2", updated.Keyword);
        Assert.Equal("X", updated.Replacement);
        Assert.Equal(["G2", "G3"], updated.GroupIds.OrderBy(g => g));
    }

    [Fact]
    public async Task UpdateKeyword_SwitchingToApplyToAllGroups_ClearsPreviousGroupScope()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, false, ["G1", "G2"]));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var updateResponse = await _fixture.Client.PutAsJsonAsync(
            $"/api/settings/keywords/{created!.Id}", new UpsertMaskKeywordDto("word", null, true, null));
        Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        var updated = Assert.Single(keywords!);
        Assert.True(updated.ApplyToAllGroups);
        Assert.Empty(updated.GroupIds);
    }

    [Fact]
    public async Task CreateKeyword_ApplyToAllGroupsTrue_IgnoresSuppliedGroupIds()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("word", null, true, ["G1", "G2"]));
        var created = await response.Content.ReadFromJsonAsync<MaskKeywordDto>();

        Assert.True(created!.ApplyToAllGroups);
        Assert.Empty(created.GroupIds);
    }

    [Fact]
    public async Task UpdateKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/keywords/999", new UpsertMaskKeywordDto("x", null, true, null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteKeyword_RemovesIt()
    {
        var createResponse = await _fixture.Client.PostAsJsonAsync(
            "/api/settings/keywords", new UpsertMaskKeywordDto("temp", null, true, null));
        var created = await createResponse.Content.ReadFromJsonAsync<MaskKeywordDto>();

        var deleteResponse = await _fixture.Client.DeleteAsync($"/api/settings/keywords/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var keywords = await _fixture.Client.GetFromJsonAsync<List<MaskKeywordDto>>("/api/settings/keywords");
        Assert.Empty(keywords!);
    }

    [Fact]
    public async Task DeleteKeyword_NonExistentId_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/keywords/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpsertAlias_ThenGetAliases_ReturnsIt()
    {
        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("值班A"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        var alias = Assert.Single(aliases!);
        Assert.Equal("U1", alias.UserId);
        Assert.Equal("值班A", alias.Alias);
    }

    [Fact]
    public async Task UpsertAlias_CalledTwice_UpdatesExistingRow()
    {
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("First"));
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("Second"));

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        var alias = Assert.Single(aliases!);
        Assert.Equal("Second", alias.Alias);
    }

    [Fact]
    public async Task DeleteAlias_RemovesIt()
    {
        await _fixture.Client.PutAsJsonAsync("/api/settings/aliases/U1", new UpsertUserAliasDto("值班A"));

        var deleteResponse = await _fixture.Client.DeleteAsync("/api/settings/aliases/U1");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var aliases = await _fixture.Client.GetFromJsonAsync<List<UserAliasDto>>("/api/settings/aliases");
        Assert.Empty(aliases!);
    }

    [Fact]
    public async Task DeleteAlias_NonExistentUser_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/aliases/NoSuchUser");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsers_FiltersByGroupId_AndDedupesAcrossGroups()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G1", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G2", UserId = "U1", DisplayName = "小明", UpdatedAt = now });
            dbContext.GroupMembers.Add(new GroupMember { GroupId = "G2", UserId = "U2", DisplayName = "大華", UpdatedAt = now });
            await Task.CompletedTask;
        });

        var g1Users = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users?groupId=G1");
        Assert.Single(g1Users!);

        var allUsers = await _fixture.Client.GetFromJsonAsync<List<GroupMemberDto>>("/api/users");
        Assert.Equal(2, allUsers!.Count);
    }

    // === 保留天數（決策6：搬進 Web 設定頁，RetentionCleanupService 每次執行讀 DB） ===

    [Fact]
    public async Task GetRetentionSettings_DefaultsToThreeYears()
    {
        var settings = await _fixture.Client.GetFromJsonAsync<RetentionSettingsDto>("/api/settings/retention");

        Assert.Equal(ViewerSettings.DefaultRetentionDays, settings!.RetentionDays);
    }

    [Fact]
    public async Task UpdateRetentionSettings_PersistsNewValue()
    {
        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/retention", new RetentionSettingsDto(30));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<RetentionSettingsDto>("/api/settings/retention");
        Assert.Equal(30, settings!.RetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task UpdateRetentionSettings_OutOfRange_ReturnsBadRequest(int days)
    {
        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/retention", new RetentionSettingsDto(days));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRetentionSettings_WhenSingletonRowMissing_RecreatesItWithFixedId()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.ViewerSettings.RemoveRange(dbContext.ViewerSettings);
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.PutAsJsonAsync("/api/settings/retention", new RetentionSettingsDto(90));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<RetentionSettingsDto>("/api/settings/retention");
        Assert.Equal(90, settings!.RetentionDays);
    }

    // === 個資去識別化開關（決策7：預設全開） ===

    [Fact]
    public async Task GetPiiMaskingSettings_Defaults_NhiCardOffOthersOn()
    {
        // 健保卡固定 12 碼數字跟宅配貨運單號格式撞在一起，預設關閉；其他三種格式沒有這個問題，
        // 維持預設全開
        var settings = await _fixture.Client.GetFromJsonAsync<PiiMaskingSettingsDto>("/api/settings/pii-masking");

        Assert.True(settings!.MaskNationalId);
        Assert.True(settings.MaskMobilePhone);
        Assert.True(settings.MaskLandline);
        Assert.False(settings.MaskNhiCard);
    }

    [Fact]
    public async Task UpdatePiiMaskingSettings_PersistsEachFlagIndependently()
    {
        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/pii-masking", new PiiMaskingSettingsDto(false, true, false, true));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<PiiMaskingSettingsDto>("/api/settings/pii-masking");
        Assert.False(settings!.MaskNationalId);
        Assert.True(settings.MaskMobilePhone);
        Assert.False(settings.MaskLandline);
        Assert.True(settings.MaskNhiCard);
    }

    [Fact]
    public async Task UpdatePiiMaskingSettings_TakesEffectInMessageMasking()
    {
        var now = DateTimeOffset.UtcNow;
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.GroupMessages.Add(new GroupMessage
            {
                WebhookEventId = "e1", LineMessageId = "m1", GroupId = "G1", MessageType = "text",
                Text = "身分證A123456789", EventTimestamp = now, ReceivedAt = now
            });
            await Task.CompletedTask;
        });

        await _fixture.Client.PutAsJsonAsync(
            "/api/settings/pii-masking", new PiiMaskingSettingsDto(false, true, true, true));

        var page = await _fixture.Client.GetFromJsonAsync<MessagesPageDto>("/api/groups/G1/messages?days=3");

        Assert.Equal("身分證A123456789", Assert.Single(page!.Messages).Text);
    }

    [Fact]
    public async Task UpdatePiiMaskingSettings_WhenSingletonRowMissing_RecreatesItWithFixedId()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.ViewerSettings.RemoveRange(dbContext.ViewerSettings);
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.PutAsJsonAsync(
            "/api/settings/pii-masking", new PiiMaskingSettingsDto(false, false, false, false));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var settings = await _fixture.Client.GetFromJsonAsync<PiiMaskingSettingsDto>("/api/settings/pii-masking");
        Assert.False(settings!.MaskNationalId);
        Assert.False(settings.MaskMobilePhone);
        Assert.False(settings.MaskLandline);
        Assert.False(settings.MaskNhiCard);
    }

    // === 主機狀態（需求4，見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次D）===

    [Fact]
    public async Task GetHostHeartbeats_NoRows_ReturnsEmptyList()
    {
        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        Assert.Empty(rows!);
    }

    [Fact]
    public async Task GetHostHeartbeats_RecentlySeen_StatusIsOnline()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.Add(new HostHeartbeat
            {
                Role = "Core", MachineName = "core-host", LastSeenAt = DateTimeOffset.UtcNow,
                OutboxPending = null, OutboxOldestAgeSeconds = null, EncryptionKeyFingerprint = "abcd1234"
            });
            await Task.CompletedTask;
        });

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        var row = Assert.Single(rows!);
        Assert.Equal("Core", row.Role);
        Assert.Equal("core-host", row.MachineName);
        Assert.Equal("Online", row.Status);
        Assert.Equal("abcd1234", row.EncryptionKeyFingerprint);
    }

    [Fact]
    public async Task GetHostHeartbeats_LongSinceLastSeen_StatusIsOffline()
    {
        // 預設 Heartbeat:IntervalSeconds=60，離線門檻是 5 倍＝300 秒；30 分鐘前遠遠超過
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.Add(new HostHeartbeat
            {
                Role = "Edge", MachineName = "edge-host", LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-30)
            });
            await Task.CompletedTask;
        });

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        Assert.Equal("Offline", Assert.Single(rows!).Status);
    }

    [Fact]
    public async Task GetHostHeartbeats_ModeratelyStale_StatusIsDelayed()
    {
        // 2 倍～5 倍間隔（120～300 秒）之間算遲滯，不是離線
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.Add(new HostHeartbeat
            {
                Role = "AllInOne", MachineName = "allinone-host", LastSeenAt = DateTimeOffset.UtcNow.AddSeconds(-180)
            });
            await Task.CompletedTask;
        });

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        Assert.Equal("Delayed", Assert.Single(rows!).Status);
    }

    [Fact]
    public async Task GetHostHeartbeats_WithOutboxBacklog_ReturnsPendingAndAge()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.Add(new HostHeartbeat
            {
                Role = "Edge", MachineName = "edge-host", LastSeenAt = DateTimeOffset.UtcNow,
                OutboxPending = 7, OutboxOldestAgeSeconds = 120.5
            });
            await Task.CompletedTask;
        });

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        var row = Assert.Single(rows!);
        Assert.Equal(7, row.OutboxPending);
        Assert.Equal(120.5, row.OutboxOldestAgeSeconds);
    }

    [Fact]
    public async Task GetHostHeartbeats_MultipleHosts_OrderedByRoleThenMachineName()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.AddRange(
                new HostHeartbeat { Role = "Edge", MachineName = "b-host", LastSeenAt = DateTimeOffset.UtcNow },
                new HostHeartbeat { Role = "Core", MachineName = "a-host", LastSeenAt = DateTimeOffset.UtcNow },
                new HostHeartbeat { Role = "Edge", MachineName = "a-host", LastSeenAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");

        Assert.Equal(
            [("Core", "a-host"), ("Edge", "a-host"), ("Edge", "b-host")],
            rows!.Select(r => (r.Role, r.MachineName)));
    }

    [Fact]
    public async Task DeleteHostHeartbeat_ExistingRow_RemovesIt()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.Add(new HostHeartbeat
            {
                Role = "Edge", MachineName = "retired-host", LastSeenAt = DateTimeOffset.UtcNow.AddDays(-30)
            });
            await Task.CompletedTask;
        });

        var response = await _fixture.Client.DeleteAsync("/api/settings/host-heartbeats/Edge/retired-host");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");
        Assert.Empty(rows!);
    }

    [Fact]
    public async Task DeleteHostHeartbeat_UnknownRow_ReturnsNotFound()
    {
        var response = await _fixture.Client.DeleteAsync("/api/settings/host-heartbeats/Edge/nonexistent-host");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteHostHeartbeat_OnlyRemovesMatchingRow_LeavesOthersIntact()
    {
        await _fixture.SeedAsync(async dbContext =>
        {
            dbContext.HostHeartbeats.AddRange(
                new HostHeartbeat { Role = "Edge", MachineName = "keep-me", LastSeenAt = DateTimeOffset.UtcNow },
                new HostHeartbeat { Role = "Core", MachineName = "keep-me", LastSeenAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        await _fixture.Client.DeleteAsync("/api/settings/host-heartbeats/Edge/keep-me");

        var rows = await _fixture.Client.GetFromJsonAsync<List<HostHeartbeatDto>>("/api/settings/host-heartbeats");
        var remaining = Assert.Single(rows!);
        Assert.Equal(("Core", "keep-me"), (remaining.Role, remaining.MachineName));
    }
}
