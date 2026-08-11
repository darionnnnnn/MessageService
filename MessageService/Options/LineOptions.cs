namespace MessageService.Options;

public class LineOptions
{
    public const string SectionName = "Line";

    public string ChannelSecret { get; set; } = "";
    public string ChannelAccessToken { get; set; } = "";

    /// <summary>這台主機是否要對外呼叫 LINE API（媒體下載＋頭貼快取，兩者都只需要 outbound HTTPS，
    /// 共用同一個開關）。Full 模式恆真；Line／Db 拆機時，一對主機裡恰好一台要設 true——
    /// 啟動時無法互相檢查，設錯（兩台都真或都假）不會啟動失敗，只會變成重複下載或永遠不下載，
    /// 靠部署檢查表把關，見 docs/DEPLOYMENT-MODES.md。</summary>
    public bool OutboundHere { get; set; } = true;
}
