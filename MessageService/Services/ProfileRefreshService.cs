using MessageService.Data;
using MessageService.Models;
using MessageService.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MessageService.Services;

public class ProfileRefreshService(
    IProfileRefreshQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<ProfileCacheOptions> options,
    ILogger<ProfileRefreshService> logger) : BackgroundService
{
    private readonly ProfileCacheOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var task in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(task, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Unexpected error refreshing profile cache for group {GroupId} user {UserId}",
                    task.GroupId, task.UserId);
            }
        }
    }

    public async Task ProcessAsync(ProfileRefreshTask task, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var profileClient = scope.ServiceProvider.GetRequiredService<ILineProfileClient>();
        var cutoff = DateTimeOffset.UtcNow - _options.RefreshAfter;

        await RefreshGroupAsync(dbContext, profileClient, task.GroupId, cutoff, cancellationToken);

        if (task.UserId is not null)
        {
            await RefreshMemberAsync(dbContext, profileClient, task.GroupId, task.UserId, cutoff, cancellationToken);
        }
    }

    private async Task RefreshGroupAsync(
        MessageDbContext dbContext, ILineProfileClient profileClient, string groupId,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Groups.FindAsync([groupId], cancellationToken);
        if (existing is not null && existing.UpdatedAt >= cutoff)
        {
            return;
        }

        var summary = await profileClient.GetGroupSummaryAsync(groupId, cancellationToken);
        if (summary is null)
        {
            logger.LogWarning("Group summary unavailable for group {GroupId}", groupId);
            return;
        }

        if (existing is null)
        {
            dbContext.Groups.Add(new Group
            {
                GroupId = groupId,
                GroupName = summary.GroupName,
                PictureUrl = summary.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.GroupName = summary.GroupName;
            existing.PictureUrl = summary.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshMemberAsync(
        MessageDbContext dbContext, ILineProfileClient profileClient, string groupId, string userId,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var existing = await dbContext.GroupMembers.FindAsync([groupId, userId], cancellationToken);
        if (existing is not null && existing.UpdatedAt >= cutoff)
        {
            return;
        }

        var profile = await profileClient.GetGroupMemberProfileAsync(groupId, userId, cancellationToken);
        if (profile is null)
        {
            logger.LogWarning("Member profile unavailable for group {GroupId} user {UserId}", groupId, userId);
            return;
        }

        if (existing is null)
        {
            dbContext.GroupMembers.Add(new GroupMember
            {
                GroupId = groupId,
                UserId = userId,
                DisplayName = profile.DisplayName,
                PictureUrl = profile.PictureUrl,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.DisplayName = profile.DisplayName;
            existing.PictureUrl = profile.PictureUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
