using GrimList.Application;
using GrimList.Core;

internal static class ReferenceIndexChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Compiled attachment source retains live size conditions and isolates mutable results", () =>
        {
            var ability = new AbilityProfile("Size", "If this unit has a Starting Strength of 7 or more, or if a Character is leading this unit, then weapons equipped by models in this unit have the [LETHAL HITS] ability.");
            var unit = new UnitDefinition("Fixture", "Unit", [new(5, 100), new(10, 200)], Profile: new(Abilities: [ability]));
            var data = new Dataset(new("Fixture", "1", "", ""), [unit]);
            var small = ArmyList.New(unit.Faction) with { Entries = [new("unit", unit.Key, unit.Name, 5)] };
            var large = small with { Entries = [small.Entries[0] with { Models = 10 }] };
            Check(AttachedEffects.ForUnit(small, data, small.Entries[0]).Count == 0, "Small unit received grant");
            var effect = AttachedEffects.ForUnit(large, data, large.Entries[0]).Single();
            Check(effect.Adjustment!.Keywords.Single() == "LETHAL HITS", "Large unit missed grant");
            effect.Adjustment.Keywords.Clear();
            Check(AttachedEffects.ForUnit(large, data, large.Entries[0]).Single().Adjustment!.Keywords.Count == 1
                && AttachedEffects.ForUnit(small, data, small.Entries[0]).Count == 0, "Mutable result or size decision leaked into cache");
            var revised = ability with { Text = ability.Text.Replace("7 or more", "12 or more") };
            Check(AttachedEffects.CompileSource(revised).MinimumSize == 12 && AttachedEffects.CompileSource(ability).MinimumSize == 7, "Changed ability reused old condition");
        });
        test("Compiled attachment interpretations match uncached parsing across bundled abilities", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            foreach (var ability in data.Units.SelectMany(u => u.Profile?.Abilities ?? []).DistinctBy(a => (a.Name, a.Text)))
                Check(DataJson.Write(AttachedEffects.CompileSource(ability)) == DataJson.Write(AttachedEffects.ParseSource(ability)), "Attachment interpretation changed: " + ability.Name);
        });
        test("Indexed weapon definitions match the uncached oracle across all bundled factions", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset; var count = 0;
            foreach (var unit in data.Units.GroupBy(u => u.Faction).SelectMany(g => g.Take(3)))
                foreach (var weapon in (unit.Profile?.Weapons ?? []).Where(w => w.Keywords.Length > 0).Take(2))
                {
                    Check(DataJson.Write(WeaponRuleReference.ForWeapon(weapon, unit, data)) == DataJson.Write(WeaponRuleReference.ForWeaponUncached(weapon, unit, data)), "Changed reference: " + unit.Name);
                    count++;
                }
            Check(count > 100, "Insufficient definition coverage");
        });
        test("Definition indexes preserve local priority, empty text, normalized aliases and edition boundaries", () =>
        {
            var weapon = new WeaponProfile("Test", "Melee", "", "1", "2+", "4", "0", "1", "Psychic, Sustained Hits 2, Anti-Infantry 4+, Missing");
            var source = new SourceInfo("Fixture", "1", "", "", Edition: "11");
            var unit = new UnitDefinition("Fixture", "Unit", [], Profile: new(Abilities: [new("Psychic", " "), new("Sustained Hits X", " First "), new("Sustained Hits 1", "First"), new("Sustained Hits", "Second")]), ProfileSource: source);
            var other = unit with { Name = "Other", Profile = new(Abilities: [new("Psychic", "Do not override empty local"), new("Anti-Vehicle 3+", "Correct edition")]) };
            var wrong = other with { ProfileSource = source with { Edition = "10" }, Profile = new(Abilities: [new("Anti", "Wrong edition")]) };
            var data = new Dataset(source, [unit, other, wrong]);
            foreach (var variant in new[] { data, data with { SharedAbilities = new Dictionary<string, List<AbilityProfile>>() { ["11"] = other.Profile!.Abilities! } } })
            {
                var expected = WeaponRuleReference.ForWeaponUncached(weapon, unit, variant);
                var indexed = WeaponRuleReference.ForWeapon(weapon, unit, variant);
                Check(DataJson.Write(indexed) == DataJson.Write(expected), "Priority/order/edition differs");
                Check(indexed[0].Text.Contains("not available") && indexed[1].Text == "First\n\nSecond" && indexed[2].Text == "Correct edition", "Fixture failed to exercise boundaries");
            }
            Check(DataJson.Write(WeaponRuleReference.ForWeapon(weapon, null, null)) == DataJson.Write(WeaponRuleReference.ForWeaponUncached(weapon, null, null)), "Null context differs");
        });
        test("Definition indexes reject stale same-name revisions and support concurrent readers", () =>
        {
            var weapon = new WeaponProfile("Test", "Melee", "", "1", "2+", "4", "0", "1", "Psychic");
            var source = new SourceInfo("Fixture", "same", "", "", Edition: "11");
            var data = new Dataset(source, []) { SharedAbilities = new Dictionary<string, List<AbilityProfile>>() { ["11"] = [new("Psychic", "Old")] } };
            var updated = data with { SharedAbilities = new Dictionary<string, List<AbilityProfile>>() { ["11"] = [new("Psychic", "New")] } };
            Parallel.For(0, 32, _ =>
            {
                Check(WeaponRuleReference.ForWeapon(weapon, null, data).Single().Text == "Old", "Old revision changed");
                Check(WeaponRuleReference.ForWeapon(weapon, null, updated).Single().Text == "New", "Updated import used stale index");
            });
            var unit = new UnitDefinition("Fixture", "Local", [], Profile: new(Abilities: [new("Psychic", "Local old")]));
            var revised = unit with { Profile = unit.Profile! with { Abilities = [new("Psychic", "Local new")] } };
            Check(WeaponRuleReference.ForWeapon(weapon, unit, data).Single().Text == "Local old"
                && WeaponRuleReference.ForWeapon(weapon, revised, data).Single().Text == "Local new", "Copied local profile retained stale definitions");
        });
    }
}
