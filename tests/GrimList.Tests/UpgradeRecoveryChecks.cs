using GrimList.Application;
using GrimList.Core;

internal static class UpgradeRecoveryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Legacy libraries recover every source upgrade without requiring a Character or a raw cache", () =>
        {
            var original = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/libraries/GrimList-Library.json"));
            var archive = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var sourceCards = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(File.ReadAllBytes(archive)), original.Source);
            var expected = EnhancementLibraryRepair.Apply(original, sourceCards);
            var repaired = EnhancementLibraryRepair.ApplyBundled(original);
            var expectedUpgrades = expected.ReferenceCards!.Where(c => c.IsUpgrade).ToList();
            Check(expectedUpgrades.Count > 100, "Real source fixture did not exercise all-faction upgrades");
            foreach (var card in expectedUpgrades)
            {
                var actual = repaired.ReferenceCards!.Single(c => c.Faction == card.Faction && c.Section == card.Section && c.Name == card.Name && c.Detachment == card.Detachment);
                Check(actual.IsUpgrade && DataJson.Write(actual.Eligibility) == DataJson.Write(card.Eligibility), "Upgrade metadata missing: " + card.Faction + "/" + card.Name);
            }
            Check(ReferenceEquals(repaired, EnhancementLibraryRepair.ApplyBundled(repaired)), "Repair is not idempotent");
            Check(ReferenceEquals(original.Units, repaired.Units), "Repair changed source profiles");
            var unit = original.Units.Single(u => u.Faction == "Necrons" && u.Name == "Lokhust Destroyers");
            var gift = repaired.ReferenceCards!.Single(c => c.Faction == unit.Faction && c.Name == "Deepening Madness");
            var army = ArmyList.New(unit.Faction) with { Detachment = gift.Detachment, Entries = [new("destroyers", unit.Key, unit.Name, 3)] };
            Check(!Enhancements.Character(unit) && Enhancements.Eligible(army, unit, gift, repaired), "Non-character unit cannot equip its upgrade");
            Check(!Enhancements.Eligible(army with { Detachment = "Other" }, unit, gift, repaired), "Wrong detachment accepted");
            Check(!Enhancements.Eligible(army, unit, gift with { IsUpgrade = false }, repaired), "Ordinary enhancement bypassed Character restriction");
            using (var imported = WindowsServices.OpenStore(Path.Combine(root, "legacy-upgrade-preview")))
            {
                var workflow = new LibraryWorkflow(imported);
                var preview = workflow.Prepare(original with { Units = [unit], ReferenceCards = [original.ReferenceCards!.Single(c => c.Faction == unit.Faction && c.Name == gift.Name)] }, ImportLayer.Combined);
                Check(preview.Dataset.ReferenceCards!.Single(c => c.Section == "Enhancements").IsUpgrade, "Import preview lost upgrade classification");
                Check(preview.Dataset.ReferenceCards!.Any(c => c.Section == "Army Rules" && c.Faction == unit.Faction), "Import preview omitted source-backed army rules");
                workflow.Apply(preview);
                var installed = imported.LoadDataset()!;
                Check(ReferenceEquals(installed, EnhancementLibraryRepair.Restore(imported, installed)), "Fresh import required a second repair");
            }
            using var store = WindowsServices.OpenStore(Path.Combine(root, "legacy-upgrades-no-cache"));
            store.Import(original); store.Save(army);
            var saved = DataJson.Write(store.LoadArmies());
            var session = new ArmySession(store);
            Check(DataJson.Write(store.LoadArmies()) == saved, "Automatic repair modified saved armies");
            Check(store.LoadDataset()!.ReferenceCards!.Single(c => c.Faction == unit.Faction && c.Name == gift.Name).IsUpgrade, "Repair was not persisted");
            session.Open(army);
            var before = session.Result.Total;
            session.SelectEnhancement("destroyers", new(gift.Detachment, gift.Name));
            Check(session.Result.Total == before + Enhancements.Cost(gift), "Upgrade selection or pricing failed in shared Windows/Android session");
            session.Undo(); Check(session.Army.Entries[0].Enhancement == null, "Undo did not remove upgrade");
            session.Redo(); Check(session.Army.Entries[0].Enhancement?.Name == gift.Name, "Redo did not restore upgrade");
            var reopened = new ArmySession(store); reopened.Restore();
            Check(reopened.Army.Entries[0].Enhancement?.Name == gift.Name, "Reopen lost upgrade");
        });
        test("Recovering an upgrade flag preserves existing eligibility and requires exact source evidence", () =>
        {
            var source = new SourceInfo("BSData fixture", "1", "", "", Edition: "11", ContentHash: "revision");
            var restricted = new EnhancementCondition("keyword", "Infantry", "instanceOf");
            var card = new ReferenceCard("Test", "Enhancements", "Gift", "This unit gains an ability.", "Host", "10 pts", source, Eligibility: restricted);
            var data = new Dataset(source, [], ReferenceCards: [card]);
            var recovered = card with { IsUpgrade = true, Eligibility = restricted };
            var repaired = EnhancementLibraryRepair.Apply(data, [recovered]);
            Check(repaired.ReferenceCards![0].IsUpgrade && repaired.ReferenceCards[0].Eligibility == restricted, "Classification repair overwrote an existing restriction");
            Check(ReferenceEquals(data, EnhancementLibraryRepair.Apply(data, [recovered with { Source = source with { ContentHash = "other" } }])), "Different source repaired");
            Check(EnhancementLibraryRepair.Apply(data, [recovered with { Text = "Changed description" }]).ReferenceCards![0].IsUpgrade, "Description blocked classification recovery");
            Check(EnhancementLibraryRepair.Apply(data, [recovered with { Cost = "20 pts" }]).ReferenceCards![0] is { IsUpgrade: true, Cost: "10 pts" }, "Price blocked recovery or was overwritten");
        });
        test("Adding older recovery packs retains newer revisions and repairs classification independently of eligibility", () =>
        {
            var original = DataJson.Read<Dataset>(File.ReadAllText("tests/fixtures/bsdata/11e/da7248777da54218034f09b6271ad89ff6b68a77/reimported.json"));
            var repaired = EnhancementLibraryRepair.ApplyBundled(original);
            var upgrades = repaired.ReferenceCards!.Where(c => c.IsUpgrade).ToList();
            Check(upgrades.Count > 100, "Newer revision lost upgrade recovery");
            var classifiedLater = repaired with { ReferenceCards = upgrades.Select(c => c with { IsUpgrade = false }).ToList() };
            var restored = EnhancementLibraryRepair.ApplyBundled(classifiedLater);
            Check(restored.ReferenceCards!.All(c => c.IsUpgrade), "Existing eligibility prevented upgrade classification recovery");
            Check(DataJson.Write(restored.ReferenceCards!.Select(c => c.Eligibility)) == DataJson.Write(upgrades.Select(c => c.Eligibility)), "Existing eligibility changed");
            var otherSource = original with { ReferenceCards = original.ReferenceCards!.Select(c => c with { Source = c.Source! with { ContentHash = "unknown" } }).ToList() };
            Check(ReferenceEquals(otherSource, EnhancementLibraryRepair.ApplyBundled(otherSource)), "Bundled repair guessed classifications for an unknown source");
        });
    }
}
