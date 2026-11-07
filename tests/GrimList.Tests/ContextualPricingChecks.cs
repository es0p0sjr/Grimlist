using System.Text.Json.Nodes;
using GrimList.Core;

internal static partial class PricingPatternChecks
{
    public static void RunContextual(Action<string, Action> test, string directory)
    {
        var source = new SourceInfo("Contextual fixtures", "1", "", "", Edition: "11");
        test("Dependent model maxima use selected roles, persist, and resize safely", () =>
        {
            var support = Role("support", 1, 1);
            support["modifiers"] = Array("""[{"field":"support-max","type":"increment","value":1,"conditions":[{"field":"selections","scope":"unit","childId":"ordinary","type":"atLeast","value":8,"includeChildSelections":true}]}]""");
            var squad = Squad(Role("ordinary", 4, 8), support); squad["modifiers"] = new JsonArray(Price());
            var unit = Import((JsonObject)squad.DeepClone());
            Check(unit.Options.SequenceEqual(new[] { new PointOption(5, 50), new PointOption(10, 100) }), "Conditional maximum lost supported squad size");
            var definition = unit.Profile!.Loadout!; var bound = definition.Roles.Single(r => r.Id == "support").Maximum;
            Check(bound.Adjustments!.Single().Query?.ChildId == "ordinary" && bound.Value == 1, "Dynamic rule was flattened");
            var selected = Loadouts.Create(unit, 10);
            Check(selected.Models.Count(m => m.RoleId == "support") == 2 && selected.Models.Count(m => m.RoleId == "ordinary") == 8, "Large squad defaults invalid");
            var invalid = new UnitLoadout([.. Enumerable.Range(0, 7).Select(_ => new ModelLoadout("ordinary", [])), new("support", []), new("support", [])], []);
            Check(bound.At(9, invalid.Models) == 1 && Loadouts.Issues(unit, new("a", unit.Key, unit.Name, 9, Loadout: invalid)).Any(i => i.Contains("support")), "Total size substituted for selected ordinary models");
            var reordered = selected with { Models = selected.Models.OrderByDescending(m => m.RoleId == "support").ToList() };
            var small = Loadouts.Resize(unit, reordered, 5);
            Check(small.Models.Count(m => m.RoleId == "support") == 1 && Loadouts.Issues(unit, new("a", unit.Key, unit.Name, 5, Loadout: small)).Count == 0, "Resize retained unsupported extra model");
            using var store = WindowsServices.OpenStore(Path.Combine(directory, "dependent-model-limits")); store.Import(new(source, [unit]));
            var saved = store.LoadDataset()!.Units.Single();
            Check(saved.Profile!.Loadout!.Roles.Single(r => r.Id == "support").Maximum.At(9, invalid.Models) == 1, "Saved constraint lost its selection query");
            var savedAdjustment = bound.Adjustments!.Single();
            var invalidQuery = savedAdjustment with { Query = savedAdjustment.Query! with { RoleIds = ["missing"] } };
            var invalidUnit = unit with { Profile = unit.Profile with { Loadout = definition with { Roles = definition.Roles.Select(r => r.Id == "support" ? r with { Maximum = bound with { Adjustments = [invalidQuery] } } : r).ToList() } } };
            try { new Dataset(source, [invalidUnit]).Validate(); throw new Exception("Missing selection identity accepted"); } catch (InvalidDataException) { }
            support["modifiers"]![0]!["conditions"]![0]!["scope"] = "roster";
            Check(Import(squad).Options.Count == 0, "Unsupported roster query treated as local selections");
        });
        test("Model-group queries require descendant counting and do not count other roles", () =>
        {
            var group = Role("ordinary-group", 4, 8, ""); group["selectionEntries"] = new JsonArray(Role("first", 0, 8), Role("second", 0, 8));
            var support = Role("support", 1, 1);
            support["modifiers"] = Array("""[{"field":"support-max","type":"increment","value":1,"conditions":[{"field":"selections","scope":"squad","childId":"ordinary-group","type":"atLeast","value":8,"includeChildSelections":true}]}]""");
            var squad = Squad(group, support); squad["modifiers"] = new JsonArray(Price());
            var unit = Import((JsonObject)squad.DeepClone());
            var bound = unit.Profile!.Loadout!.Roles.Single(r => r.Id == "support").Maximum;
            List<ModelLoadout> selected = [.. Enumerable.Range(0, 4).Select(_ => new ModelLoadout("first", [])), .. Enumerable.Range(0, 4).Select(_ => new ModelLoadout("second", [])), new("support", [])];
            Check(bound.At(9, selected) == 2 && unit.Options.Last().Models == 10, "Named model group did not unlock second support model");
            selected[0] = new("support", []);
            Check(bound.At(9, selected) == 1, "Other roles counted toward named group threshold");
            support["modifiers"]![0]!["conditions"]![0]!["includeChildSelections"] = false;
            Check(Import(squad).Options.Count == 0, "Non-recursive group query silently counted descendant models");
        });
        test("Missing price references name the catalogue, unit and broken scope", () =>
        {
            var squad = Squad(Role("body", 1, 2)); squad["modifiers"] = new JsonArray(Price(scope: "dead-beef-abcd-1234"));
            var unit = Import(squad);
            Check(unit.Options.Count == 0 && unit.PointsWarning.Contains("Missing price scope 'dead-beef-abcd-1234'")
                && unit.PointsWarning.Contains("Fixture squad") && unit.PointsWarning.Contains("Chaos - Death Guard"), "Missing source reference was hidden behind a generic unsupported-rule error");
        });
        test("Mandatory detachment upgrades activate exactly once and survive persistence", () =>
        {
            var upgrade = Role("mandatory", 0, 1, "upgrade"); upgrade["hidden"] = true;
            upgrade["costs"] = Array("""[{"name":"pts","typeId":"points","value":25}]""");
            upgrade["modifiers"] = Array("""[{"field":"mandatory-min","type":"set","value":1,"conditions":[{"field":"selections","scope":"force","childId":"special","type":"atLeast","value":1}]},{"field":"hidden","type":"set","value":false,"conditions":[{"field":"selections","scope":"force","childId":"special","type":"atLeast","value":1}]}]""");
            var detachment = Role("special", 0, 1, "upgrade"); detachment["name"] = "Special host";
            var squad = Squad(Role("body", 1, 1), upgrade);
            var choice = Role("detachments", 0, 1, ""); choice["name"] = "Detachment"; choice["selectionEntries"] = new JsonArray(detachment);
            var forces = new JsonArray(new JsonObject { ["id"] = "army", ["selectionEntryGroups"] = new JsonArray(choice) });
            var unit = Import((JsonObject)squad.DeepClone(), forces: (JsonArray)forces.DeepClone());
            Check(unit.DetachmentCosts!.Single().Points == 25 && unit.Options.Single().Points == 50, "Mandatory upgrade folded into every detachment");
            var data = new Dataset(source, [unit], Detachments: [new(unit.Faction, "Special host")]);
            var army = ArmyList.New(unit.Faction) with { Entries = [new("a", unit.Key, unit.Name, 1)] };
            Check(ArmyCalculator.Calculate(army, data).Total == 50, "Inactive upgrade charged");
            army = army with { Detachment = "Special host" };
            Check(ArmyCalculator.Calculate(army, data).Total == 75, "Mandatory upgrade missing or doubled");
            using var store = WindowsServices.OpenStore(Path.Combine(directory, "mandatory-detachment")); store.Import(data); store.Save(army);
            var loaded = store.LoadDataset()!;
            Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), loaded).Total == 75, "Detachment surcharge lost on save");
            var profiles = DatasetMerge.Prepare(loaded, data with { Units = [unit with { Options = [], DetachmentCosts = null }] }, ImportLayer.Profiles).Dataset;
            Check(ArmyCalculator.Calculate(army, profiles).Total == 75, "Profile refresh removed detachment pricing");
            upgrade["modifiers"]![1]!["conditions"]![0]!["childId"] = "unknown";
            Check(Import(squad, forces: forces).Options.Count == 0, "Mismatched activation accepted");
        });
        test("Composition formulas price optional models, copies and saved loadouts", () =>
        {
            var standard = Role("standard", 2, 4); var extra = Role("extra", 0, 1);
            extra["costs"] = Array("""[{"name":"pts","typeId":"points","value":60}]""");
            var squad = Squad(standard, extra); var rule = Price("standard"); rule["conditions"]![0]!["value"] = 4;
            squad["modifiers"] = new JsonArray(rule);
            var unit = Import(squad); Check(unit.PriceFormula != null && unit.ModelCosts!.Single().Points == 60, "Optional composition not represented");
            var data = new Dataset(source, [unit]); data.Validate();
            var entry = new ArmyEntry("a", unit.Key, unit.Name, 4, Loadout: Loadouts.Create(unit, 4));
            var army = ArmyList.New(unit.Faction) with { Entries = [entry] };
            Check(ArmyCalculator.Calculate(army, data).Total == 100, "Four standard models should trigger larger band");
            var chosen = entry.Loadout! with { Models = [new("extra", []), .. entry.Loadout!.Models.Skip(1)] };
            entry = entry with { Loadout = chosen }; army = army with { Entries = [entry] };
            var result = ArmyCalculator.Calculate(army, data);
            Check(result.Complete && result.Total == 110 && result.Entries[0].WargearPoints == 60, "Three standard models plus extra charged the wrong band");
            using var store = WindowsServices.OpenStore(Path.Combine(directory, "composition-pricing")); store.Import(data); store.Save(army);
            Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset()).Total == 110, "Formula or chosen paid model lost on save");
            var loaded = store.LoadDataset()!;
            var profiles = DatasetMerge.Prepare(loaded, data with { Units = [unit with { Options = [], ModelCosts = null, PriceFormula = null }] }, ImportLayer.Profiles).Dataset;
            Check(ArmyCalculator.Calculate(army, profiles).Total == 110, "Profile update dropped formula pricing");
            Check(FormulaPricing.Cost(unit, entry with { Loadout = chosen with { Models = [new("missing", []), .. chosen.Models.Skip(1)] } }, 1, false) == null, "Unknown role got a price");
            Check(FormulaPricing.Cost(unit, entry with { Loadout = chosen with { Models = chosen.Models.Select(_ => new ModelLoadout("extra", [])).ToList() } }, 1, false) == null, "Impossible paid model count got a price");
            var taxed = unit with { PriceFormula = unit.PriceFormula! with { Rules = [.. unit.PriceFormula!.Rules, new("increment", 10, new("copy", "atLeast", 2))] } };
            var copies = ArmyCalculator.Calculate(army with { Entries = [entry, entry with { Id = "b" }] }, data with { Units = [taxed] });
            Check(copies.Total == 230 && copies.Entries.Last().CopyTax == 10, "Composition formula lost copy surcharge or double-charged paid model");
            try { (data with { Units = [unit with { ModelCosts = [new("missing", 1)] }] }).Validate(); throw new Exception("Missing model identity accepted"); } catch (InvalidDataException) { }
            try { (data with { Units = [unit with { PriceFormula = new(50, [new("divide", 2, new("models", "atLeast", 4))]) }] }).Validate(); throw new Exception("Unsupported formula accepted"); } catch (InvalidDataException) { }
        });
        test("Crucible and battle-only references are excluded from repairs and retained unchanged", () =>
        {
            var crucible = new UnitDefinition("Death Guard", "Fixture [Crucible]", [], PointsWarning: "Unknown");
            var reference = crucible with { Name = "Spawned unit", Profile = new(Keywords: ["Reference"]) };
            var broken = crucible with { Name = "Ordinary unit" };
            var current = new Dataset(source, [crucible, reference, broken]);
            var incoming = current with { Units = current.Units.Select(u => u with { Options = [new(1, 50)], PointsWarning = "" }).ToList() };
            var preview = PricingRepair.Preview(current, incoming);
            Check(preview.Recovered == 1 && preview.Unresolved == 0 && preview.SkippedCrucible == 1 && preview.SkippedReferences == 1, "Skipped entries polluted repair results");
            Check(DataJson.Write(preview.Dataset.Units.Take(2)) == DataJson.Write(current.Units.Take(2)), "Skipped units changed");
            Check(PricingRepair.Scan(preview.Dataset).Count == 0, "Skipped entries reappeared in scan");
            Check(UnitAvailability.Problem(ArmyList.New("Death Guard"), reference, current) != null, "Spawned models offered as roster units");
            Check(PricingRepair.IsCrucible(broken with { Profile = new(Keywords: ["Crucible"]) }), "Category-tagged Crucible entry not skipped");
        });
        test("Optional enhancement purchases stay separate from base unit pricing", () =>
        {
            var option = Role("enhancement", 0, 1, "upgrade");
            option["costs"] = Array("""[{"name":"pts","typeId":"points","value":20},{"name":"Enhancements","typeId":"enhancement-count","value":1}]""");
            var unit = Import(Squad(Role("body", 1, 1), option));
            Check(unit.Options.Single().Points == 50 && unit.ModelCosts!.Count == 0, "Optional enhancement charged in base price");
        });
        test("Unconstrained wargear containers retain all mandatory weapons", () =>
        {
            JsonObject Gun(string id)
            {
                var gun = Role(id, 1, 1, "upgrade");
                gun["profiles"] = Array($$"""[{"name":"{{id}}","typeName":"Ranged Weapons","characteristics":[{"name":"Range","$text":"24"}]}]""");
                return gun;
            }
            var body = Role("body", 1, 1);
            body["selectionEntryGroups"] = new JsonArray(new JsonObject { ["id"] = "weapons", ["name"] = "Wargear", ["selectionEntries"] = new JsonArray(Gun("rifle"), Gun("pistol")) });
            var unit = Import(Squad(body));
            var entry = new ArmyEntry("a", unit.Key, unit.Name, 1, Loadout: Loadouts.Create(unit, 1));
            Check(Loadouts.Issues(unit, entry).Count == 0 && Loadouts.Equipment(unit, entry.Loadout).Sum(e => e.Count) == 2, "Mandatory weapons forced into an invented exclusive group");
        });
        test("Repair refreshes loadout definitions, preserves saved selections and checks default prices", () =>
        {
            var old = new UnitDefinition("Death Guard", "Repaired fixture", [], Profile: new(Notes: "Retained datasheet"), PointsWarning: "Old importer failure");
            var current = new Dataset(source, [old]);
            var loadout = new LoadoutDefinition([new("body", "Body", new(1), new(1), [])], [], []);
            var fixedUnit = old with { Options = [new(1, 50)], PointsWarning = "", ModelCosts = [new("body", 20)], Profile = new(Notes: "New source notes", Loadout: loadout) };
            var incoming = current with { Units = [fixedUnit] };
            using var store = WindowsServices.OpenStore(Path.Combine(directory, "loadout-refresh-repair")); store.Import(current);
            var army = ArmyList.New(old.Faction) with { Entries = [new("saved", old.Key, old.Name, 1, Loadout: new([], [], Manual: true))] };
            store.Save(army); var saved = DataJson.Write(store.LoadArmies());
            var preview = PricingRepair.Preview(store.LoadDataset()!, incoming);
            Check(preview.Recovered == 1 && preview.Dataset.Units[0].Profile!.Notes == "Retained datasheet", "Repair did not preserve other datasheet content");
            Check(preview.Dataset.Units[0].Profile!.Loadout != null, "Repair missed required loadout definition");
            PricingRepairService.Apply(store, preview);
            Check(DataJson.Write(store.LoadArmies()) == saved, "Repair silently reinterpreted saved loadout");
            var result = ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset());
            Check(!result.Complete && result.Issues.Any(i => i.Contains("Reset this unit's loadout")), "Outdated paid loadout not flagged");
            var unusable = fixedUnit with { Profile = fixedUnit.Profile! with { Loadout = loadout with { Roles = [loadout.Roles[0] with { Minimum = new(0), Maximum = new(0) }] } } };
            Check(PricingRepair.Preview(current, current with { Units = [unusable] }).Recovered == 0, "Unavailable default total reported as recovered");
        });
    }
}
