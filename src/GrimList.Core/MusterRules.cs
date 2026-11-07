using System.Text.RegularExpressions;

namespace GrimList.Core;

public record DetachmentCondition(string Kind, string Detachment = "", string Comparison = "", int Value = 0, List<DetachmentCondition>? Children = null);
public record DetachmentKeywordRule(string Keyword, bool Remove, DetachmentCondition Condition);

public static class MusterRules
{
    public static List<string> Keywords(ArmyList army, UnitDefinition unit, Dataset? data = null)
    {
        var keywords = unit.Profile?.Keywords is { Count: > 0 } imported ? imported.ToList() : [unit.Category];
        bool Test(DetachmentCondition condition) => RuleEvaluation.Test(condition, c => c.Kind, c => c.Children ?? [],
            c => RuleEvaluation.Compare(DetachmentRules.Contains(army, c.Detachment, data) ? 1 : 0, c.Comparison, c.Value));
        // Detachment benefits belong to the owning faction, not allied units with similar names.
        if (!AlliedRules.IsAlly(army, unit))
        {
            foreach (var rule in unit.Profile?.DetachmentKeywords ?? [])
            {
                if (!Test(rule.Condition)) continue;
                keywords.RemoveAll(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(rule.Keyword));
                if (!rule.Remove) keywords.Add(rule.Keyword);
            }
            // Older libraries already retain detachment reference text. Recognise only an explicit,
            // unconditional keyword grant; "as if Battleline" and battle-created units do not qualify.
            if (unit.Profile?.DetachmentKeywords == null && data != null)
                foreach (var card in (data.ReferenceCards ?? []).Where(c => c.Faction == army.Faction && c.Detachment.Length > 0
                    && DetachmentRules.Contains(army, c.Detachment, data) && c.Section is "Detachments" or "Detachment Rules"
                    && (c.Applicability == null || DetachmentRules.Applicable(army, new(c.Faction, "", Applicability: c.Applicability), data))))
                foreach (Match match in Regex.Matches(card.Text.Replace("*", "").Replace("^^", "").Replace("__", ""),
                    @"(?:^|[.\r\n])\s*[-•]?\s*(?:KEYWORDS\s*:?\s*)?(?<target>[\p{L}\p{N} ’'\-]+?) units? from your army (?:gains?|have|has) the (?<keyword>[\p{L}\p{N} -]+?) keyword\b", RegexOptions.IgnoreCase))
                    if (Regex.Split(match.Groups["target"].Value, @"\s+(?:and|or)\s+", RegexOptions.IgnoreCase)
                        .Any(target => UnitIdentity.Normalize(target) == UnitIdentity.Normalize(unit.Name) || keywords.Any(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(target))))
                        keywords.Add(match.Groups["keyword"].Value);
        }
        return keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static bool Has(ArmyList army, UnitDefinition unit, string keyword, Dataset? data = null) => Keywords(army, unit, data)
        .Any(k => UnitIdentity.Normalize(k) == UnitIdentity.Normalize(keyword));
    public static string Category(ArmyList army, UnitDefinition unit, Dataset? data = null) => AlliedRules.IsAlly(army, unit) ? "Allies"
        : UnitCategories.FromKeywords(Keywords(army, unit, data));
    public static int Limit(ArmyList army, UnitDefinition unit, Dataset? data)
    {
        var category = UnitCategories.FromKeywords(Keywords(army, unit, data));
        var epic = Has(army, unit, "Epic Hero", data) || category == "Epic Heroes";
        var doubled = Has(army, unit, "Battleline", data) || Has(army, unit, "Dedicated Transport", data)
            || category == "Dedicated Transports";
        var edition = unit.ProfileSource?.Edition is { Length: > 0 } value ? value : data?.Source.Edition;
        // Standard 11e Incursion has 2/4. Custom and larger limits retain the 3/6 ceiling;
        // no extra slots are inferred merely from a larger points allowance.
        var normal = edition == "11" && army.Limit == 1000 ? 2 : 3;
        return Math.Min(epic ? 1 : normal * (doubled ? 2 : 1), unit.MaxCopies ?? int.MaxValue);
    }
    private static string Identity(UnitDefinition unit) => UnitIdentity.Normalize(AlliedRules.OriginFaction(unit)) + "|"
        + UnitIdentity.Normalize(Regex.Replace(unit.Name, @" \(variant [0-9a-f-]+\)$", "", RegexOptions.IgnoreCase));
    public static int Count(ArmyList army, Dataset? data, UnitDefinition unit) => army.Entries.Count(e =>
        data?.FindUnit(e.UnitKey) is { } found ? found.Key == unit.Key || Identity(found) == Identity(unit) : e.UnitKey == unit.Key);
    public static string? AddProblem(ArmyList army, Dataset? data, UnitDefinition unit) => Count(army, data, unit) >= Limit(army, unit, data)
        ? $"{unit.Name}: unit limit reached ({Count(army, data, unit)}/{Limit(army, unit, data)})." : null;
    public static List<string> Issues(ArmyList army, Dataset? data) => army.Entries.Select(e => MusterEffects.EffectiveUnit(army, e, data)).OfType<UnitDefinition>()
        .DistinctBy(Identity).Where(u => Count(army, data, u) > Limit(army, u, data))
        .Select(u => $"{u.Name}: {Count(army, data, u)} units exceeds the limit of {Limit(army, u, data)}.").ToList();
    public static void ValidateIncrease(ArmyList previous, ArmyList proposed, Dataset? data)
    {
        foreach (var unit in proposed.Entries.Select(e => MusterEffects.EffectiveUnit(proposed, e, data)).OfType<UnitDefinition>().DistinctBy(Identity))
            if (Count(proposed, data, unit) > Count(previous, data, unit) && Count(proposed, data, unit) > Limit(proposed, unit, data))
                throw new InvalidDataException($"{unit.Name}: cannot add more than {Limit(proposed, unit, data)} units with the current army rules.");
    }
}
