using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;

internal static class EquipmentPriceChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Dataset Fixture(int price = 0, bool conditional = false, bool ambiguous = false, bool missing = false, bool bundle = false)
    {
        var document = BsDataDocument.Parse("fixture.json", """
        {"catalogue":{"id":"equipment-fixture","name":"Chaos - Death Guard","costTypes":[{"id":"pts","name":"pts"}],
          "selectionEntries":[{"id":"model","name":"Equipment fixture","type":"model","costs":[{"typeId":"pts","name":"pts","value":100}],
            "profiles":[{"id":"stats","name":"Model","typeName":"Unit","characteristics":[{"name":"W","$text":"2"}]}],
            "selectionEntries":[{"id":"blade","name":"Blade","type":"upgrade","profiles":[{"id":"blade-p","name":"Blade","typeName":"Melee Weapons"}]}],
            "selectionEntryGroups":[{"id":"outer","name":"Equipment bundle","selectionEntries":[{"id":"bundle","name":"Nested bundle","type":"upgrade",
              "selectionEntryGroups":[{"id":"inner","name":"Gun choice","selectionEntries":[{"id":"gun","name":"Gun","type":"upgrade","costs":[{"typeId":"pts","name":"pts","value":0}],
                "profiles":[{"id":"gun-p","name":"Gun","typeName":"Ranged Weapons"}]}]}]}]}]}]}}
        """);
        var root = document.Root["selectionEntries"]![0]!;
        var wrapper = root["selectionEntryGroups"]![0]!["selectionEntries"]![0]!;
        var choices = wrapper["selectionEntryGroups"]![0]!["selectionEntries"]!.AsArray();
        var gun = choices[0]!.AsObject(); gun["costs"]![0]!["value"] = price;
        if (conditional) gun["modifiers"] = JsonNode.Parse("""[{"type":"increment","field":"pts","value":5,"conditions":[{"type":"atLeast","field":"selections","scope":"parent","value":1}]}]""");
        if (ambiguous) { var other = gun.DeepClone(); other["id"] = "other-gun"; other["costs"]![0]!["value"] = price + 5; choices.Add(other); }
        if (missing) gun["infoLinks"] = JsonNode.Parse("""[{"type":"profile","targetId":"missing-profile"}]""");
        if (bundle) wrapper["costs"] = JsonNode.Parse("""[{"typeId":"pts","name":"pts","value":10}]""");
        return BsDataImporter.Import([document], new("Synthetic equipment source", "1", "", "", Edition: "11", ContentHash: "synthetic-hash")).Dataset;
    }
    private static ArmyEntry Manual(UnitDefinition unit, string name = "Gun", int count = 2) => new("saved-entry", unit.Key, unit.Name, 1,
        Loadout: new([], [new("manual", "manual:" + name, count)], true));

    public static void Run(Action<string, Action> test, string root)
    {
        test("Equipment pricing survives nested choices and keeps mapped selections", () =>
        {
            var unit = Fixture().Units.Single();
            Check(unit.Profile?.Loadout?.Incomplete == true, "Incomplete loadout not labelled");
            Check(WargearPricing.Options(unit).Any(o => o.Id == "blade"), "Mapped blade was discarded");
            Check(WargearPricing.OptionCost(unit, "manual:Gun") == 0, "Included gun lost its price");
            Check(WargearPricing.Cost(unit, Manual(unit)) == 0, "Manual included equipment not priced");
            Check(Loadouts.Issues(unit, Manual(unit)).Contains(Loadouts.ReviewWarning), "Manual legality review missing");
            Check(WargearPricing.CoverageIssues(unit).Count == 0, "Price coverage incomplete");
        });
        test("Manual prices charge paid weapons and retain conditional ambiguous missing and bundled unknowns", () =>
        {
            var paid = Fixture(price: 5).Units.Single();
            Check(WargearPricing.Cost(paid, Manual(paid)) == 10, "Paid manual weapons not charged twice");
            foreach (var data in new[] { Fixture(conditional: true), Fixture(ambiguous: true), Fixture(missing: true), Fixture(bundle: true) })
            {
                var unit = data.Units.Single();
                Check(WargearPricing.OptionCost(unit, "manual:Gun") == null, "Unknown became free");
                Check(WargearPricing.Cost(unit, Manual(unit)) == null, "Unknown produced numeric total");
                Check(unit.WargearCosts!.Single(c => c.OptionId == "manual:Gun").Warning.Length > 0, "Unknown lacks explanation");
            }
        });
        test("Repair scan detects empty equipment lists and stale selections cannot disappear from totals", () =>
        {
            var unit = Fixture().Units.Single();
            var legacy = unit with { Profile = unit.Profile! with { Loadout = null }, WargearCosts = [] };
            Check(PricingRepair.NeedsRepair(legacy), "Empty price table escaped repair scan");
            Check(WargearPricing.CoverageIssues(legacy).Count > 0, "Gate missed missing prices");
            Check(WargearPricing.Cost(unit, Manual(unit, "Removed weapon")) == null, "Stale selection priced as zero");
        });
        test("Equipment recovery pins source identity preserves overrides and is idempotent", () =>
        {
            var data = Fixture(); var unit = data.Units.Single(); var pack = EquipmentPriceRecovery.Create(data);
            var legacy = unit with { Profile = unit.Profile! with { Loadout = null }, WargearCosts = [], Options = [new(1, 123)],
                PointsSource = new("MFM", "saved", "", "", Edition: "11") };
            var old = data with { Units = [legacy] };
            var recovered = EquipmentPriceRecovery.Apply(old, [pack]);
            Check(WargearPricing.Cost(recovered.Units[0], Manual(legacy)) == 0, "Legacy prices not recovered");
            Check(recovered.Units[0].Options[0].Points == 123 && recovered.Units[0].PointsSource == legacy.PointsSource, "Base price changed");
            Check(recovered.Units[0].Profile == legacy.Profile, "Legacy loadout/profile changed");
            Check(ReferenceEquals(recovered, EquipmentPriceRecovery.Apply(recovered, [pack])), "Recovery not idempotent");
            foreach (var changed in new[] { legacy with { ProfileSource = legacy.ProfileSource! with { ContentHash = "different" } },
                legacy with { ProfileSource = legacy.ProfileSource! with { Edition = "10" } }, legacy with { Aliases = ["different"] },
                legacy with { Profile = legacy.Profile! with { Weapons = [] } } })
                Check(ReferenceEquals(changed, EquipmentPriceRecovery.Apply(old with { Units = [changed] }, [pack]).Units[0]), "Recovery crossed identity boundary");
            var overridden = legacy with { WargearCosts = [new("manual:Gun", 9)] };
            Check(WargearPricing.OptionCost(EquipmentPriceRecovery.Apply(old with { Units = [overridden] }, [pack]).Units[0], "manual:Gun") == 9, "Override replaced");
            var sourceOverride = legacy with { WargearCosts = [new("gun", 9)] };
            Check(ReferenceEquals(sourceOverride, EquipmentPriceRecovery.Apply(old with { Units = [sourceOverride] }, [pack]).Units[0]),
                "Manual alias bypasses an existing source-selection price");
            Check(ReferenceEquals(old, EquipmentPriceRecovery.Apply(old, [pack, pack])), "Conflicting evidence accepted");
            var flat = legacy with { Profile = legacy.Profile! with { Weapons = legacy.Profile.Weapons!.Select(w => w with { SourceId = null, SourceCategories = null }).ToList() } };
            Check(WargearPricing.Cost(EquipmentPriceRecovery.Apply(old with { Units = [flat] }, [pack]).Units[0], Manual(flat)) == 0,
                "Source-pinned flattened legacy profile was not recovered");
            var edited = flat with { Profile = flat.Profile! with { Weapons = flat.Profile.Weapons!.Select(w => w with { Damage = "999" }).ToList() } };
            Check(ReferenceEquals(edited, EquipmentPriceRecovery.Apply(old with { Units = [edited] }, [pack]).Units[0]), "Edited legacy profile was overwritten");
        });
        test("Saved manual equipment survives source recovery save reopen and automatic datasheet refresh", () =>
        {
            var data = Fixture(); var unit = data.Units[0]; var entry = Manual(unit);
            var army = ArmyList.New(unit.Faction) with { Entries = [entry] };
            var folder = Path.Combine(root, "equipment-recovery");
            using (var store = WindowsServices.OpenStore(folder)) { store.Import(data); store.Save(army); }
            using (var store = WindowsServices.OpenStore(folder))
            {
                var session = new ArmySession(store); session.Open(army.Id);
                Check(DataJson.Write(session.Army.Entries[0].Loadout) == DataJson.Write(entry.Loadout), "Saved quantity changed");
                Check(Loadouts.Weapons(unit, session.Army.Entries[0].Loadout).Single().Count == 2, "Saved weapon lost");
                Check(WargearPricing.Cost(unit, session.Army.Entries[0]) == 0, "Saved price lost");
                session.ChooseEquipment(entry.Id, -1, "manual", "manual:Gun", 3);
                session.Undo(); Check(session.Army.Entries[0].Loadout!.Equipment.Single().Count == 2, "Undo lost manual quantity");
                session.Redo(); Check(session.Army.Entries[0].Loadout!.Equipment.Single().Count == 3, "Redo lost manual quantity");
            }
            using (var store = WindowsServices.OpenStore(folder))
                Check(store.LoadArmies().Single().Entries[0].Loadout!.Equipment.Single().Count == 3, "Manual edit failed to persist");
        });
        test("Bundled equipment recovery covers affected units and preserves Boyz base total", () =>
        {
            var old = BootstrapRelease.ReadBundled().Dataset; var repaired = EquipmentPriceRecovery.ApplyBundled(old);
            var errors = EquipmentPriceRecovery.ValidateBundled(old);
            Check(errors.Count == 0, "Bundled candidate has silent price gaps: " + string.Join("; ", errors.Take(10)));
            var affected = old.Units.Where(u => u.Profile?.Loadout == null && u.Profile?.Weapons?.Count > 0 && u.WargearCosts is { Count: 0 }).ToList();
            Check(affected.Count >= 63, "Affected-unit fixture missing");
            foreach (var unit in affected)
                Check(WargearPricing.CoverageIssues(repaired.Units.Single(u => u.Key == unit.Key)).Count == 0, unit.Name + " not covered");
            var boyz = repaired.Units.Single(u => u.Faction == "Orks" && u.Name == "Boyz");
            foreach (var option in Loadouts.ManualOptions(boyz.Profile!.Weapons!))
                Check(WargearPricing.OptionCost(boyz, option.Id) == 0, "Boyz missing price: " + option.Name);
            var entry = Manual(boyz, "Choppa", 9) with { Models = 10 };
            var result = ArmyCalculator.Calculate(ArmyList.New("Orks") with { Entries = [entry] }, repaired);
            Check(result.Entries.Single().Points == boyz.Options.Single(p => p.Models == 10 && p.FromCopy == 1).Points, "Boyz base total lost");
        });
        test("Pinned Orks importer retains Boyz choices and prices every manually selectable weapon", () =>
        {
            var bytes = File.ReadAllBytes("tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip");
            var documents = BsDataDocument.ReadArchive(bytes); var orks = documents.Single(d => BsDataImporter.FactionName(d) == "Orks");
            var data = BsDataImporter.ImportAll(documents.Where(d => d == orks || d.Id == BsDataDocument.Text(orks.Root, "gameSystemId")).ToList(),
                new("Pinned Orks fixture", "cc1830", "", "", Edition: "11")).Dataset;
            var boyz = data.Units.Single(u => u.Name == "Boyz");
            Check(boyz.Profile!.Loadout is { Incomplete: true, Roles.Count: > 0 }, "Boyz supported roles were discarded");
            foreach (var option in Loadouts.ManualOptions(boyz.Profile.Weapons!))
                Check(WargearPricing.OptionCost(boyz, option.Id) == 0, "Source Boyz price missing: " + option.Name);
            Check(WargearPricing.CoverageIssues(boyz).Count == 0, "Source Boyz price gap");
            Check(boyz.Options.Any(o => o.Models == 10 && o.Points == 85), "Source Boyz base tier lost");
        });
    }
}
