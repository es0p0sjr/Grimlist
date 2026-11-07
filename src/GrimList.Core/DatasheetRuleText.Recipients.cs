using System.Text.RegularExpressions;

namespace GrimList.Core;

public static partial class DatasheetRuleText
{
    private static List<DatasheetModifier> RecipientGrants(AbilityProfile ability)
    {
        var text = Clean(ability.Text);
        var match = Regex.Match(text, @"^While a (?<keys>[\w '-]+?) model is leading this unit, that (?<target>[\w '-]+?)(?: model)? (?<effect>(?:has|gains?) .+)\.$", Rx);
        var scope = "leaders";
        if (!match.Success)
        {
            match = Regex.Match(text, @"^While this unit is in the same unit as a (?<keys>[\w '-]+?) model, that (?<target>[\w '-]+?)(?: model)? (?<effect>(?:has|gains?) .+)\.$", Rx);
            scope = "other-members";
        }
        if (!match.Success || EffectiveDatasheets.Key(match.Groups["keys"].Value) != EffectiveDatasheets.Key(match.Groups["target"].Value)) return [];
        // Reuse the ordinary grant grammar only after establishing an explicit
        // recipient. Trailing combat/phase restrictions remain unparsed, not permanent.
        return Grants(ability.Name, "This model " + match.Groups["effect"].Value + ".", "recipient/" + EffectiveDatasheets.Key(ability.Name))
            .Select(m => m with { Recipient = new(scope, new("keyword", match.Groups["keys"].Value)),
                Characteristic = m.Target == "ability" ? ability.Text : m.Characteristic }).ToList();
    }
}
