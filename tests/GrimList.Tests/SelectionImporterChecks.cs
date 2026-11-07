using System.Text.Json.Nodes;
using GrimList.Core;

internal static partial class PricingPatternChecks
{
    public static void RunSelectionSemantics(Action<string, Action> test, string directory, string[] args)
    {
        test("Numeric rules preserve order, boolean groups, repeated SET and MULTIPLY semantics", () =>
        {
            var yes = new SelectionTest();
            NumericModifier Mod(string op, int value, int order, params NumericRepeat[] repeats) => new(op, value, yes, repeats.ToList(), order);
            Check(RuleEvaluation.Effective(10, [Mod("set", 20, 0), Mod("multiply", 3, 1)], _ => 0) == 60, "SET/MULTIPLY order lost");
            Check(RuleEvaluation.Effective(10, [Mod("multiply", 3, 0), Mod("set", 20, 1)], _ => 0) == 20, "MULTIPLY/SET order lost");
            var repeat = new NumericRepeat(new("unit", "model"), 2);
            Check(RuleEvaluation.Effective(10, [Mod("multiply", 2, 0, repeat)], _ => 6) == 80, "Repeated multiplication treated as operand scaling");
            Check(RuleEvaluation.Effective(10, [Mod("set", 7, 0, repeat)], _ => 6) == 7, "Repeated SET multiplied its operand");
            Check(RuleEvaluation.Effective(10, [Mod("increment", 2, 0, repeat, repeat)], _ => 6) == 22, "Repeats were not additive");
            var or = new SelectionTest("or", Children: [new("query", new("unit", "a"), "atLeast", 2), new("query", new("unit", "b"), "equalTo", 1)]);
            Check(RuleEvaluation.Test(or, q => q.ChildId == "b" ? 1 : 0), "OR query failed");
            Check(!RuleEvaluation.Test(or with { Kind = "and" }, q => q.ChildId == "b" ? 1 : 0), "AND query failed");
        });
        test("Importer resolves root entry-link scope IDs without guessing dangling references", () =>
        {
            var squad = Squad(Role("body", 5, 10)); squad["modifiers"] = new JsonArray(Price(scope: "abcd-abcd-abcd-abcd"));
            var root = new JsonObject { ["id"] = "catalogue", ["name"] = "Chaos - Death Guard", ["gameSystemId"] = "game",
                ["sharedSelectionEntries"] = new JsonArray(squad),
                ["entryLinks"] = Array("""[{"id":"abcd-abcd-abcd-abcd","targetId":"squad","type":"selectionEntry","name":"Renamed example"}]""") };
            UnitDefinition Read() => BsDataImporter.ImportAll([new("fixture.json", root)], new("Fixture", "test", "", "", Edition: "11")).Dataset.Units.Single();
            var unit = Read(); Check(unit.Options.Select(o => o.Points).SequenceEqual(new[] { 50, 100 }), "Valid entry-link scope rejected");
            squad["modifiers"]![0]!["conditions"]![0]!["scope"] = "dead-beef-abcd-1234";
            var broken = Read();
            Check(broken.Options.Count == 0 && broken.ImportStatus == ImportStatus.SourceDataError, "Dangling scope invented a price or lost its classification");
            Check(broken.Diagnostics!.Single().ReferenceId == "dead-beef-abcd-1234" && broken.Diagnostics![0].RuleId.Length > 0
                && broken.Diagnostics![0].Revision == "test", "Source reference provenance missing");
        });
        test("Unmapped paid options retain source cost, identity and selectable limits", () =>
        {
            var purchase = Role("unmapped-item", 0, 1, "upgrade");
            purchase["costs"] = Array("""[{"name":"pts","typeId":"points","value":7}]""");
            var body = Role("body", 1, 1); body["selectionEntries"] = new JsonArray(purchase);
            var u = Import(Squad(body));
            var entry = new ArmyEntry("a", u.Key, u.Name, 1, Loadout: Loadouts.Create(u, 1));
            var group = u.Profile!.Loadout!.Roles.Single().Groups.Single();
            var chosen = Loadouts.Choose(u, entry, 0, group.Id, "unmapped-item", 1);
            Check(WargearPricing.Cost(u, entry) == 0 && WargearPricing.Cost(u, entry with { Loadout = chosen }) == 7, "Unmapped source cost discarded");
            Check(u.WargearCosts!.Single().Source?.SelectionId == "unmapped-item" && u.ImportStatus == ImportStatus.AvailableWithWarnings,
                "Source identity or precise warning status missing");
        });
        test("Recursive selections count a model below another model without counting its group", () =>
        {
            var nested = Role("inner", 1, 1, ""); nested["selectionEntries"] = new JsonArray(Role("companion", 1, 1));
            var carrier = Role("carrier", 1, 1); carrier["selectionEntryGroups"] = new JsonArray(nested);
            var u = Import(Squad(carrier));
            Check(u.Options.Single().Models == 2, "Nested physical model was lost or its group counted as a model");
            var selected = Loadouts.Create(u, 2);
            Check(selected.Models.Select(m => m.RoleId).Order().SequenceEqual(new[] { "carrier", "companion" }), "Recursive model identities lost");
        });
        test("Repeated conditional prices use the shared modifier evaluator after JSON serialization", () =>
        {
            var squad = Squad(Role("body", 2, 4));
            squad["modifiers"] = Array("""[{"field":"points","type":"increment","value":3,"repeats":[{"field":"selections","scope":"squad","childId":"model","includeChildSelections":true,"value":2,"repeats":2}]}]""");
            var u = Import(squad);
            Check(u.Options.Any(o => o.Models == 3 && o.Points == 56) && u.Options.Any(o => o.Models == 4 && o.Points == 62), "Repeated price amount incorrect");
            Check(DataJson.Read<UnitDefinition>(DataJson.Write(u)).Options.SequenceEqual(u.Options), "Repeat price projection lost on serialization");
        });
        var flag = System.Array.IndexOf(args, "--selection-import-archive");
        if (flag < 0 || flag + 1 >= args.Length) return;
        Dataset? imported = null;
        Dataset Data() => imported ??= BsDataImporter.ImportAll(BsDataDocument.ReadArchive(File.ReadAllBytes(args[flag + 1])),
            new("BSData regression", "cc1830fbfead059f8059ed6c7f8e73cd4c390908", "2026-10-02", "", Edition: "11")).Dataset;
        UnitDefinition Unit(string name, string? faction = null) => Data().Units.Single(u => u.Name == name && (faction == null || u.Faction == faction));
        ArmyEntry Entry(UnitDefinition unit, int size) => new("test", unit.Key, unit.Name, size, Loadout: Loadouts.Create(unit, size));
        void Usable(UnitDefinition unit)
        {
            Check(!PricingRepair.NeedsRepair(unit), unit.Name + ": " + unit.PointsWarning);
            foreach (var size in unit.Options.Select(o => o.Models).Distinct())
            {
                var entry = Entry(unit, size);
                Check(Loadouts.Issues(unit, entry).Count == 0, unit.Name + " invalid default " + size);
                Check(WargearPricing.Cost(unit, entry) != null, "Default equipment price lost");
                if (unit.PriceFormula != null) Check(FormulaPricing.Cost(unit, entry, 1, false) != null, "Default formula price lost");
            }
        }
        test("Real Cadian Recon Squad: both factions retain two-model replacement bundle and dynamic limits", () =>
        {
            foreach (var faction in new[] { "Astra Militarum", "Genestealer Cults" })
            {
                var u = Unit("Cadian Recon Squad", faction); Usable(u);
                var entry = Entry(u, 10); var definition = u.Profile!.Loadout!;
                var missile = definition.Roles.Single(r => r.Name.Contains("Missile launcher"));
                var section = Loadouts.Sections(u).Single(s => s.RoleIds.Contains(missile.Id));
                var next = Loadouts.SetRoleCount(u, entry, section.Id, missile.Id, 1);
                Check(next.Models.Count == 10 && next.Models.Count(m => m.RoleId == missile.Id) == 1, "Team changed physical size");
                Check(next.Models.Any(m => definition.Roles.Single(r => r.Id == m.RoleId).Name.Contains("Laspistol")), "Second team model missing");
                Check(Loadouts.Issues(u, entry with { Loadout = next }).Count == 0, "Legal team rejected");
                var sniper = definition.Roles.Single(r => r.Name.Contains("Long-las"));
                try { Loadouts.SetRoleCount(u, entry with { Loadout = next }, section.Id, sniper.Id, 2); throw new Exception("Two snipers accepted alongside team"); }
                catch (InvalidDataException) { }
            }
        });
        test("Real Wolf Scouts: recursive six/twelve-model bundles and repeated specialist replacements", () =>
        {
            var u = Unit("Wolf Scouts"); Usable(u);
            Check(u.Options.Where(o => o.FromCopy == 1).Select(o => o.Models).SequenceEqual(new[] { 6, 12 }), "Scout bundle sizes lost");
            foreach (var size in new[] { 6, 12 })
            {
                var entry = Entry(u, size); var role = u.Profile!.Loadout!.Roles.Single(r => r.Name.Contains("plasma gun"));
                var section = Loadouts.Sections(u).Single(s => s.RoleIds.Contains(role.Id));
                var next = Loadouts.SetRoleCount(u, entry, section.Id, role.Id, 1);
                Check(next.Models.Count == size && Loadouts.Issues(u, entry with { Loadout = next }).Count == 0, "Replacement failed");
            }
        });
        test("Real Jakhals: bundle selection drives Dishonoured requirements and discrete squad sizes", () =>
        {
            var u = Unit("Jakhals"); Usable(u);
            Check(u.Options.Where(o => o.FromCopy == 1).Select(o => o.Models).SequenceEqual(new[] { 10, 20 }), "Invented intermediate Jakhal size");
            foreach (var size in new[] { 10, 20 })
            {
                var entry = Entry(u, size); var definition = u.Profile!.Loadout!;
                var ids = definition.Roles.Where(r => r.Name.StartsWith("Dishonoured")).Select(r => r.Id).ToHashSet();
                Check(entry.Loadout!.Models.Count(m => ids.Contains(m.RoleId)) == size / 10, "Linked required models incorrect");
                var invalid = entry.Loadout with { Models = entry.Loadout.Models.Where(m => !ids.Contains(m.RoleId)).ToList() };
                Check(Loadouts.Issues(u, entry with { Loadout = invalid }).Count > 0, "Missing linked models accepted");
            }
        });
        test("Real Headtakers nested-unit variant derives physical models recursively", () =>
        {
            var u = Unit("Wolf Guard Headtakers (variant 0f93-6850-8f14-a351)"); Usable(u);
            Check(u.Profile!.Loadout!.Composition != null && u.Options.All(o => o.Models >= 3), "Nested unit counts used as model counts");
            Check(SelectionCompositions.Traverse(u.Profile!.Loadout!.Composition!.Root).Count(n => n.Kind == "unit") >= 3, "Nested structure discarded");
            var entry = Entry(u, 6); var wolves = u.Profile.Loadout.Roles.Single(r => r.Name == "Hunting Wolves");
            var section = Loadouts.Sections(u).Single(s => s.RoleIds.Contains(wolves.Id));
            var selected = Loadouts.SetRoleCount(u, entry, section.Id, wolves.Id, 3);
            Check(FormulaPricing.Cost(u, entry with { Loadout = selected }, 1, false) == 115, "Six physical models incorrectly priced as six Headtakers");
            var alternative = u.Profile.Loadout.Roles.Single(r => r.Id != section.DefaultRoleId && r.Id != wolves.Id);
            try { Loadouts.SetRoleCount(u, entry with { Loadout = selected }, section.Id, alternative.Id, 1); throw new Exception("Mixed Headtaker wargear accepted"); }
            catch (InvalidDataException) { }
        });
        test("Real Scourges retain mapped loadout and paid heavy-weapon source costs", () =>
        {
            var u = Unit("Scourges with Heavy Weapons"); Usable(u);
            var entry = Entry(u, 5); var role = u.Profile!.Loadout!.Roles.Single(r => r.Name == "Scourge with Heavy Weapon");
            var group = role.Groups.Single(g => g.Options.Any(o => o.Name == "Dark Lance"));
            var option = group.Options.Single(o => o.Name == "Dark Lance");
            var next = Loadouts.SetOptionCount(u, entry, role.Id, group.Id, option.Id, 4);
            Check(WargearPricing.Cost(u, entry with { Loadout = next }) == 20, "Four five-point weapons not charged exactly once");
        });
        test("Real Inquisitorial Agents apply multiplication and source-ordered native/allied contexts", () =>
        {
            var u = Unit("Inquisitorial Agents"); Usable(u);
            Check(u.Options.Any(o => o.Points == 50) && u.Options.Any(o => o.Points == 100), "Conditional multiplication missing");
            Check(u.AlliedOptions is { Count: > 0 } && u.AlliedOptions.All(o => o.Points == 60), "Later allied SET did not override multiplication in source order");
        });
        test("Real Ghostkeel copy query evaluates first, second and third copies", () =>
        {
            var u = Unit("Ghostkeel Battlesuit"); Usable(u);
            var army = ArmyList.New(u.Faction) with { Entries = Enumerable.Range(1, 3).Select(i => Entry(u, 1) with { Id = i.ToString() }).ToList() };
            Check(ArmyCalculator.Calculate(army, Data()).Entries.Select(e => e.Points).SequenceEqual(new int?[] { 150, 150, 165 }), "Copy threshold/surcharge incorrect");
        });
        test("Real Kapricus entry-link scope resolves and recovered definitions round-trip through SQLite", () =>
        {
            var u = Unit("Kapricus Defenders"); Usable(u);
            Check(u.Options.Any(o => o.Models == 1 && o.Points == 75) && u.Options.Any(o => o.Models == 2 && o.Points == 150), "Link scope prices incorrect");
            var data = Data() with { Units = Data().Units.Where(v => v.Profile?.Loadout?.Composition != null).ToList(), ReferenceCards = [], Detachments = [] };
            using var store = WindowsServices.OpenStore(Path.Combine(directory, "recursive-selections")); store.Import(data);
            var saved = store.LoadDataset()!;
            foreach (var unit in saved.Units) Usable(unit);
        });
    }
}
