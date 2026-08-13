using System.Linq.Expressions;
using MessageService.Data;
using MessageService.Data.Crypto;
using MessageService.Models;
using MessageService.Services;
using MessageService.Web.Dtos;
using MessageService.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MessageService.Web.Controllers.Api;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）
[ApiController]
[RequiresCapability(Capability.Viewer)]
public class MessagesController(
    MessageDbContext dbContext,
    ContentStreamService contentStreamService,
    IMaskingService maskingService,
    IAnonymousIdentityService anonymousIdentityService,
    IOptions<EncryptionOptions> encryptionOptions) : ControllerBase
{
    public const int MaxDays = 3650;

    /// <summary>單次回應的訊息筆數硬上限——沒有這個上限，忙碌群組按幾次「載入更早」之後
    /// DOM 會累積上萬個節點，捲動開始掉幀。截斷時哪一端被丟棄依查詢方向而定，見 GetMessages。</summary>
    public const int MessageWindowLimit = 500;

    [HttpGet("api/groups/{groupId}/messages")]
    public async Task<ActionResult<MessagesPageDto>> GetMessages(
        string groupId,
        [FromQuery] int days = 3,
        [FromQuery] long? beforeId = null,
        [FromQuery] long? afterId = null,
        [FromQuery] long? aroundId = null,
        CancellationToken cancellationToken = default)
    {
        // 上限刻意遠高於收錄端的保留年限（預設 3 年），這樣前端在沒有游標可用時
        // 靠放大天數視窗也一定能觸及所有仍保留的訊息
        days = Math.Clamp(days, 1, MaxDays);

        List<MessageRow> rows;
        bool truncated;

        if (aroundId is { } around)
        {
            var aroundResult = await GetMessagesAroundAnchorAsync(groupId, around, cancellationToken);
            if (aroundResult is null)
            {
                return NotFound();
            }
            (rows, truncated) = aroundResult.Value;
        }
        else
        {
            IQueryable<GroupMessage> query = dbContext.GroupMessages.Where(m => m.GroupId == groupId);

            if (afterId is { } after)
            {
                query = query.Where(m => m.Id > after);
            }
            else if (beforeId is { } before)
            {
                var cursor = await dbContext.GroupMessages
                    .Where(m => m.Id == before)
                    .Select(m => new { m.EventTimestamp })
                    .FirstOrDefaultAsync(cancellationToken);

                if (cursor is null)
                {
                    return NotFound();
                }

                // 下一則更早訊息（依 Id，即實際到達順序）。若它跟游標之間的空窗比 days 還長，
                // 就改以它為基準開窗；否則群組沉寂超過一個視窗時，查詢永遠回空、游標不會前進，
                // 使用者會一直按「載入更早」卻什麼都不會出現
                var nextOlder = await dbContext.GroupMessages
                    .Where(m => m.GroupId == groupId && m.Id < before)
                    .OrderByDescending(m => m.Id)
                    .Select(m => new { m.EventTimestamp })
                    .FirstOrDefaultAsync(cancellationToken);

                var anchor = cursor.EventTimestamp;
                var plainCutoff = anchor.AddDays(-days);
                if (nextOlder is not null && nextOlder.EventTimestamp < plainCutoff)
                {
                    anchor = nextOlder.EventTimestamp;
                }

                var cutoff = anchor.AddDays(-days);
                query = query.Where(m => m.Id < before && m.EventTimestamp >= cutoff);
            }
            else
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
                query = query.Where(m => m.EventTimestamp >= cutoff);
            }

            // 截斷方向依查詢意圖而定：afterId（輪詢）要保留離游標最近、時間上最早的那批，往前追趕；
            // 初載／beforeId 都是「往回看」的視窗，越接近游標（或現在）越優先，被丟的是視窗裡
            // 更久遠的那一批。多撈一筆（MessageWindowLimit + 1）用來判斷是否真的被截斷，
            // 不必另外一次 COUNT 查詢。
            IOrderedQueryable<GroupMessage> capOrdered = afterId is not null
                ? query.OrderBy(m => m.Id)
                : query.OrderByDescending(m => m.Id);

            var capped = await capOrdered
                .Take(MessageWindowLimit + 1)
                .Select(MessageRow.Projection)
                .ToListAsync(cancellationToken);

            truncated = capped.Count > MessageWindowLimit;
            rows = capped.Take(MessageWindowLimit).OrderBy(r => r.Id).ToList();
        }

        var userIds = rows.Select(r => r.UserId).Where(id => id is not null).Cast<string>().Distinct().ToList();
        var members = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => m.GroupId == groupId && userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => m.UserId, cancellationToken);

        // 一個請求只載入一次遮蔽規則，套用到每則訊息時全是同步運算，不會每則訊息各打一次 DB
        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        // 只有 Anonymous 模式才需要查/指派永久代號；其他模式完全不打這張表
        IReadOnlyDictionary<string, AnonymousIdentityInfo> anonymousIdentities =
            new Dictionary<string, AnonymousIdentityInfo>();
        if (maskingRules.RequiresAnonymousIdentity)
        {
            anonymousIdentities = await anonymousIdentityService.GetOrAssignAsync(groupId, userIds, cancellationToken);
        }

        var messages = rows.Select(r =>
        {
            var text = r.Text is null ? null : maskingRules.MaskText(groupId, r.Text);
            var content = r.Content is null
                ? null
                : new MessageContentDto(r.Content.Id, r.Content.FileName, r.Content.ContentType, r.Content.DownloadStatus.ToString());

            if (r.UserId is null)
            {
                return new MessageDto(r.Id, r.MessageType, text, null, "(未知)", r.EventTimestamp, content, null, null, r.StickerId);
            }

            members.TryGetValue(r.UserId, out var member);

            string displayName;
            string? pictureUrl;
            string avatarIcon;
            if (maskingRules.RequiresAnonymousIdentity)
            {
                var identity = anonymousIdentities[r.UserId];
                displayName = maskingRules.ResolveDisplayName(r.UserId, member?.DisplayName, identity.Label);
                pictureUrl = null;
                avatarIcon = identity.IconKey;
            }
            else
            {
                displayName = maskingRules.ResolveDisplayName(r.UserId, member?.DisplayName);
                // 非 Original 模式下真實頭貼一律不外流，即使前端不渲染，URL 本身就是身分線索
                pictureUrl = maskingRules.RevealsOriginalProfile ? member?.PictureUrl : null;
                // 一律附上決定性的 fallback 圖示 key，前端在 PictureUrl 缺失或載入失敗時可以直接換上
                avatarIcon = AvatarIconCatalog.ForHash(r.UserId).IconKey;
            }

            return new MessageDto(r.Id, r.MessageType, text, r.UserId, displayName, r.EventTimestamp, content, pictureUrl, avatarIcon, r.StickerId);
        }).ToList();

        // hasMore：初載/往前加載都要判斷是否還有更早的訊息；輪詢（afterId）不需要，省一次查詢
        var oldestFetchedId = messages.Count > 0 ? messages[0].Id : beforeId ?? long.MaxValue;
        var hasMore = afterId is null &&
            await dbContext.GroupMessages.AnyAsync(m => m.GroupId == groupId && m.Id < oldestFetchedId, cancellationToken);

        // 初載時即使畫面上顯示的天數視窗內剛好沒有訊息，前端輪詢仍需要一個基準 id 才能偵測後續新訊息；
        // 往前加載/輪詢本身不需要，只有初載才算，省不必要的查詢
        long? latestId = null;
        if (beforeId is null && afterId is null && aroundId is null)
        {
            latestId = await dbContext.GroupMessages
                .Where(m => m.GroupId == groupId)
                .Select(m => (long?)m.Id)
                .MaxAsync(cancellationToken);
        }

        return Ok(new MessagesPageDto(messages, hasMore, latestId, truncated));
    }

    private record MessageContentRow(long Id, string? FileName, string? ContentType, DownloadStatus DownloadStatus);

    private record MessageRow(
        long Id, string MessageType, string? Text, string? UserId, string? StickerId,
        DateTimeOffset EventTimestamp, MessageContentRow? Content)
    {
        public static readonly Expression<Func<GroupMessage, MessageRow>> Projection = m => new MessageRow(
            m.Id, m.MessageType, m.Text, m.UserId, m.StickerId, m.EventTimestamp,
            m.Content == null
                ? null
                : new MessageContentRow(m.Content.Id, m.Content.FileName, m.Content.ContentType, m.Content.DownloadStatus));
    }

    /// <summary>問題6：aroundId 原本是 `OrderBy(Math.Abs(Id - anchor))`，翻成 SQL 是
    /// `ORDER BY ABS(Id - @anchor)`——非 sargable，資料庫得把整個候選集排序一次才能取出最接近
    /// 錨點的那批，錨點落在大群組舊訊息時會明顯變慢。改成錨點兩側各查一次，各自走
    /// `(GroupId, Id)` 索引直接 Take，不需要排序整個候選集。
    ///
    /// 語意差異：舊版是「整體最近 MessageWindowLimit 則」，錨點在視窗邊緣時可以整批都在
    /// 同一側（例如錨點是群組最早的訊息，500 則全部來自較新的那一側）；新版是「兩側各最多
    /// MessageWindowLimit/2 則」，任一側不足額不會把配額讓給另一側。對搜尋跳轉場景（使用者
    /// 從搜尋結果跳到某則訊息，通常想看的是它前後的對話脈絡）這個差異感覺不出來，換到的是
    /// 兩段查詢都能用索引。
    ///
    /// 回傳 null 代表錨點不存在（呼叫端應回 404）。</summary>
    private async Task<(List<MessageRow> Rows, bool Truncated)?> GetMessagesAroundAnchorAsync(
        string groupId, long around, CancellationToken cancellationToken)
    {
        var anchorExists = await dbContext.GroupMessages
            .AnyAsync(m => m.Id == around && m.GroupId == groupId, cancellationToken);
        if (!anchorExists)
        {
            return null;
        }

        var halfWindow = MessageWindowLimit / 2;

        var olderOrEqual = await dbContext.GroupMessages
            .Where(m => m.GroupId == groupId && m.Id <= around)
            .OrderByDescending(m => m.Id)
            .Take(halfWindow + 1)
            .Select(MessageRow.Projection)
            .ToListAsync(cancellationToken);

        var newer = await dbContext.GroupMessages
            .Where(m => m.GroupId == groupId && m.Id > around)
            .OrderBy(m => m.Id)
            .Take(halfWindow + 1)
            .Select(MessageRow.Projection)
            .ToListAsync(cancellationToken);

        var truncated = olderOrEqual.Count > halfWindow || newer.Count > halfWindow;
        var rows = olderOrEqual.Take(halfWindow)
            .Concat(newer.Take(halfWindow))
            .OrderBy(r => r.Id)
            .ToList();

        return (rows, truncated);
    }

    public const int SearchResultLimit = 100;
    private const int SearchCandidateLimit = 300;

    /// <summary>內容命中／姓名命中各自的保留配額，見 Search 方法內的說明。</summary>
    private const int SearchQuotaPerCategory = 50;

    [HttpGet("api/messages/search")]
    public async Task<ActionResult<IReadOnlyList<MessageSearchResultDto>>> Search(
        [FromQuery] string? q,
        [FromQuery] string? groupId,
        CancellationToken cancellationToken)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return Ok(Array.Empty<MessageSearchResultDto>());
        }

        var maskingRules = await maskingService.LoadRulesAsync(cancellationToken);

        // === 名稱比對：解析後名稱含關鍵字的成員，他們的訊息全部算命中（不管訊息內容本身有沒有關鍵字）===
        var memberQuery = dbContext.GroupMembers.AsNoTracking().AsQueryable();
        if (groupId is not null)
        {
            memberQuery = memberQuery.Where(m => m.GroupId == groupId);
        }
        var members = await memberQuery.ToListAsync(cancellationToken);

        // Anonymous 模式的代號只讀不指派——沒被指派過代號的人姓名比對略過是正確行為，
        // 指派只應該發生在訊息視窗端點（使用者實際看到訊息時）
        Dictionary<(string GroupId, string UserId), string> anonymousLabels = [];
        if (maskingRules.RequiresAnonymousIdentity)
        {
            var identityQuery = dbContext.AnonymousIdentities.AsNoTracking().AsQueryable();
            if (groupId is not null)
            {
                identityQuery = identityQuery.Where(a => a.GroupId == groupId);
            }
            anonymousLabels = await identityQuery
                .ToDictionaryAsync(a => (a.GroupId, a.UserId), a => a.Label, cancellationToken);
        }

        var nameMatchedKeys = new List<(string GroupId, string UserId)>();
        foreach (var member in members)
        {
            string displayName;
            if (maskingRules.RequiresAnonymousIdentity)
            {
                if (!anonymousLabels.TryGetValue((member.GroupId, member.UserId), out var label))
                {
                    continue;
                }
                displayName = maskingRules.ResolveDisplayName(member.UserId, member.DisplayName, label);
            }
            else
            {
                displayName = maskingRules.ResolveDisplayName(member.UserId, member.DisplayName);
            }

            if (displayName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                nameMatchedKeys.Add((member.GroupId, member.UserId));
            }
        }

        // === 內容比對：SQL 端用原文 LIKE 撈候選，之後用遮蔽後文字複驗，避免搜尋變成遮蔽的後門 ===
        // 加密模式例外：Text 欄位在 SQL 端存的是密文，LIKE 對密文做子字串比對沒有意義（GCM
        // 加密後同樣的明文子字串在密文裡不會有任何對應關係），SQL LIKE 下推完全失效。改成只在
        // 最近 Encryption:SearchWindowDays 天內的文字訊息解密後在記憶體比對——EventTimestamp
        // 不是加密欄位，範圍過濾正常下推到 SQL；Text 透過 ValueConverter 在投影時自動解密。
        IQueryable<GroupMessage> baseQuery = dbContext.GroupMessages.AsNoTracking();
        if (groupId is not null)
        {
            baseQuery = baseQuery.Where(m => m.GroupId == groupId);
        }

        IQueryable<GroupMessage> textQuery = baseQuery.Where(m => m.MessageType == "text" && m.Text != null);
        if (encryptionOptions.Value.Enabled)
        {
            // 密文沒辦法用 LIKE 做子字串比對，只能撈回來解密後在記憶體比對——所以除了天數視窗
            // 之外一定還要有筆數上限：沒有 groupId 時這個查詢涵蓋「所有群組最近 N 天的全部文字
            // 訊息」，忙碌群組隨便就是幾萬則，每一則還會在具現化時跑一次 AES-GCM 解密。少了
            // Take，任何進得來的人（本站只有 IP 白名單、沒有登入）連打幾次搜尋就能把記憶體與
            // CPU 吃光。下面配額的 break 是在具現化之後才發生的，救不了這件事。
            var cutoff = DateTimeOffset.UtcNow.AddDays(-encryptionOptions.Value.EffectiveSearchWindowDays);
            textQuery = textQuery.Where(m => m.EventTimestamp >= cutoff)
                .OrderByDescending(m => m.Id)
                .Take(SearchCandidateLimit);
        }
        else
        {
            var likePattern = $"%{EscapeLikePattern(q)}%";
            textQuery = textQuery.Where(m => EF.Functions.Like(m.Text, likePattern, "\\"))
                .OrderByDescending(m => m.Id)
                .Take(SearchCandidateLimit);
        }

        var textCandidates = await textQuery
            .Select(m => new { m.Id, m.GroupId, m.UserId, m.MessageType, m.Text, m.EventTimestamp })
            .ToListAsync(cancellationToken);

        // 內容命中與姓名命中各自保留固定配額（見類別常數說明）——不然搜「王」這種同時是常見字
        // 又是姓氏的關鍵字，結果會被該姓氏成員的近期訊息灌滿，真正含關鍵字的訊息反而排不進來
        var merged = new Dictionary<long, (string GroupId, string? UserId, string MessageType, string? Text, DateTimeOffset EventTimestamp)>();

        // textCandidates 已經照 Id（＝到達順序）由新到舊排好，依序取前 SearchQuotaPerCategory 筆
        // 複驗通過的，就是最近的配額筆數，不必額外排序
        foreach (var row in textCandidates)
        {
            if (merged.Count >= SearchQuotaPerCategory)
            {
                break;
            }

            // 遮蔽後複驗：被關鍵字規則遮掉的詞（例如「密碼」）搜不到，摘要也只會顯示遮蔽後的文字
            var maskedText = maskingRules.MaskText(row.GroupId, row.Text!);
            if (maskedText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                merged[row.Id] = (row.GroupId, row.UserId, row.MessageType, row.Text, row.EventTimestamp);
            }
        }

        if (nameMatchedKeys.Count > 0)
        {
            var nameCandidates = new List<(long Id, string GroupId, string? UserId, string MessageType, string? Text, DateTimeOffset EventTimestamp)>();
            foreach (var group in nameMatchedKeys.GroupBy(k => k.GroupId))
            {
                var userIds = group.Select(k => k.UserId).ToList();
                var rows = await dbContext.GroupMessages
                    .AsNoTracking()
                    .Where(m => m.GroupId == group.Key && m.UserId != null && userIds.Contains(m.UserId))
                    .OrderByDescending(m => m.Id)
                    .Take(SearchCandidateLimit)
                    .Select(m => new { m.Id, m.GroupId, m.UserId, m.MessageType, m.Text, m.EventTimestamp })
                    .ToListAsync(cancellationToken);

                nameCandidates.AddRange(rows.Select(r => (r.Id, r.GroupId, r.UserId, r.MessageType, r.Text, r.EventTimestamp)));
            }

            // 多個成員符合姓名比對時，候選是分別各撈一批再合併的，要重新依時間排序才能正確
            // 取出「整體最近」的配額筆數，不是每個成員各自取到配額
            foreach (var row in nameCandidates.OrderByDescending(r => r.EventTimestamp).Take(SearchQuotaPerCategory))
            {
                merged.TryAdd(row.Id, (row.GroupId, row.UserId, row.MessageType, row.Text, row.EventTimestamp));
            }
        }

        var top = merged
            .OrderByDescending(kv => kv.Value.EventTimestamp)
            .Take(SearchResultLimit)
            .ToList();

        if (top.Count == 0)
        {
            return Ok(Array.Empty<MessageSearchResultDto>());
        }

        var resultGroupIds = top.Select(kv => kv.Value.GroupId).Distinct().ToList();
        var groupCache = await dbContext.Groups
            .AsNoTracking()
            .Where(g => resultGroupIds.Contains(g.GroupId))
            .ToDictionaryAsync(g => g.GroupId, cancellationToken);

        var resultMembers = await dbContext.GroupMembers
            .AsNoTracking()
            .Where(m => resultGroupIds.Contains(m.GroupId))
            .ToDictionaryAsync(m => (m.GroupId, m.UserId), cancellationToken);

        var results = top.Select(kv =>
        {
            var id = kv.Key;
            var rowGroupId = kv.Value.GroupId;
            var userId = kv.Value.UserId;
            var messageType = kv.Value.MessageType;
            var text = kv.Value.Text;
            var eventTimestamp = kv.Value.EventTimestamp;

            groupCache.TryGetValue(rowGroupId, out var group);
            var groupDisplayName = group?.GroupName ?? rowGroupId;

            string displayName;
            if (userId is null)
            {
                displayName = "(未知)";
            }
            else
            {
                resultMembers.TryGetValue((rowGroupId, userId), out var member);
                displayName = maskingRules.RequiresAnonymousIdentity
                    ? maskingRules.ResolveDisplayName(
                        userId, member?.DisplayName,
                        anonymousLabels.TryGetValue((rowGroupId, userId), out var label) ? label : null)
                    : maskingRules.ResolveDisplayName(userId, member?.DisplayName);
            }

            var snippet = MessagePreviewFormatter.Format(messageType, text, maskingRules, rowGroupId);

            return new MessageSearchResultDto(id, rowGroupId, groupDisplayName, displayName, snippet, eventTimestamp);
        }).OrderByDescending(r => r.EventTimestamp).ToList();

        return Ok(results);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    [HttpGet("api/messages/{id:long}/content")]
    public async Task<IActionResult> GetContent(long id, CancellationToken cancellationToken)
    {
        var rangeHeader = Request.Headers.Range.ToString();
        var ifNoneMatch = Request.Headers.IfNoneMatch.ToString();
        var result = await contentStreamService.StreamAsync(
            id,
            string.IsNullOrEmpty(rangeHeader) ? null : rangeHeader,
            string.IsNullOrEmpty(ifNoneMatch) ? null : ifNoneMatch,
            Response, cancellationToken);

        return result == ContentStreamResult.NotFound ? NotFound() : new EmptyResult();
    }

    [HttpGet("api/messages/statuses")]
    public async Task<ActionResult<IReadOnlyList<MessageStatusDto>>> GetStatuses(
        [FromQuery] string? ids, CancellationToken cancellationToken)
    {
        var contentIds = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        if (contentIds.Count == 0)
        {
            return Ok(Array.Empty<MessageStatusDto>());
        }

        var statuses = await dbContext.MessageContents
            .Where(c => contentIds.Contains(c.Id))
            .Select(c => new MessageStatusDto(c.Id, c.DownloadStatus.ToString(), c.ContentType))
            .ToListAsync(cancellationToken);

        return Ok(statuses);
    }
}
