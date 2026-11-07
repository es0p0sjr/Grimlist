namespace GrimList.Core;

public static class WargearPricing
{
    public static IEnumerable<EquipmentOption> Options(UnitDefinition unit) => Loadouts.Definition(unit).UnitGroups
        .Concat(Loadouts.Definition(unit).Roles.SelectMany(r => r.Groups)).SelectMany(g => g.Options).DistinctBy(o => o.Id);
    public static bool MissingPrices(UnitDefinition unit) => unit.WargearCosts != null
        && Options(unit).Any(o => !unit.WargearCosts.Any(c => c.OptionId == o.Id));
    public static List<string> CoverageIssues(UnitDefinition unit) => Options(unit)
        .Where(o => unit.WargearCosts?.Any(c => c.OptionId == o.Id && (c.Points != null || c.Warning.Length > 0)) != true)
        .Select(o => $"{unit.Faction} / {unit.Name} / {o.Name}: equipment price or unresolved reason missing.").ToList();
    public static int? OptionCost(UnitDefinition unit, string optionId) => unit.WargearCosts == null ? 0 : unit.WargearCosts.FirstOrDefault(c => c.OptionId == optionId)?.Points;
    public static string Label(UnitDefinition unit, EquipmentOption option)
    {
        var points = OptionCost(unit, option.Id);
        return option.Name + (points == 0 ? "" : points == null ? " · points unavailable" : $" · +{points} pts");
    }
    public static int? Cost(UnitDefinition unit, ArmyEntry entry)
    {
        // A legacy paid-equipment warning must never become a free loadout after an app update.
        if (unit.WargearCosts == null && unit.Profile?.Loadout?.Warnings.Any(w => w.Contains("additional equipment points")) == true) return null;
        var loadout = entry.Loadout ?? Loadouts.Create(unit, entry.Models);
        // Never silently omit stale selections that the projection cannot resolve.
        var selected = Loadouts.Equipment(unit, loadout);
        if (selected.Sum(s => s.Count) != loadout.Equipment.Concat(loadout.Models.SelectMany(m => m.Equipment)).Sum(s => s.Count)) return null;
        var modelPricing = unit.ModelCosts?.Any(c => c.Points != 0) == true;
        if (loadout.Manual && modelPricing) return null;
        if ((selected.Any(s => OptionCost(unit, s.Option.Id) != 0) || modelPricing)
            && Loadouts.Issues(unit, entry with { Loadout = loadout }).Any(i => i != Loadouts.ReviewWarning)) return null;
        int? total = 0;
        foreach (var cost in unit.ModelCosts ?? []) total += cost.Points * loadout.Models.Count(m => m.RoleId == cost.OptionId);
        foreach (var selection in selected) total += OptionCost(unit, selection.Option.Id) * selection.Count;
        return total;
    }
}
