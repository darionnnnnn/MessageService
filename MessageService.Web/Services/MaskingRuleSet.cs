using System.Text.RegularExpressions;
using MessageService.Models;

namespace MessageService.Web.Services;

public partial class MaskingRuleSet(
    NameDisplayMode mode,
    IReadOnlyList<MaskKeyword> keywords,
    IReadOnlyDictionary<string, string> aliases,
    PiiMaskingSettings? piiSettings = null) : IMaskingRuleSet
{
    private readonly PiiMaskingSettings _pii = piiSettings ?? PiiMaskingSettings.AllEnabled;

    public string MaskText(string groupId, string text)
    {
        foreach (var rule in keywords)
        {
            if (string.IsNullOrEmpty(rule.Keyword) || !AppliesToGroup(rule, groupId))
            {
                continue;
            }

            var replacement = rule.Replacement ?? new string('*', rule.Keyword.Length);
            text = text.Replace(rule.Keyword, replacement, StringComparison.OrdinalIgnoreCase);
        }

        // 內建個資格式偵測：跟上面的關鍵字規則不同，這裡是「長得像」身分證/手機/市話/健保卡
        // 就自動遮蔽，不需要使用者事先知道要輸入什麼關鍵字當規則。四種格式的字元組成夠不同
        // （身分證以字母開頭、手機固定 09 開頭全數字、市話要求連字號、健保卡固定 12 碼數字），
        // 依序套用不會互相誤吃彼此的比對範圍；已經被前面規則遮成 * 的部分不會再符合後面的格式。
        if (_pii.MaskNationalId)
        {
            text = NationalIdRegex().Replace(text, m => MaskMiddle(m.Value));
        }
        if (_pii.MaskMobilePhone)
        {
            text = MobilePhoneRegex().Replace(text, m => MaskMiddle(m.Value));
        }
        if (_pii.MaskLandline)
        {
            text = LandlineRegex().Replace(text, m => MaskMiddle(m.Value));
        }
        if (_pii.MaskNhiCard)
        {
            text = NhiCardRegex().Replace(text, m => MaskMiddle(m.Value));
        }

        return text;
    }

    public string ResolveDisplayName(string userId, string? rawDisplayName, string? anonymousLabel = null)
    {
        if (mode == NameDisplayMode.Anonymous)
        {
            return anonymousLabel ?? "(未知)";
        }

        if (mode == NameDisplayMode.Original)
        {
            return rawDisplayName ?? userId;
        }

        if (mode == NameDisplayMode.CustomAlias && aliases.TryGetValue(userId, out var alias))
        {
            return alias;
        }

        // MaskMiddle，或 CustomAlias 沒設別名時的 fallback
        return MaskMiddle(rawDisplayName ?? userId);
    }

    public bool RevealsOriginalProfile => mode == NameDisplayMode.Original;

    public bool RequiresAnonymousIdentity => mode == NameDisplayMode.Anonymous;

    private static bool AppliesToGroup(MaskKeyword rule, string groupId) =>
        rule.ApplyToAllGroups || rule.Groups.Any(g => g.GroupId == groupId);

    /// <summary>首尾字保留、中間 * 遮蔽；2 字只留首字（如「小明」→「小*」）；1 字全遮。</summary>
    private static string MaskMiddle(string name) => name.Length switch
    {
        0 => name,
        1 => "*",
        2 => name[0] + "*",
        _ => name[0] + new string('*', name.Length - 2) + name[^1]
    };

    // 邊界一律用「前後不是數字／英數字」的環視斷言，不用 \b——.NET regex 的 \b 是以 \w 為準，
    // 中日韓文字元也算 \w，中文緊貼著數字/英文字母時（現實聊天訊息幾乎都是這樣，例如
    // 「身分證是A123456789啦」中間沒有空白）\b 在那個位置不會成立，會讓整條規則失效。

    // 四條規則的前後邊界一律用 (?<![A-Za-z0-9]) / (?![A-Za-z0-9])：只擋數字是不夠的，
    // 那樣「ORD0912345678」這種英數混合的訂單編號會被咬掉後半段變成 ORD0********8。

    // 身分證／統一證號，共 10 碼。大小寫都要認——真實聊天訊息不會有人特地按 Shift 打大寫，
    // 只認 [A-Z] 等於在最常見的輸入上直接失效。這裡把大小寫寫進字元類而不是用
    // RegexOptions.IgnoreCase，是因為 IgnoreCase 會連帶影響前後環視斷言，寫死比較好推理。
    // 第二碼涵蓋三種格式：本國身分證（1/2）、2021 年起的新式外來人口統一證號（8/9）、
    // 舊式居留證統一證號（第二碼是 A~D 的英文字母）。第二碼刻意不放寬成任意字母——
    // 「兩個字母 + 8 碼數字」太常見了（PO12345678 這類單號就是），限定在真的會出現的
    // 性別／證件類別碼可以把誤判擋掉一大半。
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z][1289A-Da-d]\d{8}(?![A-Za-z0-9])")]
    private static partial Regex NationalIdRegex();

    // 手機：09 開頭共 10 碼數字，容許 0912-345-678 / 0912 345 678 / 09XXXXXXXX 三種常見寫法。
    // 分隔符只認連字號與半形／全形空白，不用 \s——\s 含換行，會把「0912\n345\n678」這種
    // 跨行的數字併成一個號碼吃掉，破壞訊息版面。
    [GeneratedRegex(@"(?<![A-Za-z0-9])09\d{2}[- 　]?\d{3}[- 　]?\d{3}(?![A-Za-z0-9])")]
    private static partial Regex MobilePhoneRegex();

    // 市話：區碼（0 + 2~8，含 037／049 這類 3 碼區碼）+ 分隔 + 號碼。
    // 第一個分隔符是必要的，這點沿用原本的設計——沒有分隔的純數字市話跟一般數字序列
    // （金額、訂單號、日期串）無法區分，硬要認會誤遮正常內容。
    // 第二個分隔符則是可選的，因為 02-2345-6789 是台北市話最普遍的寫法，
    // 原本只認單一連字號（0\d{1,2}-\d{6,8}）會整個漏掉它。
    // 區碼限定 0[2-8] 而不是 0\d：01／09 都不是有效區碼，放寬會把「01-20250812」這種
    // 「前綴-日期」型的訂單／批號整段誤遮。
    [GeneratedRegex(@"(?<![A-Za-z0-9])0[2-8]\d?[- 　]\d{3,4}[- 　]?\d{4}(?![A-Za-z0-9])")]
    private static partial Regex LandlineRegex();

    // 健保卡卡號：固定 12 碼數字。跟身分證／電話格式差異夠大（沒有字母、沒有連字號、
    // 位數不同），依序套用時不會互相誤判；純數字格式沒有其他結構特徵可以更精確辨識，
    // 是這四種格式裡誤判風險相對最高的一個，必要時可在設定頁關閉
    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{12}(?![A-Za-z0-9])")]
    private static partial Regex NhiCardRegex();
}
