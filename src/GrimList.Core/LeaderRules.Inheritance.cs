using System.Text.RegularExpressions;

namespace GrimList.Core;

public static partial class LeaderRules
{
    // Recipient-side permission only. Never copy the referenced unit's keywords,
    // partner permissions, slots or effects onto the actual bodyguard.
    private static List<InheritedLeaderRule> InheritedLeaders(UnitProfile profile)
    {
        List<InheritedLeaderRule> rules = [];
        foreach (var ability in profile.Abilities ?? [])
        {
            var match = Regex.Match(CleanRule(ability.Text),
                @"\AIf an? (?<leader>[^.]+?) can be attached to an? (?<target>[^.,]+?)(?: unit)?, (?:then )?it can be attached to this unit instead(?:\.|\z)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var leader = Regex.Match(match.Groups["leader"].Value,
                @"\A(?<keywords>[\w '/\-]+?)(?: (?:unit|model))? from your army(?<requires> with the Leader ability)?(?: \(excluding (?<excluded>[\w '/\-]+)\))?\z", RegexOptions.IgnoreCase);
            if (!leader.Success) continue;
            // Some sources repeat CHARACTER UNIT before the grammatical "unit".
            var keywords = Regex.Replace(leader.Groups["keywords"].Value, @"\s+(?:unit|model)\z", "", RegexOptions.IgnoreCase);
            rules.Add(new(match.Groups["target"].Value.Trim(), keywords, leader.Groups["excluded"].Value,
                leader.Groups["requires"].Success, ability.Text));
        }
        return rules;
    }

    private static bool InheritsLeader(ArmyList army, Dataset data, ArmyEntry leader, UnitDefinition leaderUnit,
        UnitDefinition bodyguard, HashSet<string>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(bodyguard.Key)) return false;
        try
        {
            var leaderRules = Semantics(leaderUnit.Profile);
            var keywords = (leaderUnit.Profile?.Keywords ?? []).Append(leaderUnit.Name).Append(leaderUnit.Category).ToList();
            string SingularHeroes(string value) => Regex.Replace(value, @"\bHeroes\b", "Hero", RegexOptions.IgnoreCase);
            bool Matches(string terms) => EffectiveDatasheets.MatchesKeywords(keywords.Select(SingularHeroes), SingularHeroes(terms));
            foreach (var rule in Semantics(bodyguard.Profile).InheritedLeaders ?? [])
            {
                if (rule.RequiresLeader && (!leaderRules.CanAttach || leaderRules.IsSupport)
                    || !Matches(rule.LeaderKeywords) || rule.ExcludedKeywords.Length > 0 && Matches(rule.ExcludedKeywords)) continue;
                // A prose target list proves capability without requiring X in the
                // roster or even in this library. Structured permissions must instead
                // be evaluated against X's real identity, retaining owner conditions.
                if (leaderUnit.Profile?.Semantics?.Attachments is not { Count: > 0 }
                    && leaderRules.BodyguardTargets.Any(t => UnitIdentity.Normalize(t) == UnitIdentity.Normalize(rule.BodyguardTarget))) return true;
                foreach (var reference in data.Units.Where(u => u.Faction == bodyguard.Faction && MatchesTerm(u, rule.BodyguardTarget)))
                {
                    var id = "attachment-reference/" + reference.Key;
                    while (army.Entries.Any(e => e.Id == id)) id += "/";
                    var target = new ArmyEntry(id, reference.Key, reference.Name, reference.Options.FirstOrDefault()?.Models ?? 1);
                    var permission = EffectiveDatasheets.AttachmentAllowed(army, data, leader, leaderUnit, target, reference);
                    if ((permission ?? leaderRules.BodyguardTargets.Any(t => MatchesTerm(reference, t)))
                        || InheritsLeader(army, data, leader, leaderUnit, reference, visited)) return true;
                }
            }
            return false;
        }
        finally { visited.Remove(bodyguard.Key); }
    }
}
