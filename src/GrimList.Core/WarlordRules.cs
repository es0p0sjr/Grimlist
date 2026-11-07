namespace GrimList.Core;

public static partial class WarlordRules
{
    private static bool Mandatory(UnitDefinition? unit) => unit?.Profile?.MustBeWarlord == true
        || unit?.Profile?.Abilities?.Any(a => UnitIdentity.Normalize(a.Name) == "SUPREMECOMMANDER") == true;
    public static bool Required(UnitDefinition? unit) => Mandatory(unit) && (unit == null || !UnitRestrictions(unit).Any(r => r.Condition == null));
    public static bool IsCharacter(UnitDefinition? unit) => unit != null && (unit.DisplayCategory is "Epic Heroes" or "Characters"
        || unit.Profile?.Keywords?.Any(k => UnitIdentity.Normalize(k) == "CHARACTER") == true);
    public static bool Eligible(UnitDefinition? unit) => unit != null && !UnitRestrictions(unit).Any(r => r.Condition == null) && (Mandatory(unit) || IsCharacter(unit));
    public static List<ArmyEntry> Candidates(ArmyList army, Dataset? data)
    {
        if (data == null || data.Game != army.Game) return [];
        var eligible = army.Entries.Select(e => (Entry: e, Eligibility: Evaluate(army, e, data))).Where(e => e.Eligibility.Eligible).ToList();
        var required = eligible.Where(e => e.Eligibility.MustBeWarlord).ToList();
        return (required.Count > 0 ? required : eligible).Select(e => e.Entry).ToList();
    }
    public static ArmyList Normalize(ArmyList army, Dataset? data)
    {
        var candidates = Candidates(army, data);
        var mandatory = candidates.Where(e => Required(army, e, data)).ToList();
        var id = mandatory.Count == 1 ? mandatory[0].Id
            : candidates.Any(e => e.Id == army.WarlordEntryId) ? army.WarlordEntryId : "";
        return id == army.WarlordEntryId ? army : army with { WarlordEntryId = id };
    }
    public static ArmyList Select(ArmyList army, Dataset? data, string entryId)
    {
        var candidates = Candidates(army, data);
        if (entryId.Length == 0 && !candidates.Any(e => Required(army, e, data))) return army with { WarlordEntryId = "" };
        if (!candidates.Any(e => e.Id == entryId))
        {
            var entry = army.Entries.FirstOrDefault(e => e.Id == entryId);
            var reasons = entry == null ? [] : Evaluate(army, entry, data).Reasons;
            throw new InvalidDataException(reasons.Count > 0 ? entry!.Name + ": " + string.Join("\n", reasons)
                : "Choose an eligible character. An eligible mandatory warlord takes precedence.");
        }
        return Normalize(army with { WarlordEntryId = entryId }, data);
    }
    public static List<string> Issues(ArmyList army, Dataset? data)
    {
        var candidates = Candidates(army, data);
        if (army.WarlordEntryId.Length > 0 && army.Entries.Count(e => e.Id == army.WarlordEntryId) == 1
            && candidates.Any(e => e.Id == army.WarlordEntryId)) return [];
        if (army.WarlordEntryId.Length > 0 && army.Entries.Count(e => e.Id == army.WarlordEntryId) > 1)
            return ["Exactly one warlord is required; multiple roster entries share the selected warlord ID."];
        if (army.Entries.FirstOrDefault(e => e.Id == army.WarlordEntryId) is { } selected && Evaluate(army, selected, data).Reasons is { Count: > 0 } reasons)
            return reasons.Select(r => selected.Name + ": " + r).ToList();
        var issues = new List<string> { candidates.Count > 1 && candidates.All(e => Required(army, e, data))
            ? "Multiple mandatory warlords: choose one of them as your warlord."
            : "Select exactly one eligible warlord; import datasheets if eligibility is unknown." };
        if (candidates.Count == 0)
            issues.AddRange(army.Entries.Select(e => (Entry: e, Reason: RestrictionReason(army, e, data)))
                .Where(e => e.Reason.Length > 0).Select(e => e.Entry.Name + ": " + e.Reason));
        return issues;
    }
}
