namespace MessageService.Web.Services;

public record AvatarIconInfo(string IconKey, string Label);

/// <summary>
/// 匿名/遮蔽名稱模式下使用的動植物代號圖示庫。刻意避開豬、狗、老鼠、蛇、雞、烏龜、驢、狐狸、猴
/// 等中文口語裡帶貶義聯想的動物（豬頭、狗腿、鼠輩、蛇蠍、縮頭烏龜、蠢驢、狐狸精、潑猴），
/// 全部維持中性偏正面，適合嚴肅場合。實際 SVG 圖示放在 wwwroot/img/default-avatars.svg，
/// IconKey 對應該檔案裡的 sprite id。
/// </summary>
public static class AvatarIconCatalog
{
    public static readonly IReadOnlyList<AvatarIconInfo> Icons =
    [
        new("bear", "小熊"),
        new("cat", "小貓"),
        new("rabbit", "小兔"),
        new("bird", "小鳥"),
        new("deer", "小鹿"),
        new("penguin", "企鵝"),
        new("dolphin", "海豚"),
        new("owl", "貓頭鷹"),
        new("koala", "無尾熊"),
        new("panda", "熊貓"),
        new("sheep", "綿羊"),
        new("otter", "水獺"),
        new("hedgehog", "刺蝟"),
        new("seal", "海豹"),
        new("swan", "天鵝"),
        new("whale", "鯨魚"),
        new("flower", "小花"),
        new("cherry-blossom", "櫻花"),
        new("maple-leaf", "楓葉"),
        new("sunflower", "向日葵"),
        new("tulip", "鬱金香"),
        new("clover", "三葉草"),
        new("ginkgo-leaf", "銀杏"),
        new("lotus", "蓮花"),
    ];

    /// <summary>群組（而非個人）沒有真實頭貼時使用的預設圖示 key，不在成員代號的選取池內。</summary>
    public const string GroupFallbackIconKey = "group";

    /// <summary>依字串雜湊決定性挑選圖示，同樣的 seed 永遠拿到同一個 icon（單純展示用途，不做唯一性保證）。</summary>
    public static AvatarIconInfo ForHash(string seed)
    {
        var hash = 0u;
        foreach (var ch in seed)
        {
            hash = hash * 31 + ch;
        }
        return Icons[(int)(hash % Icons.Count)];
    }
}
