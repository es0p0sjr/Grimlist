using System.Text.RegularExpressions;

namespace GrimList.Core;

public static class EnhancementDefences
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static List<RuleDefinition> FeelNoPain(ReferenceCard card)
    {
        var text = Regex.Replace(card.Text.Replace("**", "").Replace("^^", ""), @"\s+", " ").Trim();
        List<RuleDefinition> rules = [];
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?])\s+"))
        {
            // Match an explicit grant to the bearer. Aura/selected-unit grants and generic
            // definitions must never become a defensive stat on the enhancement's owner.
            var grant = Regex.Match(sentence, @"\b(?:the bearer|this model) (?:has|gains?) (?:the |a )?(?:(?:Save|Wounds|Toughness|Leadership|Objective Control) characteristic of \d+\+? and (?:the )?)?Feel No Pain\s*(?<roll>[2-6])\+(?: ability)?", Options);
            if (!grant.Success) continue;
            var before = sentence[..grant.Index].Trim();
            var after = sentence[(grant.Index + grant.Length)..].Trim().TrimEnd('.').Trim();
            var additive = Regex.IsMatch(before, @"^Add \d+ to the bearer['’]s (?:Save|Wounds|Toughness|Leadership|Objective Control) characteristic and$", Options);
            var conditional = Regex.IsMatch(before, @"^(?:While|If|When|Once per|Until|During|At the)\b", Options);
            if (before.Length > 0 && !additive && !conditional) continue;
            var extraStat = Regex.IsMatch(after, @"^and (?:a |an? )?(?:Save|Wounds|Toughness|Leadership|Objective Control) characteristic of \d+\+?$", Options);
            var restricted = conditional || after.Length > 0 && !extraStat;
            rules.Add(new(grant.Groups["roll"].Value + "+++ FNP" + (restricted ? "*" : ""),
                $"{Enhancements.Kind(card)}: {card.Name}\n{card.Detachment}\n\n{card.Text}"));
        }
        return rules.Distinct().ToList();
    }
}
