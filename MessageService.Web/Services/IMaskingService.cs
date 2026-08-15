namespace MessageService.Web.Services;

/// <summary>每個請求呼叫一次，載入當下的遮蔽設定與規則，避免每則訊息各打一次 DB。</summary>
public interface IMaskingService
{
    Task<IMaskingRuleSet> LoadRulesAsync(CancellationToken cancellationToken);

    /// <summary>設定或規則被修改後呼叫，讓下一次 LoadRulesAsync 重新讀資料庫。</summary>
    void InvalidateCache();
}
