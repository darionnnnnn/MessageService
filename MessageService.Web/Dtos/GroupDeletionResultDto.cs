namespace MessageService.Web.Dtos;

/// <summary>群組刪除或歷史訊息刪除的執行結果 DTO。</summary>
public record GroupDeletionResultDto(
    int MessageCount, int MemberCount, int AnonymousIdentityCount, int MaskKeywordScopeCount,
    int HighlightScopeCount);
