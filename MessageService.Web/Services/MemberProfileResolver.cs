namespace MessageService.Web.Services;

using MessageService.Models;
using MessageService.Web.Dtos;

/// <summary>
/// 集中處理成員名稱、頭貼與代號解析邏輯，供訊息串 DTO 與成員解析端點共用。
/// </summary>
public static class MemberProfileResolver
{
    public static ResolvedMemberDto Resolve(
        string groupId,
        string userId,
        string? rawDisplayName,
        bool hasPicture,
        IMaskingRuleSet maskingRules,
        AnonymousIdentityInfo? anonymousIdentity)
    {
        var nameResolved = !string.IsNullOrWhiteSpace(rawDisplayName);

        string displayName;
        string? pictureUrl;
        string avatarIcon;

        if (maskingRules.RequiresAnonymousIdentity)
        {
            var label = anonymousIdentity?.Label ?? "(未知)";
            displayName = maskingRules.ResolveDisplayName(userId, rawDisplayName, label);
            pictureUrl = null;
            avatarIcon = anonymousIdentity?.IconKey ?? AvatarIconCatalog.ForHash(userId).IconKey;
        }
        else
        {
            displayName = maskingRules.ResolveDisplayName(userId, rawDisplayName);
            // 非 Original 模式下真實頭貼一律不外流，即使前端不渲染，URL 本身就是身分線索
            pictureUrl = maskingRules.RevealsOriginalProfile && hasPicture
                ? $"api/groups/{groupId}/members/{userId}/avatar"
                : null;
            // 一律附上決定性的 fallback 圖示 key，前端在 PictureUrl 缺失或載入失敗時可以直接換上
            avatarIcon = AvatarIconCatalog.ForHash(userId).IconKey;
        }

        return new ResolvedMemberDto(userId, displayName, pictureUrl, avatarIcon, nameResolved);
    }
}
