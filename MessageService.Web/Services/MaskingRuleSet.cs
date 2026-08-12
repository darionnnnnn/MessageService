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

    // 身分證：1 個大寫英文字母 + 1 或 2（性別碼，居留證等證件用 8/9）+ 8 碼數字，共 10 碼
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z][12]\d{8}(?!\d)")]
    private static partial Regex NationalIdRegex();

    // 手機：09 開頭共 10 碼數字，容許 3-3-4 或 3-4-4 常見分隔（09XX-XXX-XXX／09XX-XXXX-XX 等寫法不強求，
    // 只認最常見的 0912-345-678 / 0912 345 678 / 09XXXXXXXX 三種）
    [GeneratedRegex(@"(?<!\d)09\d{2}[-\s]?\d{3}[-\s]?\d{3}(?!\d)")]
    private static partial Regex MobilePhoneRegex();

    // 市話：0 + 區碼（1-2 碼，如 02、049）+ 連字號 + 6-8 碼號碼——要求連字號是刻意的，
    // 純數字的市話寫法（沒有分隔）跟一般數字序列難以區分，容易誤判
    [GeneratedRegex(@"(?<!\d)0\d{1,2}-\d{6,8}(?!\d)")]
    private static partial Regex LandlineRegex();

    // 健保卡卡號：固定 12 碼數字。跟身分證／電話格式差異夠大（沒有字母、沒有連字號、
    // 位數不同），依序套用時不會互相誤判；純數字格式沒有其他結構特徵可以更精確辨識，
    // 是這四種格式裡誤判風險相對最高的一個，必要時可在設定頁關閉
    [GeneratedRegex(@"(?<!\d)\d{12}(?!\d)")]
    private static partial Regex NhiCardRegex();
}
