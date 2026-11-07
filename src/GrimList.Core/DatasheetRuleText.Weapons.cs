using System.Text.RegularExpressions;

namespace GrimList.Core;

public static partial class DatasheetRuleText
{
    public static List<DatasheetModifier> WeaponAbilities(string source, string text, bool rosterRule = false)
    {
        List<DatasheetModifier> result = [];
        var index = 0;
        // A complete permanent grant can precede an independent conditional clause.
        // Split only an explicit conjunction introducing that clause, never a trailing
        // 'while/if/only' restriction on the grant itself.
        var clauses = Regex.Replace(Clean(text), @",\s+and\s+(?=(?:each time|while|if|until)\b)", ". ", Rx);
        foreach (var raw in Regex.Split(clauses, @"(?<=\.)\s+"))
        {
            index++;
            var clause = raw;
            EffectCondition? condition = null;
            var conditional = Regex.Match(clause, @"^(?<condition>(?:While|If|Each time|Until) .+?), (?<effect>.+)$", Rx);
            if (conditional.Success)
            {
                condition = new("battlefield", Label: conditional.Groups["condition"].Value);
                clause = conditional.Groups["effect"].Value;
            }
            // These complete subjects define ownership explicitly. Arbitrary prose or
            // incomplete targets must never become an all-weapons grant.
            var match = Regex.Match(clause, @"^(?:(?:The bearer's|This model's|This unit's) (?:(?<type>ranged|melee) )?(?:weapons|attacks)|(?:(?<type>ranged|melee) )?weapons equipped by (?<owner>the bearer|this model|models in (?:this|that) unit|.+? models (?:from|in) your army))(?<filter> that have the \[[^\]]+\] ability)? (?:have|has|gain|gains|lose|loses) (?:the )?(?<abilities>\[[^\]]+\](?:\s*(?:,|and)?\s*\[[^\]]+\])*)(?: abilit(?:y|ies))?\.$", Rx);
            if (!match.Success) continue;
            var owner = match.Groups["owner"].Value;
            var armyOwner = Regex.Match(owner, @"^(?<keys>.+?) models (?:from|in) your army$", Rx);
            if (armyOwner.Success)
            {
                if (!rosterRule) continue; // A bearer aura needs a recipient/distance context.
                var recipient = new EffectCondition("keyword", armyOwner.Groups["keys"].Value);
                condition = condition == null ? recipient : new("and", Children: [recipient, condition], Label: condition.Label);
            }
            else if (rosterRule) continue; // 'this unit' has no defined recipient on an army reference.
            WeaponPredicate? predicate = null;
            if (match.Groups["filter"].Success)
                predicate = new("keyword", Regex.Match(match.Groups["filter"].Value, @"\[([^\]]+)\]").Groups[1].Value);
            var operation = Regex.IsMatch(clause, @"\blose(?:s)?\b", Rx) ? "remove" : "add";
            var type = match.Groups["type"].Value;
            var keywords = Regex.Matches(match.Groups["abilities"].Value, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value);
            result.Add(new("weapon-text/" + EffectiveDatasheets.Key(source) + "/" + index, source, "weapon", "Keywords", operation,
                string.Join(", ", keywords), WeaponType: type.Length == 0 ? "" : char.ToUpperInvariant(type[0]) + type[1..].ToLowerInvariant(),
                Condition: condition, WeaponPredicate: predicate));
        }
        return result;
    }
}
