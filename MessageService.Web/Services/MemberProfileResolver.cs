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
        string? rawPictureUrl,
        bool hasPicture,
        IMaskingRuleSet maskingRules,
        AnonymousIdentityInfo? anonymousIdentity)
    {
        // ProfileResolved 的語意是「目前這個模式下，畫面上的名稱**與頭貼**都已是最終值」，
        // 不是「有沒有抓到真名」——匿名模式顯示的是代號、根本不看真名，
        // 若在那裡回 false，前端會把這些人永遠留在待解析集合裡每 30 秒空轉一次。
        // 頭貼也要算進去：名稱先到、圖檔下載暫時失敗時，只看名稱會讓前端太早停止輪詢，
        // 圖檔之後補齊了畫面也不會更新，那正是使用者回報「大頭照要重新整理才出現」的情境
        bool profileResolved;

        string displayName;
        string? pictureUrl;
        string avatarIcon;

        if (maskingRules.RequiresAnonymousIdentity)
        {
            // 代號已指派就是最終值；沒指派（例如成員不在這個群組）才算未解析
            profileResolved = anonymousIdentity is not null;   // 匿名模式不顯示真實頭貼，代號就是全部
            displayName = maskingRules.ResolveDisplayName(userId, rawDisplayName, anonymousIdentity?.Label);
            pictureUrl = null;
            // fallback 的種子要與 AnonymousIdentityService 的指派邏輯一致（含 groupId），
            // 否則同一個人在別處的代號圖示會對不起來
            avatarIcon = anonymousIdentity?.IconKey ?? AvatarIconCatalog.ForHash($"{groupId}:{userId}").IconKey;
        }
        else
        {
            // 別名模式下設過別名的人，顯示值就是別名、與真名無關，同樣算名稱已解析
            var nameFinal = !string.IsNullOrWhiteSpace(rawDisplayName)
                || maskingRules.HasAliasFor(userId);
            // 頭貼只在 Original 模式顯示；LINE 端沒有頭貼（PictureUrl 空）的人沒東西可等，
            // 有來源網址但圖檔還沒下載成功的才算未定
            var avatarFinal = !maskingRules.RevealsOriginalProfile
                || hasPicture
                || string.IsNullOrWhiteSpace(rawPictureUrl);
            profileResolved = nameFinal && avatarFinal;
            displayName = maskingRules.ResolveDisplayName(userId, rawDisplayName);
            // 非 Original 模式下真實頭貼一律不外流，即使前端不渲染，URL 本身就是身分線索
            pictureUrl = maskingRules.RevealsOriginalProfile && hasPicture
                ? $"api/groups/{groupId}/members/{userId}/avatar"
                : null;
            // 一律附上決定性的 fallback 圖示 key，前端在 PictureUrl 缺失或載入失敗時可以直接換上
            avatarIcon = AvatarIconCatalog.ForHash(userId).IconKey;
        }

        return new ResolvedMemberDto(userId, displayName, pictureUrl, avatarIcon, profileResolved);
    }
}
