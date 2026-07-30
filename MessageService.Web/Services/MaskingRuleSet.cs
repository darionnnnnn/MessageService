using MessageService.Models;

namespace MessageService.Web.Services;

public class MaskingRuleSet(
    NameDisplayMode mode,
    IReadOnlyList<MaskKeyword> keywords,
    IReadOnlyDictionary<string, string> aliases) : IMaskingRuleSet
{
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
}
