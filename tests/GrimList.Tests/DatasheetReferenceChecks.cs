using GrimList.Core;

internal static class DatasheetReferenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("FNP badges use concrete source values, retain restrictions and ignore incidental or generic mentions", () =>
        {
            foreach (var name in new[] { "Feel No Pain5+", "Feel No Pain 5+", "FNP 5+" })
                Check(DatasheetStats.FeelNoPain(new(Abilities: [new(name, "Imported rule")])).Single().Name == "5+++ FNP", "FNP name variant lost");
            Check(DatasheetStats.FeelNoPain(new(Abilities: [new("Feel No Pain", "4+")])).Single().Name == "4+++ FNP", "FNP profile value lost");
            Check(DatasheetStats.FeelNoPain(new(Abilities: [new("Feel No Pain 3+*", "Imported rule")])).Single().Name == "3+++ FNP*", "Restricted FNP became unconditional");
            Check(DatasheetStats.FeelNoPain(new(Abilities: [new("Feel No Pain 4+ (against psychic attacks)", "Imported rule")])).Single().Name == "4+++ FNP*", "Qualified FNP lost its restriction");
            Check(DatasheetStats.FeelNoPain(new(Abilities: [new("Feel No Pain", "Roll X+"), new("Temporary protection", "Once per battle, gain Feel No Pain 4+.")])).Count == 0, "Conditional/generic text invented a permanent stat");
            var stats = new Dictionary<string, string> { ["T"] = "5", ["InSv"] = "4+", ["FNP"] = "5+" };
            Check(DatasheetStats.Standard(stats).Count == 1 && DatasheetStats.Invulnerable(stats) == "4+"
                && DatasheetStats.FeelNoPain(new(Characteristics: stats)).Single().Name == "5+++ FNP", "Separate defensive stats lost a source value");
        });
        test("Weapon references resolve parameterized keywords and remove only weapon definitions from unit abilities", () =>
        {
            var weapon = new WeaponProfile("Fixture gun", "Ranged", "24", "2", "3+", "5", "0", "1", "[Rapid Fire 2], Anti-Infantry 4+, Lethal Hits");
            var rules = new List<AbilityProfile> { new("Rapid Fire", "Rapid fixture"), new("Anti", "Anti fixture"), new("Lethal Hits", "Lethal fixture"), new("Leader", "Leader fixture"), new("Icon aura", "Equipment fixture", "Equipment rules") };
            var unit = new UnitDefinition("Test", "Unit", [new(1, 10)], Profile: new(Weapons: [weapon], Abilities: rules));
            Check(WeaponRuleReference.ForWeapon(weapon, unit, null).Select(r => r.Text).SequenceEqual(["Rapid fixture", "Anti fixture", "Lethal fixture"]), "Keyword values prevented lookup");
            Check(rules.Where(a => !WeaponRuleReference.IsWeaponDefinition(a, [weapon])).Select(a => a.Name).SequenceEqual(["Leader", "Icon aura"]), "Unit/equipment ability removed with keyword definitions");
        });
        test("Granted weapon keywords find same-edition definitions and unavailable definitions remain explicit", () =>
        {
            var source = new SourceInfo("Fixture", "1", "", "", Edition: "11");
            var unit = new UnitDefinition("Test", "Unit", [], Profile: new());
            var other = unit with { Name = "Other", Profile = new(Abilities: [new("Sustained Hits", "Current definition")]) };
            var old = other with { Name = "Old", ProfileSource = source with { Edition = "10" }, Profile = new(Abilities: [new("Sustained Hits", "Wrong edition")]) };
            var weapon = new WeaponProfile("Granted weapon", "Melee", "Melee", "1", "3+", "4", "0", "1", "Sustained Hits 1, Unknown keyword");
            var result = WeaponRuleReference.ForWeapon(weapon, unit, new(source, [unit, old, other]));
            Check(result[0].Text == "Current definition" && result[1].Text.Contains("not available"), "Lookup mixed editions or invented missing rules");
        });
    }
}
