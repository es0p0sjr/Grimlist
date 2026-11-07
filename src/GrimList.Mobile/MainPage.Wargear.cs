using GrimList.Core;
using Microsoft.Maui.Controls;

namespace GrimList.Mobile;

public sealed partial class MainPage
{
    private readonly HashSet<string> collapsedWargear = [];
    private Dictionary<string, List<string>> rosterWargear = [];

    // Presentation only: selected choices, defaults and prices all come from Core.
    private static List<string> WargearBadges(UnitDefinition unit, ArmyEntry entry)
    {
        var loadout = entry.Loadout ?? Loadouts.Create(unit, entry.Models);
        var defaults = Loadouts.Equipment(unit, Loadouts.Create(unit, entry.Models))
            .GroupBy(e => e.Option.Id).ToDictionary(g => g.Key, g => g.Sum(e => e.Count));
        List<string> badges = [];
        foreach (var selected in Loadouts.Equipment(unit, loadout).GroupBy(e => e.Option.Id))
        {
            var count = selected.Sum(e => e.Count);
            var cost = WargearPricing.OptionCost(unit, selected.Key);
            if (cost == 0 && count <= defaults.GetValueOrDefault(selected.Key)) continue;
            var label = $"{count} × {selected.First().Option.Name}";
            badges.Add(label + (cost == null ? "\nPoints unavailable" : cost == 0 ? "" : $"\n{cost * count} pts"));
        }
        foreach (var cost in unit.ModelCosts ?? [])
        {
            var count = loadout.Models.Count(m => m.RoleId == cost.OptionId);
            if (count == 0 || cost.Points == 0) continue;
            var role = Loadouts.Definition(unit).Roles.FirstOrDefault(r => r.Id == cost.OptionId);
            badges.Add($"{count} × {role?.Name ?? "Model upgrade"}\n" + (cost.Points == null ? "Points unavailable" : $"{cost.Points * count} pts"));
        }
        return badges;
    }
}
