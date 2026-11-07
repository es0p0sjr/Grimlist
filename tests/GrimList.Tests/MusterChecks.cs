using GrimList.Core;

internal static class MusterChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static void Run(Action<string, Action> test)
    {
        var source = new SourceInfo("Muster fixtures", "1", "", "", Edition: "11");
        UnitDefinition Unit(string name, params string[] keywords) => new("Space Marines", name, [new(1, 10)], Profile: new(Keywords: keywords.ToList()));
        var ordinary = Unit("Infantry", "Infantry"); var battleline = Unit("Troops", "Battleline");
        var dedicated = Unit("Carrier", "Transport", "Dedicated Transport"); var transport = Unit("Tank", "Vehicle", "Transport");
        var hero = Unit("Hero", "Epic Hero");
        var data = new Dataset(source, [ordinary, battleline, dedicated, transport, hero]);
        ArmyList Army(UnitDefinition u, int count) => ArmyList.New(u.Faction) with { Entries = Enumerable.Range(0, count).Select(i => new ArmyEntry(i.ToString(), u.Key, u.Name, 1)).ToList() };
        test("Muster enforces 3/6 at 2000, 2/4 at 1000, one Epic Hero and narrower imported limits", () =>
        {
            foreach (var (unit, limit) in new[] { (ordinary, 3), (battleline, 6), (dedicated, 6), (transport, 3), (hero, 1) })
            {
                var full = Army(unit, limit); var next = full with { Entries = [.. full.Entries, new("extra", unit.Key, unit.Name, 1)] };
                Check(MusterRules.Limit(full, unit, data) == limit && MusterRules.AddProblem(full, data, unit) != null, "Wrong add limit for " + unit.Name);
                Check(MusterRules.AddProblem(Army(unit, limit - 1), data, unit) == null && MusterRules.Issues(full, data).Count == 0, "Final legal slot rejected");
                try { MusterRules.ValidateIncrease(full, next, data); throw new Exception("Illegal increase accepted"); } catch (InvalidDataException) { }
                Check(MusterRules.Issues(next, data).Count == 1, "Restored over-limit army not flagged");
                Check(MusterRules.Limit(full with { Limit = 1000 }, unit, data) == (unit == hero ? 1 : limit == 6 ? 4 : 2), "Wrong Incursion limit");
            }
            Check(MusterRules.Limit(Army(battleline, 1), battleline with { MaxCopies = 1 }, data) == 1, "Specific lower source cap ignored");
            Check(MusterRules.Limit(Army(ordinary, 1) with { Limit = 1000 }, ordinary, data with { Source = source with { Edition = "10" } }) == 3, "11e scaling leaked into 10e");
        });
        test("Detachment keywords update category and limits without altering profiles; removing the rule preserves excess units for correction", () =>
        {
            var changed = ordinary with { Profile = ordinary.Profile! with { DetachmentKeywords = [new("Battleline", false, new("or", Children:
                [new("count", "First", "atLeast", 1), new("count", "Second", "atLeast", 1)]))] } };
            var d = data with { Units = [changed] }; var on = Army(changed, 6) with { Detachments = ["Second"] }; var off = on with { Detachments = [] };
            Check(MusterRules.Category(on, changed, d) == "Battleline" && MusterRules.Limit(on, changed, d) == 6, "Detachment benefit missing");
            Check(MusterRules.Category(off, changed, d) == "Others" && MusterRules.Limit(off, changed, d) == 3, "Stale detachment benefit retained");
            MusterRules.ValidateIncrease(on, off, d);
            Check(MusterRules.Issues(off, d).Count == 1 && off.Entries.Count == 6, "Detachment change discarded selections or failed to warn");
            Check(!changed.Profile!.Keywords!.Contains("Battleline"), "Shared datasheet was permanently mutated");
            Check(RosterExport.Text(on, d).Contains("BATTLELINE"), "Export did not follow effective category");
        });
        test("Muster copies resolve aliases and datasheet variants, independently of pricing copy categories", () =>
        {
            var renamed = ordinary with { Id = "opaque-id", Aliases = ["old-id"], CopyCategory = "Infantry" };
            var d = data with { Units = [renamed, transport] };
            var army = Army(renamed, 2) with { Entries = [new("1", "old-id", "Old name", 1), new("2", renamed.Key, renamed.Name, 1), new("3", transport.Key, transport.Name, 1)] };
            Check(MusterRules.Count(army, d, renamed) == 2, "Copies counted pricing categories or missed aliases");
            var variant = ordinary with { Name = "Infantry (variant aaaa-bbbb-cccc-dddd)" };
            Check(MusterRules.Count(army with { Entries = [.. army.Entries, new("4", variant.Key, variant.Name, 1)] }, d with { Units = [renamed, variant, transport] }, renamed) == 3, "Variant label evaded datasheet limit");
        });
        test("Older imported detachment text grants Battleline generically but as-if effects and allies do not", () =>
        {
            var card = new ReferenceCard("Space Marines", "Detachments", "Rule", "KEYWORDS\nInfantry units from your army gain the BATTLELINE keyword.", "Example");
            var d = data with { ReferenceCards = [card] }; var army = Army(ordinary, 1) with { Detachments = ["Example"] };
            Check(MusterRules.Category(army, ordinary, d) == "Battleline", "Generic reference fallback failed");
            Check(MusterRules.Category(army, ordinary, d with { ReferenceCards = [card with { Text = "Infantry units can act as if they had the BATTLELINE keyword." }] }) == "Others", "As-if rule granted permanent keyword");
            Check(!MusterRules.Keywords(army, ordinary with { Faction = "Imperial Agents" }, d).Contains("Battleline"), "Host detachment upgraded an ally");
        });
    }
}
