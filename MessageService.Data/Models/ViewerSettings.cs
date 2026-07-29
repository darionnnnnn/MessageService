namespace MessageService.Models;

/// <summary>全站共用的單列設定（沒有登入機制，設定不分使用者）。</summary>
public class ViewerSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public NameDisplayMode NameDisplayMode { get; set; } = NameDisplayMode.MaskMiddle;
}
