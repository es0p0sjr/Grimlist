using GrimList.Application;
using GrimList.Core;

internal static class TextExportChecks
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        // Use the actual bundled Rhino's standard/pintle groups, including their
        // shared option ID. Counts must come from selections, never modifier notes.
        var bundled = BootstrapRelease.ReadBundled().Dataset;
        var original = bundled.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Chaos Rhino");
        var role = original.Profile!.Loadout!.Roles.Single();
        var standard = role.Groups.Single(g => g.Name == "Wargear");
        var pintle = role.Groups.Single(g => g.Name == "Pintle weapon");
        EquipmentSelection Pick(EquipmentGroup group, string name) => new(group.Id, group.Options.Single(o => o.Name == name).Id);
        var equipment = new List<EquipmentSelection> { Pick(standard, "Armoured tracks"), Pick(standard, "Combi-bolter"), Pick(standard, "Havoc launcher") };
        var unit = original with { Options = [new(1, 75)], WargearCosts = role.Groups.SelectMany(g => g.Options)
            .DistinctBy(o => o.Id).Select(o => new EquipmentCost(o.Id, 0)).ToList() };
        var entry = new ArmyEntry("rhino", unit.Key, "Chaos Rhino (unit 1)", 1,
            Loadout: new([new(role.Id, [.. equipment, Pick(pintle, "Combi-bolter")])], []));
        var army = ArmyList.New(unit.Faction) with { Entries = [entry] };
        test("Text roster lists both selected Rhino bolters without effective-rule annotations", () =>
        {
            foreach (var condition in new[] { new EffectCondition("unknown"), new EffectCondition("constant", "1") })
            {
                var modified = unit with { Profile = unit.Profile! with { Semantics = new([], [
                    new("range-fixture", "Combi-bolter", "weapon", "Range", "set", "30\"", Profile: "Combi-bolter", Condition: condition)]) } };
                var data = new Dataset(bundled.Source, [modified]);
                var before = DataJson.Write(army);
                var effective = EffectiveDatasheets.Project(army, entry, data)!;
                Check(effective.Summary.Contains("Range: 30\"") && effective.Summary.Contains("Source: Combi-bolter"), "Fixture did not reproduce modifier notes");
                var text = RosterExport.Text(army, data);
                var expected = string.Join(Environment.NewLine, "Chaos Rhino (unit 1) (75 pts)", "  1 model",
                    "  1 × Armoured tracks", "  2 × Combi-bolter", "  1 × Havoc launcher");
                Check(text.Contains(expected), "Text does not match the requested concise Rhino block");
                Check(!text.Contains("Range: 30\"") && !text.Contains("Conditional:") && !text.Contains("Source: Combi-bolter"), "Modifier diagnostics leaked into text");
                Check(DataJson.Write(army) == before && EffectiveDatasheets.Project(army, entry, data)!.Summary == effective.Summary,
                    "Text export altered saved selections or detailed rules");
            }
        });
        test("Text weapon quantities follow the saved standard and optional selections", () =>
        {
            var data = new Dataset(bundled.Source, [unit]);
            foreach (var optional in new[] { "", "Combi-bolter", "Combi-weapon" })
            {
                var selected = entry with { Loadout = new([new(role.Id,
                    optional.Length == 0 ? equipment : [.. equipment, Pick(pintle, optional)])], []) };
                var text = RosterExport.Text(army with { Entries = [selected] }, data);
                Check(text.Contains($"  {(optional == "Combi-bolter" ? 2 : 1)} × Combi-bolter"), "Bolter count disagrees with saved selections");
                Check(text.Contains("  1 × Combi-weapon") == (optional == "Combi-weapon"), "Unselected optional weapon exported");
                Check(text.Split('\n').Count(line => line.TrimEnd().EndsWith("× Combi-bolter")) == 1, "Duplicate bolter rows");
            }
        });
        test("Text roster preserves explicitly counted manual weapons", () =>
        {
            var options = Loadouts.ManualOptions(unit.Profile!.Weapons!);
            var selected = entry with { Loadout = new([], options.Where(o => o.Name is "Combi-bolter" or "Armoured tracks" or "Havoc launcher")
                .Select(o => new EquipmentSelection("manual", o.Id, o.Name == "Combi-bolter" ? 2 : 1)).ToList(), Manual: true) };
            var text = RosterExport.Text(army with { Entries = [selected] }, new(bundled.Source, [unit]));
            Check(text.Contains("  2 × Combi-bolter") && text.Contains("  1 × Armoured tracks") && text.Contains("  1 × Havoc launcher"), "Manual counts changed");
            Check(!text.Contains("× Combi-weapon"), "Manual export invented unselected equipment");
        });
    }
}
