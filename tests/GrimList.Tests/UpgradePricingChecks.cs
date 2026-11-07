using System.Text.Json.Nodes;
using GrimList.Core;

internal static class UpgradePricingChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid selection accepted"); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var source = new SourceInfo("Upgrade and wargear fixtures", "1", "", "", Edition: "11");
        var troops = new UnitDefinition("Death Guard", "Upgrade troops", [new(1, 50)], Profile: new(Keywords: ["Infantry", "Battleline"]));
        var character = troops with { Name = "Upgrade character", Category = "Characters", Profile = new(Keywords: ["Infantry", "Character"],
            Abilities: [new("Leader", "This model can be attached to the following units: Upgrade troops.")]) };
        var hero = character with { Name = "Upgrade epic hero", Profile = new(Keywords: ["Character", "Epic Hero", "Infantry"]) };
        var upgrade = new ReferenceCard("Death Guard", "Enhancements", "Infantry blessing", "INFANTRY unit only. Test upgrade.", "Test host", "15 pts", IsUpgrade: true);
        var other = upgrade with { Name = "Other blessing UPGRADE", IsUpgrade = false };
        var normal = upgrade with { Name = "Character blessing", IsUpgrade = false };
        var data = new Dataset(source, [troops, character, hero], Detachments: [new("Death Guard", "Test host")], ReferenceCards: [upgrade, other, normal]);
        ArmyList Army(int limit = 1000) => ArmyList.New("Death Guard") with { Limit = limit, Detachment = "Test host", Entries = [
            .. Enumerable.Range(0, 4).Select(i => new ArmyEntry("troop-" + i, troops.Key, troops.Name, 1)),
            .. Enumerable.Range(0, 5).Select(i => new ArmyEntry("char-" + i, character.Key, character.Name, 1))] };
        EnhancementSelection Pick(ReferenceCard card) => new(card.Detachment, card.Name);
        test("Upgrade tags allow eligible regular units, exclude Epic Heroes and retain keyword and detachment restrictions", () =>
        {
            var army = Army();
            Check(Enhancements.Options(army, data, troops).Select(c => c.Name).Order().SequenceEqual(new[] { upgrade.Name, other.Name }.Order()), "Regular units did not see tagged upgrades only");
            Check(Enhancements.Options(army, data, hero).Count == 0, "Epic Hero allowed an upgrade");
            Check(Enhancements.Options(army with { Detachment = "Other host" }, data, troops).Count == 0, "Inactive upgrade offered");
            Check(!Enhancements.Eligible(army, troops, upgrade with { Text = "VEHICLE unit only. Test." }), "Upgrade ignored keyword restriction");
            Reject(() => Enhancements.Select(army, data, "troop-0", Pick(normal)));
            var selected = Enhancements.Select(army, data, "troop-0", Pick(upgrade));
            Check(ArmyCalculator.Calculate(selected, data).Total == 465, "Upgrade not charged exactly once");
            Reject(() => Enhancements.Select(selected, data, "troop-0", Pick(other)));
            selected = Enhancements.Select(Enhancements.Select(selected, data, "troop-0", null), data, "troop-0", Pick(other));
            Check(Enhancements.Usage(selected, data) == (1, 1), "Replacing upgrade used a second unit slot");
            Check(ArmyCalculator.Export(selected, data).Contains("Upgrade: Other blessing"), "Export lost upgrade type");
            using (var store = WindowsServices.OpenStore(Path.Combine(root, "upgrade-persistence"))) { store.Import(data); store.Save(selected); }
            using (var store = WindowsServices.OpenStore(Path.Combine(root, "upgrade-persistence")))
            {
                var loaded = store.LoadArmies().Single();
                Check(Enhancements.Usage(loaded, store.LoadDataset()) == (1, 1), "Upgrade tag or selection lost on reload");
            Check(loaded.Duplicate().Entries[0].Enhancement == selected.Entries[0].Enhancement, "Duplication lost upgrade");
            }
        });
        test("Enhancements and upgrades share one slot and rejected additions preserve the existing choice", () =>
        {
            var army = Enhancements.Select(Army(), data, "char-0", Pick(normal));
            Reject(() => Enhancements.Select(army, data, "char-0", Pick(upgrade)));
            Check(army.Entries.Single(e => e.Id == "char-0").Enhancement == Pick(normal), "Rejected upgrade replaced enhancement");
            army = Enhancements.Select(Enhancements.Select(army, data, "char-0", null), data, "char-0", Pick(upgrade));
            Reject(() => Enhancements.Select(army, data, "char-0", Pick(normal)));
        });
        test("Attached leaders and bodyguard share one enhancement slot in both selection orders", () =>
        {
            var leader = character with { Profile = character.Profile! with { Abilities = [new("Leader", "This model can be attached to the following units: Upgrade troops.")] } };
            var rules = data with { Units = [troops, leader, hero] };
            var army = Enhancements.Select(Army(), rules, "char-0", Pick(normal));
            army = Enhancements.Select(army, rules, "troop-0", Pick(upgrade));
            Reject(() => LeaderRules.Attach(army, rules, "char-0", "troop-0"));
            var invalid = army with { Entries = army.Entries.Select(e => e.Id == "char-0" ? e with { AttachedToId = "troop-0" } : e).ToList() };
            Check(ArmyCalculator.Calculate(invalid, rules).Issues.Any(i => i.Contains("across its bodyguard and leaders")), "Imported attachment conflict was not flagged");
            army = Enhancements.Select(army, rules, "troop-0", null);
            army = LeaderRules.Attach(army, rules, "char-0", "troop-0");
            Reject(() => Enhancements.Select(army, rules, "troop-0", Pick(upgrade)));
            army = Enhancements.Select(army, rules, "char-0", null);
            army = Enhancements.Select(army, rules, "troop-0", Pick(upgrade));
            Reject(() => Enhancements.Select(army, rules, "char-0", Pick(normal)));
            army = LeaderRules.Attach(army, rules, "char-0", "");
            army = Enhancements.Select(army, rules, "char-0", Pick(normal));
            Check(army.Entries.Count(e => e.Enhancement != null) == 2, "Detaching did not free independent enhancement slots");
        });
        test("Three identical upgrades share one choice and enhancement allowances enforce game size", () =>
        {
            var army = Army();
            for (var i = 0; i < 3; i++) army = Enhancements.Select(army, data, "troop-" + i, Pick(upgrade));
            Check(Enhancements.Usage(army, data) == (1, 3), "Upgrades did not share exactly one choice");
            Check(Enhancements.Budget(3000) == 6, "Provisional 3000-point allowance changed");
            Reject(() => Enhancements.Select(army, data, "troop-3", Pick(upgrade)));
            Reject(() => Enhancements.ValidateIncrease(army, army with { Entries = [.. army.Entries, army.Entries[0] with { Id = "duplicate" }] }, data));
            army = Enhancements.Select(army, data, "char-0", Pick(normal));
            Check(Enhancements.Usage(army, data) == (2, 3), "Normal enhancement did not use its own choice");
            Reject(() => Enhancements.Select(army, data, "char-1", Pick(normal)));
            army = army with { Limit = 2000 };
            army = Enhancements.Select(army, data, "char-1", Pick(normal));
            army = Enhancements.Select(army, data, "char-2", Pick(normal));
            Reject(() => Enhancements.Select(army, data, "char-3", Pick(normal)));
            Check(ArmyCalculator.Calculate(army with { Limit = 1000 }, data).Issues.Any(i => i.Contains("allowance exceeded")), "Changing game size did not flag over-allocation");
            var invalid = army with { Entries = army.Entries.Select(e => e.Id == "troop-3" ? e with { Enhancement = Pick(upgrade) } : e).ToList() };
            Check(Enhancements.ArmyIssues(invalid, data).Any(i => i.Contains("at most 3")), "Invalid imported upgrade count was not flagged");
            army = Enhancements.Select(army, data, "troop-0", null);
            Check(Enhancements.Usage(army, data) == (4, 2), "Removing one upgrade prematurely freed its shared choice");
            army = Enhancements.Select(army, data, "troop-1", null);
            army = Enhancements.Select(army, data, "troop-2", null);
            Check(Enhancements.Usage(army, data) == (3, 0), "Removing last upgrade did not free a choice");
        });
        test("Paid equipment quantities, free replacements, copy taxes and enhancements combine without double charging", () =>
        {
            var fixture = LoadoutChecks.Fixture(); var original = fixture.Units[0];
            var priced = original with { Options = [new(5, 100), new(5, 150, 2)],
                WargearCosts = original.WargearCosts!.Select(c => c.OptionId == "plasma" ? c with { Points = 15 } : c).ToList() };
            var dataset = fixture with { Units = [priced], ReferenceCards = [upgrade], Detachments = data.Detachments };
            var first = new ArmyEntry("a", priced.Key, priced.Name, 5, Loadout: Loadouts.Create(priced, 5));
            first = first with { Loadout = Loadouts.Choose(priced, first, 0, "ranged", "plasma", 1) };
            first = first with { Loadout = Loadouts.Choose(priced, first, 1, "ranged", "plasma", 1) };
            Check(WargearPricing.Cost(priced, first) == 30, "Two firing profiles or model quantities charged incorrectly");
            var army = ArmyList.New("Death Guard") with { Entries = [first, first with { Id = "b", Enhancement = Pick(upgrade) }] };
            var result = ArmyCalculator.Calculate(army, dataset);
            Check(result.Total == 325 && result.Entries[1].CopyTax == 50 && result.Entries[1].WargearPoints == 30, "Base, tax, wargear and upgrade did not add correctly");
            Check(ArmyCalculator.Calculate(army with { Entries = [army.Entries[1]] }, dataset).Total == 145, "Removing first copy did not remove tax");
            first = first with { Loadout = Loadouts.Choose(priced, first, 0, "ranged", "rifle", 1) };
            Check(WargearPricing.Cost(priced, first) == 15, "Free replacement retained paid weapon cost");
            var unknown = priced with { WargearCosts = priced.WargearCosts!.Select(c => c.OptionId == "plasma" ? c with { Points = null } : c).ToList() };
            Check(!ArmyCalculator.Calculate(army, dataset with { Units = [unknown] }).Complete, "Unknown selected equipment cost became zero");
            Check(WargearPricing.Cost(unknown, new("default", unknown.Key, unknown.Name, 5)) == 0, "Unselected unknown weapon made free defaults unpriceable");
            var merged = DatasetMerge.Prepare(fixture, dataset, ImportLayer.Points).Dataset;
            Check(DataJson.Write(merged.Units[0].WargearCosts) == DataJson.Write(priced.WargearCosts), "Points-only merge dropped equipment prices");
            var profilesOnly = DatasetMerge.Prepare(dataset, fixture, ImportLayer.Profiles).Dataset;
            Check(DataJson.Write(profilesOnly.Units[0].WargearCosts) == DataJson.Write(priced.WargearCosts), "Profile refresh changed equipment prices");
        });
        var index = Array.IndexOf(args, "--reference-catalogue");
        var referencePath = Path.Combine(root, "reference-fixture.json");
        if (File.Exists(referencePath)) test("Real upgrades respect named units and keywords granted by selected detachments", () =>
        {
            var rules = DataJson.Read<Dataset>(File.ReadAllText(referencePath));
            var army = ArmyList.New("Death Guard") with { Detachments = ["Contagion Engines", "Flyblown Host"] };
            List<ReferenceCard> Choices(string name) => Enhancements.Options(army, rules, new("Death Guard", name, [], Profile: new(Keywords: ["Vehicle"])));
            Check(Choices("Myphitic Blight-hauler").Count == 2, "Blight-hauler did not receive both Contagion Engine upgrades");
            Check(Choices("Helbrute").Count == 2, "Slash-separated Helbrute restriction was lost");
            Check(Choices("Foetid Bloat-drone").Count == 1, "Granted Contagion Engine keyword was ignored");
            Check(Choices("Defiler").Count == 0, "Contagion Engine keyword leaked to Defiler");
            Check(Choices("Plague Marines").Count == 2, "Plague Marines upgrades unavailable");
            Check(Enhancements.Options(army with { Detachments = ["Flyblown Host"] }, rules, new("Death Guard", "Helbrute", [])).Count == 0, "Removed detachment left upgrade eligibility active");
        });
        if (index >= 0 && index + 1 < args.Length) test("Real Defiler imports base price, second-copy tax and both paid weapon slots", () =>
        {
            var doc = BsDataDocument.Parse(args[index + 1], File.ReadAllText(args[index + 1]));
            doc.Root.Remove("selectionEntries");
            doc.Root["entryLinks"] = new JsonArray(doc.Root["entryLinks"]!.AsArray().Single(n => n?["name"]?.ToString() == "Defiler")!.DeepClone());
            var imported = BsDataImporter.Import([doc], source).Dataset;
            var defiler = imported.Units.Single();
            Check(defiler.Options.SequenceEqual(new[] { new PointOption(1, 300), new PointOption(1, 350, 2) }), "Defiler base/tax import failed: " + defiler.PointsWarning);
            var entry = new ArmyEntry("def-0", defiler.Key, defiler.Name, 1, Loadout: Loadouts.Create(defiler, 1));
            Check(WargearPricing.Cost(defiler, entry) == 0, "Defiler defaults have unexpected cost");
            var groups = defiler.Profile!.Loadout!.Roles.Single().Groups.Where(g => g.Options.Any(o => o.Name == "Hades lascannon")).ToList();
            Check(groups.Count == 2, "Defiler weapon replacement slots missing");
            foreach (var group in groups) entry = entry with { Loadout = Loadouts.Choose(defiler, entry, 0, group.Id, group.Options.Single(o => o.Name == "Hades lascannon").Id, 1) };
            var army = ArmyList.New("Death Guard") with { Entries = [entry, entry with { Id = "def-1" }, entry with { Id = "def-2" }] };
            var result = ArmyCalculator.Calculate(army, imported);
            Check(result.Complete && result.Total == 1090 && result.Entries.Select(e => e.Points).SequenceEqual(new int?[] { 330, 380, 380 }), "Real Defiler paid slots/tax combined incorrectly");
            var refreshed = BsDataImporter.Reprice(imported with { Units = [defiler with { Options = [], WargearCosts = null }] }, doc, source).Dataset;
            Check(ArmyCalculator.Calculate(army, refreshed).Total == 1090, "Pricing refresh failed to repair existing Defiler");
            File.WriteAllText(Path.Combine(root, "pricing-fixture.json"), DataJson.Write(imported));
            doc.Root["entryLinks"]![0]!["modifiers"] = JsonNode.Parse("""[{"field":"51b2-306e-1021-d207","type":"increment","value":5}]""");
            var linked = BsDataImporter.Reprice(imported, doc, source).Dataset.Units.Single();
            Check(linked.Options[0].Points == 305 && linked.Options[1].Points == 355, "Pricing refresh ignored entry-link price modifiers");
            doc.Root["entryLinks"]![0]!.AsObject().Remove("modifiers");
            var gun = doc.Root["sharedSelectionEntries"]!.AsArray().Single(n => n?["name"]?.ToString() == "Hades lascannon")!;
            gun["modifiers"] = JsonNode.Parse("""[{"field":"51b2-306e-1021-d207","type":"increment","value":5,"conditions":[{"field":"selections","scope":"roster","childId":"unsupported","type":"atLeast","value":1}]}]""");
            var conditional = BsDataImporter.Import([doc], source).Dataset;
            Check(conditional.Units[0].Options.Count == 2 && !ArmyCalculator.Calculate(army, conditional).Complete, "Conditional selected equipment price was silently treated as a fixed cost");
            var defaultArmy = army with { Entries = [entry with { Loadout = Loadouts.Create(conditional.Units[0], 1) }] };
            Check(ArmyCalculator.Calculate(defaultArmy, conditional).Total == 300 && ArmyCalculator.Calculate(defaultArmy, conditional).Complete, "Unselected conditional equipment blocked valid defaults");
        });
        File.WriteAllText(Path.Combine(root, "upgrade-fixture.json"), DataJson.Write(data));
    }
}
