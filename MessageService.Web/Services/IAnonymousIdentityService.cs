namespace MessageService.Web.Services;

public record AnonymousIdentityInfo(string IconKey, string Label);

/// <summary>NameDisplayMode=Anonymous 專用：某群組成員的動植物代號永久指派與查詢。</summary>
public interface IAnonymousIdentityService
{
    /// <summary>回傳每個 userId 的代號；首次遇到的成員會當場指派並寫入 DB，之後永遠回同一筆。</summary>
    Task<IReadOnlyDictionary<string, AnonymousIdentityInfo>> GetOrAssignAsync(
        string groupId, IReadOnlyCollection<string> userIds, CancellationToken cancellationToken);
}
