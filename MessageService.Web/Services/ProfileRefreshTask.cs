namespace MessageService.Services;

/// <summary>A refresh job for a group's cached summary, and optionally one member's profile.</summary>
public record ProfileRefreshTask(string GroupId, string? UserId);
