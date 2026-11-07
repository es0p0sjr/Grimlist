using System.Text.Json.Nodes;
using GrimList.Core;

internal static class PricingRepairChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe repair accepted"); }
    private static readonly SourceInfo Source = new("BSData repair fixtures", "1", "", "", Edition: "11");
    private static BsDataDocument Catalogue() => BsDataDocument.Parse("agents-fixture.json", """
    {"catalogue":{"id":"agents","name":"Imperium - Agents of the Imperium","gameSystemId":"game",
      "selectionEntries":[
        {"id":"assassin","name":"Fixture assassin","type":"model","costs":[{"name":"pts","typeId":"points","value":100}],
          "categoryLinks":[{"name":"Faction: Agents of the Imperium"},{"name":"Imperium"},{"name":"Character"}],
          "profiles":[{"name":"Fixture","typeName":"Unit","characteristics":[{"name":"T","$text":"4"}]}],
          "modifiers":[{"field":"points","type":"increment","value":10,"conditions":[{"field":"selections","scope":"primary-catalogue","childId":"agents","type":"notInstanceOf","value":1}]}]},
        {"id":"squad","name":"Fixture fixed squad","type":"unit","costs":[{"name":"pts","typeId":"points","value":175}],
          "categoryLinks":[{"name":"Faction: Agents of the Imperium"},{"name":"Imperium"},{"name":"Requisitioned"}],
          "profiles":[{"name":"Fixture","typeName":"Unit","characteristics":[{"name":"T","$text":"6"}]}],
          "selectionEntries":[{"id":"leader","name":"Leader","type":"model","constraints":[{"field":"selections","scope":"parent","type":"min","value":1},{"field":"selections","scope":"parent","type":"max","value":1}]}],
          "selectionEntryGroups":[{"id":"four","name":"Four models","constraints":[{"field":"selections","scope":"parent","type":"min","value":4},{"field":"selections","scope":"parent","type":"max","value":4}],
            "selectionEntries":[{"id":"ordinary","name":"Ordinary model","type":"model","constraints":[{"id":"ordinary-max","field":"selections","scope":"parent","type":"max","value":4}],
              "modifiers":[{"field":"ordinary-max","type":"decrement","value":1,"repeats":[{"field":"selections","scope":"parent","childId":"specialist","value":1}]}]},
              {"id":"specialist","name":"Specialist","type":"model","constraints":[{"field":"selections","scope":"parent","type":"max","value":1}]}]}]}
      ]}}
    """);
    public static void Run(Action<string, Action> test, string root)
    {
        test("Catalogue-dependent prices retain distinct native and allied costs through persistence and merging", () =>
        {
            var data = BsDataImporter.ImportAll([Catalogue()], Source).Dataset;
            var unit = data.Units.Single(u => u.Name == "Fixture assassin");
            Check(unit.Options.Single().Points == 100 && unit.AlliedOptions?.Single().Points == 110, "Native and allied costs collapsed");
            foreach (var (faction, expected) in new[] { ("Imperial Agents", 100), ("Astra Militarum", 110) })
            {
                var army = ArmyList.New(faction) with { Entries = [new("one", unit.Key, unit.Name, 1)] };
                Check(ArmyCalculator.Calculate(army, data).Total == expected, "Wrong cost in " + faction);
            }
            using var store = WindowsServices.OpenStore(Path.Combine(root, "contextual-prices")); store.Import(data);
            Check(store.LoadDataset()!.Units.Single(u => u.Name == unit.Name).AlliedOptions!.Single().Points == 110, "Allied cost lost on save");
            var profiles = data with { Units = data.Units.Select(u => u with { Options = [], AlliedOptions = null }).ToList() };
            var merged = store.PreviewImport(profiles, ImportLayer.Profiles).Dataset;
            Check(merged.Units.Single(u => u.Name == unit.Name).AlliedOptions!.Single().Points == 110, "Profile update removed allied costs");
            var changed = data with { Units = data.Units.Select(u => u == unit ? u with { AlliedOptions = [new(1, 120)] } : u).ToList() };
            Check(ArmyStore.Compare(data, changed).Single().Context == "Allies", "Allied price change missing from review");
            Reject(() => (data with { Units = [unit with { AlliedOptions = [new(1, 100, 2)] }] }).Validate());
        });
        test("Fixed model groups recover total size without discarding paid-role validation", () =>
        {
            var document = Catalogue();
            var unit = BsDataImporter.ImportAll([document], Source).Dataset.Units.Single(u => u.Name == "Fixture fixed squad");
            Check(unit.Options.SequenceEqual(new[] { new PointOption(5, 175) }), "Fixed group substitutions obscured size");
            document.Root["selectionEntries"]![1]!["selectionEntryGroups"]![0]!["selectionEntries"]![1]!["costs"] = JsonNode.Parse("""[{"name":"pts","typeId":"points","value":20}]""");
            unit = BsDataImporter.ImportAll([document], Source).Dataset.Units.Single(u => u.Name == "Fixture fixed squad");
            Check(unit.Options.Single().Points == 175 && unit.ModelCosts!.Single().Points == 20, "Specialist cost was discarded");
            var loadout = Loadouts.Create(unit, 5);
            Check(WargearPricing.Cost(unit, new("a", unit.Key, unit.Name, 5, Loadout: loadout)) == 0, "Unselected specialist charged");
            var index = loadout.Models.FindIndex(m => m.RoleId == "ordinary");
            loadout = loadout with { Models = loadout.Models.Select((m, i) => i == index ? new ModelLoadout("specialist", []) : m).ToList() };
            Check(WargearPricing.Cost(unit, new("a", unit.Key, unit.Name, 5, Loadout: loadout)) == 20, "Selected specialist not charged");
            loadout = loadout with { Models = loadout.Models.Select(m => m.RoleId == "ordinary" ? new ModelLoadout("specialist", []) : m).ToList() };
            Check(WargearPricing.Cost(unit, new("a", unit.Key, unit.Name, 5, Loadout: loadout)) == null, "Invalid paid composition accepted");
        });
        test("Parent conditions and OR price groups preserve their predicates and reject unknown contexts", () =>
        {
            var document = Catalogue(); var model = document.Root["selectionEntries"]![0]!;
            model["modifiers"] = new JsonArray();
            model["modifierGroups"] = JsonNode.Parse("""[{"type":"and","conditions":[{"field":"selections","scope":"primary-catalogue","childId":"agents","type":"notInstanceOf","value":1}],"modifiers":[{"field":"points","type":"increment","value":10}]}]""");
            var unit = BsDataImporter.ImportAll([document], Source).Dataset.Units[0];
            Check(unit.Options.Single().Points == 100 && unit.AlliedOptions!.Single().Points == 110, "Parent condition ignored");
            model["modifierGroups"]![0]!["type"] = "or";
            Check(BsDataImporter.ImportAll([document], Source).Dataset.Units[0].Options.Single().Points == 100, "OR group evaluated incorrectly");
            model["modifierGroups"]![0]!["conditions"]![0]!["scope"] = "unknown";
            Check(BsDataImporter.ImportAll([document], Source).Dataset.Units[0].Options.Count == 0, "Unknown predicate treated as unconditional");
        });

        var incoming = BsDataImporter.ImportAll([Catalogue()], Source).Dataset;
        var good = new UnitDefinition("Imperial Agents", "Healthy fixture", [new(1, 42)]);
        var missing = new UnitDefinition("Imperial Agents", "Unsupported fixture", [], Profile: new(Keywords: ["Infantry"]), PointsWarning: "Source has no supported cost.");
        var before = incoming with { Units = incoming.Units.Select(u => u with { Options = [], AlliedOptions = null, PointsWarning = "Old importer error." }).Concat([good, missing]).ToList() };
        incoming = incoming with { Units = [.. incoming.Units, good with { Options = [new(1, 99)] }, missing] };
        test("Bulk repair scans all failures, preserves healthy units and reports unresolved sources", () =>
        {
            var repair = PricingRepair.Preview(before, incoming);
            Check(repair.Recovered == 2 && repair.Unresolved == 1 && repair.Rows.Count == 3, "Incomplete repair audit");
            Check(repair.Dataset.FindUnit(good.Key)!.Options.Single().Points == 42, "Healthy unit changed");
            foreach (var original in before.Units)
            {
                var fixedUnit = repair.Dataset.FindUnit(original.Key)!;
                Check(original.Id == fixedUnit.Id && DataJson.Write(original.Profile) == DataJson.Write(fixedUnit.Profile), "Identity or datasheet changed");
            }
            var alliedOnly = before with { Units = [incoming.Units[0] with { AlliedOptions = [], AlliedPointsWarning = "Unavailable" }] };
            Check(PricingRepair.Scan(alliedOnly).Count == 1 && PricingRepair.Preview(alliedOnly, incoming).Recovered == 1, "Missing allied-only costs overlooked");
            var paidOnly = before with { Units = [good with { WargearCosts = [new("weapon", null)] }] };
            Check(PricingRepair.Scan(paidOnly).Count == 1, "Unknown wargear cost overlooked");
            Reject(() => PricingRepair.Preview(before, incoming with { Source = Source with { Edition = "10" } }));
            Reject(() => PricingRepair.Preview(before, incoming with { Game = "Other game" }));
            try { PricingRepair.Preview(before, incoming, new CancellationToken(true)); throw new Exception("Cancelled repair completed"); }
            catch (OperationCanceledException) { }
        });
        test("Repair application backs up data, retains saved armies and rejects stale previews", () =>
        {
            var directory = Path.Combine(root, "pricing-repair");
            using var store = WindowsServices.OpenStore(directory); store.Import(before);
            var current = store.LoadDataset()!; var unit = current.Units.First(u => u.Name == "Fixture assassin");
            store.Save(ArmyList.New("Astra Militarum") with { Entries = [new("saved", unit.Key, unit.Name, 1)] });
            var armies = DataJson.Write(store.LoadArmies()); var preview = PricingRepair.Preview(current, incoming);
            var backup = PricingRepairService.Apply(store, preview);
            Check(File.Exists(Path.Combine(backup, "dataset.json")) && File.Exists(Path.Combine(backup, "armies.json")), "Backup missing");
            Check(DataJson.Write(store.LoadArmies()) == armies, "Saved army changed");
            Check(ArmyCalculator.Calculate(store.LoadArmies().Single(), store.LoadDataset()).Total == 110, "Saved ally price still unavailable");
            Reject(() => PricingRepairService.Apply(store, preview));
            Check(PricingRepair.Preview(store.LoadDataset()!, incoming).Recovered == 0, "Repeated repair was not idempotent");
        });
        File.WriteAllText(Path.Combine(root, "pricing-repair-before.json"), DataJson.Write(before));
        File.WriteAllText(Path.Combine(root, "pricing-repair-source.json"), DataJson.Write(incoming));
    }
}
