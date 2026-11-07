namespace GrimList.Core;

public static class UnitAvailability
{
    // Explicit unit metadata overrides faction defaults; an empty list means unrestricted.
    // Faction keywords distinguish allied daemons from native Death Guard daemon units.
    public static IReadOnlyList<string> RequiredDetachments(UnitDefinition unit, Dataset? data)
    {
        if (unit.RequiredDetachments != null) return unit.RequiredDetachments;
        var edition = unit.ProfileSource?.Edition is { Length: > 0 } known ? known : data?.Source.Edition ?? "";
        if (UnitIdentity.Normalize(unit.Faction) == "DEATHGUARD" && UnitIdentity.Normalize(edition) is "11" or "11TH"
            && unit.Profile?.Keywords?.Any(k => UnitIdentity.Normalize(k) is "FACTIONPLAGUELEGIONS" or "PLAGUELEGIONS") == true)
            return ["Tallyband Summoners"];
        return [];
    }

    public static string? Problem(ArmyList army, UnitDefinition unit, Dataset? data)
    {
        if (PricingRepair.IsReference(unit)) return $"{unit.Name}: reference for models created during battle; not a roster selection.";
        if (AlliedRules.Problem(army, unit, data) is { } alliance) return alliance;
        var required = RequiredDetachments(unit, data);
        return required.Count == 0 || required.Any(d => DetachmentRules.Contains(army, d, data)) ? null
            : $"{unit.Name}: requires {string.Join(" or ", required)}.";
    }
}
