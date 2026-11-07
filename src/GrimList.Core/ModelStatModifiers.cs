namespace GrimList.Core;

// The selector is retained verbatim in old imports. Repair its interpretation,
// never infer recipients from a rule name or a numeric characteristic alone.
public static class ModelStatModifiers
{
    public const string UnitGroupSelector = "self.entries.group.recursive.profiles.Unit";
    public static DatasheetModifier Normalize(DatasheetModifier modifier) =>
        modifier.Target == "weapon" && modifier.WeaponPredicate is { Kind: "unknown", Value: UnitGroupSelector }
            && EffectiveDatasheets.Stat(modifier.Characteristic) is "M" or "T" or "Sv" or "W" or "LD" or "OC" or "InSv"
        ? modifier with { Target = "model", WeaponPredicate = null, Recipient = new("attached-unit", SourceSelector: UnitGroupSelector) }
        : modifier;

    public static bool Represents(DatasheetModifier stored, DatasheetModifier parsed) =>
        EffectiveDatasheets.Stat(stored.Characteristic) == EffectiveDatasheets.Stat(parsed.Characteristic)
        && (stored.Target == "weapon" && parsed.Target == "model" && stored.WeaponPredicate?.Kind == "unknown"
        || stored.Target == parsed.Target
        && stored.WeaponType == parsed.WeaponType && (stored.Recipient?.Scope ?? "self") == (parsed.Recipient?.Scope ?? "self")
        && (parsed.Characteristic != "Keywords" || stored.Value == parsed.Value && stored.Operation == parsed.Operation));

    public static Dataset Repair(Dataset data)
    {
        var changed = false;
        List<DatasheetModifier>? RepairList(List<DatasheetModifier>? modifiers)
        {
            if (modifiers == null || !modifiers.Any(m => !ReferenceEquals(m, Normalize(m)))) return modifiers;
            var repaired = modifiers.Select(Normalize).ToList();
            if (repaired.SequenceEqual(modifiers)) return modifiers;
            changed = true; return repaired;
        }
        var cards = data.ReferenceCards?.Select(c => {
            if (!DetachmentLifecycle.BsData(c.Source ?? data.Source)) return c;
            var modifiers = RepairList(c.Modifiers);
            return ReferenceEquals(modifiers, c.Modifiers) ? c : c with { Modifiers = modifiers };
        }).ToList();
        var units = data.Units.Select(u => {
            if (!DetachmentLifecycle.BsData(u.ProfileSource ?? data.Source) || u.Profile?.Semantics is not { } semantics) return u;
            var modifiers = RepairList(semantics.Modifiers)!;
            return ReferenceEquals(modifiers, semantics.Modifiers) ? u : u with { Profile = u.Profile with { Semantics = semantics with { Modifiers = modifiers } } };
        }).ToList();
        return changed ? data with { ReferenceCards = cards, Units = units } : data;
    }
}
