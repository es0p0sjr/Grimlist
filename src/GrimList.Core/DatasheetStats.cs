using System.Text.RegularExpressions;

namespace GrimList.Core;

public static class DatasheetStats
{
    public static List<RuleDefinition> FeelNoPain(ArmyList army, Dataset? data, UnitDefinition? unit, ArmyEntry? entry)
    {
        if (entry != null && (unit == null || unit.Profile?.Semantics?.Version >= 2 && ReferenceEquals(unit, data?.FindUnit(entry.UnitKey))))
            unit = EffectiveDatasheets.Project(army, entry, data)?.Unit ?? unit;
        var rules = FeelNoPain(entry != null && unit?.Profile != null
            ? unit.Profile with { Abilities = Loadouts.Abilities(unit, entry.Loadout) } : unit?.Profile);
        if (entry != null && Enhancements.Find(army, data, entry.Enhancement) is { } enhancement
            && Enhancements.Eligible(army, MusterEffects.EffectiveUnit(army, entry, data) ?? unit, enhancement, data))
            rules.AddRange(EnhancementDefences.FeelNoPain(enhancement).Where(r => !rules.Any(existing => existing.Name == r.Name)));
        return rules.Distinct().ToList();
    }

    public static string FeelNoPainLabel(IEnumerable<RuleDefinition> rules)
    {
        var labels = rules.Select(r => r.Name).Distinct().ToList();
        // FNP rolls do not stack. Preserve all sources in the rule dialog but show the
        // best unconditional roll, plus any stronger conditional rolls with their marker.
        var best = labels.Where(n => !n.EndsWith('*')).Order().FirstOrDefault();
        var conditional = labels.Where(n => n.EndsWith('*') && (best == null || n[0] < best[0])).Order();
        return string.Join(" / ", (best == null ? Enumerable.Empty<string>() : [best]).Concat(conditional));
    }

    public static bool IsInvulnerable(string key) => UnitIdentity.Normalize(key) is "INSV" or "INVUL" or "INV" or "INVULNERABLE" or "INVULNERABLESAVE";
    public static bool IsFeelNoPain(string key) => UnitIdentity.Normalize(key) is "FNP" or "FEELNOPAIN";
    public static Dictionary<string, string> Standard(Dictionary<string, string>? characteristics)
        => (characteristics ?? []).Where(p => !IsInvulnerable(p.Key) && !IsFeelNoPain(p.Key)).ToDictionary(p => p.Key, p => p.Value);
    public static string Invulnerable(Dictionary<string, string>? characteristics)
    {
        var value = characteristics?.FirstOrDefault(p => IsInvulnerable(p.Key)).Value?.Trim() ?? "";
        return value is "" or "-" or "—" or "N/A" ? "" : value;
    }

    public static List<RuleDefinition> FeelNoPain(UnitProfile? profile)
    {
        List<RuleDefinition> result = [];
        void Add(string value, string description, bool restricted = false)
        {
            var match = Regex.Match(value, @"(?<!\d)([2-6])\+{1,3}");
            if (!match.Success) return;
            var suffix = restricted || value.Contains('*') || value[(match.Index + match.Length)..].Trim(' ', ')', ']').Length > 0 ? "*" : "";
            var label = match.Groups[1].Value + "+++ FNP" + suffix;
            result.Add(new(label, description));
        }
        foreach (var stat in profile?.Characteristics ?? [])
            if (IsFeelNoPain(stat.Key)) Add(stat.Value, "Feel No Pain: " + stat.Value);
        foreach (var ability in profile?.Abilities ?? [])
        {
            var name = Regex.Match(ability.Name, @"^(?:Feel No Pain|FNP)\s*(.*)$", RegexOptions.IgnoreCase);
            if (!name.Success) continue;
            var value = name.Groups[1].Value.Trim();
            // Generic definitions use X+; only a concrete imported value can become a stat.
            if (value.Length == 0 && Regex.IsMatch(ability.Text.Trim(), @"^[2-6]\+{1,3}\*?$")) value = ability.Text.Trim();
            foreach (var roll in Regex.Split(value, @"\s*/\s*"))
                Add(roll, ability.Name + "\n\n" + ability.Text, (profile?.Models?.Count ?? 0) > 1 || ability.Section == "Equipment rules");
        }
        return result.Distinct().ToList();
    }
}
