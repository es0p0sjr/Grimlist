using GrimList.Core;

internal static class EnhancementDefenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string[] args, string root)
    {
        var source = new SourceInfo("Defence fixture", "1", "", "", Edition: "11");
        var unit = new UnitDefinition("Test", "Character", [new(1, 100)], Profile: new(Keywords: ["Character"]));
        var card = new ReferenceCard("Test", "Enhancements", "Protection", "TEST model only. The bearer has the Feel No Pain 5+ ability.", "Detachment", "20 pts");
        var data = new Dataset(source, [unit], ReferenceCards: [card]);
        var army = ArmyList.New("Test") with { Detachments = [card.Detachment], Entries = [new("bearer", unit.Key, unit.Name, 1), new("other", unit.Key, unit.Name, 1)] };
        test("Selected enhancements supply bearer FNP without changing base data or affecting another unit", () =>
        {
            var selected = Enhancements.Select(army, data, "bearer", new(card.Detachment, card.Name));
            var rules = DatasheetStats.FeelNoPain(selected, data, unit, selected.Entries[0]);
            Check(DatasheetStats.FeelNoPainLabel(rules) == "5+++ FNP" && rules.Single().Text.Contains(card.Name), "Selected source grant not displayed or attributed");
            Check(DatasheetStats.FeelNoPain(selected, data, unit, selected.Entries[1]).Count == 0 && DatasheetStats.FeelNoPain(unit.Profile).Count == 0, "Enhancement leaked to another entry or base datasheet");
            var attached = selected with { Entries = [selected.Entries[0] with { AttachedToId = "other" }, selected.Entries[1]] };
            Check(DatasheetStats.FeelNoPain(attached, data, unit, attached.Entries[1]).Count == 0, "Bearer-only grant leaked to bodyguard");
            var removed = Enhancements.Select(selected, data, "bearer", null);
            Check(DatasheetStats.FeelNoPain(removed, data, unit, removed.Entries[0]).Count == 0, "Removing enhancement left FNP behind");
            Check(DatasheetStats.FeelNoPain(selected with { Detachments = [] }, data, unit, selected.Entries[0]).Count == 0, "Ineligible enhancement still granted FNP");
            Check(DatasheetStats.FeelNoPain(selected, data with { ReferenceCards = [] }, unit, selected.Entries[0]).Count == 0, "Missing source invented an enhancement effect");
        });
        test("Generic FNP enhancement wording supports combined stats and preserves conditional scope", () =>
        {
            foreach (var text in new[] { "The bearer has the Feel No Pain 5+ ability.", "The bearer has the\u00a0Feel No Pain 5+ ability.",
                "This model has Feel No Pain 5+.", "The bearer gains the Feel No Pain 5+ ability.",
                "The bearer has a Save characteristic of 2+ and the Feel No Pain 5+ ability.",
                "Add 1 to the bearer’s Wounds characteristic and the bearer has the Feel No Pain 5+ ability.",
                "The bearer has the Feel No Pain 5+ ability and a Wounds characteristic of 5." })
                Check(EnhancementDefences.FeelNoPain(card with { Text = text }).Single().Name == "5+++ FNP", "Unrecognised direct grant: " + text);
            foreach (var text in new[] { "While the bearer is on an objective, the bearer has the Feel No Pain 4+ ability.",
                "The bearer has the Feel No Pain 4+ ability against mortal wounds.", "Once per battle, the bearer gains the Feel No Pain 4+ ability until the end of the phase." })
                Check(EnhancementDefences.FeelNoPain(card with { Text = text }).Single().Name == "4+++ FNP*", "Conditional grant became unconditional");
            foreach (var text in new[] { "Models in a friendly unit within 6 inches of the bearer have the Feel No Pain 5+ ability.",
                "Select a friendly unit. That unit has the Feel No Pain 5+ ability.", "The bearer does not have the Feel No Pain 5+ ability.",
                "This ability takes the form Feel No Pain X+." })
                Check(EnhancementDefences.FeelNoPain(card with { Text = text }).Count == 0, "A definition or another unit's grant became a bearer stat");
        });
        test("FNP shows the best unconditional save while preserving source rules and stronger conditional saves", () =>
        {
            var innate = unit with { Profile = unit.Profile! with { Abilities = [new("Feel No Pain 6+", "Innate rule")] } };
            var selected = Enhancements.Select(army, data, "bearer", new(card.Detachment, card.Name));
            var rules = DatasheetStats.FeelNoPain(selected, data with { Units = [innate] }, innate, selected.Entries[0]);
            Check(rules.Count == 2 && DatasheetStats.FeelNoPainLabel(rules) == "5+++ FNP", "Multiple sources stacked or discarded source detail");
            var removed = Enhancements.Select(selected, data, "bearer", null);
            Check(DatasheetStats.FeelNoPainLabel(DatasheetStats.FeelNoPain(removed, data, innate, removed.Entries[0])) == "6+++ FNP", "Removing enhancement lost innate FNP");
            Check(DatasheetStats.FeelNoPainLabel([new("5+++ FNP", "base"), new("4+++ FNP*", "conditional")]) == "5+++ FNP / 4+++ FNP*", "Conditional save replaced the always-active save");
        });
        var sourceIndex = Array.IndexOf(args, "--enhancement-defence-source");
        if (sourceIndex < 0 || sourceIndex + 1 >= args.Length) return;
        test("Installed source grants FNP to Daemon Prince and a second faction's character generically", () =>
        {
            // Use the installed library snapshot, including the existing supplemental eligibility
            // cards retained by DatasetMerge where RAW catalogues omit readable restrictions.
            var installed = DataJson.Read<Dataset>(File.ReadAllText(args[sourceIndex + 1]));
            var prince = installed.Units.Single(u => u.Faction == "Death Guard" && u.Name == "Daemon Prince of Nurgle");
            var captain = installed.Units.Single(u => u.Faction == "Adeptus Custodes" && u.Name == "Shield-Captain");
            foreach (var (bearer, enhancement) in new[] { (prince, "Revolting Regeneration"), (captain, "Augury Uplink") })
            {
                var actualCard = installed.ReferenceCards!.Single(c => c.Faction == bearer.Faction && c.Section == "Enhancements" && c.Name == enhancement);
                var actualArmy = ArmyList.New(bearer.Faction) with { Detachments = [actualCard.Detachment], Entries = [new("bearer", bearer.Key, bearer.Name, 1)] };
                var selected = Enhancements.Select(actualArmy, installed, "bearer", new(actualCard.Detachment, actualCard.Name));
                var rules = DatasheetStats.FeelNoPain(selected, installed, bearer, selected.Entries.Single());
                Check(DatasheetStats.FeelNoPainLabel(rules) == "5+++ FNP" && rules.Any(r => r.Text.Contains(enhancement)), "Actual enhancement did not supply FNP for " + bearer.Name);
            }
            File.WriteAllText(Path.Combine(root, "enhancement-defence-fixture.json"), DataJson.Write(installed with { Units = [prince, captain] }));
        });
    }
}
