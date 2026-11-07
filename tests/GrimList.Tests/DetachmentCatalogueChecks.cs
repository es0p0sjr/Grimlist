using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class DetachmentCatalogueChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dataset Fixture() => new(new("BSData fixture", "old", "2026-10-02", "https://example.test", Edition: "11"),
        [new("Space Marines", "Test marine", [new(5, 100)], Profile: new(new Dictionary<string, string> { ["T"] = "5" }))],
        Detachments: [new("Space Marines", "Anvil Siege Force", Cost: "2DP"), new("Space Marines", "Gladius Task Force", Cost: "3DP"),
            new("Space Marines", "Future Force", Cost: "1DP"), new("Space Marines", "Deathwatch Support (ITS NOT READY)")],
        ReferenceCards: [new("Space Marines", "Enhancements", "Old upgrade", "CHARACTER model only. Old effect.", "Anvil Siege Force", "10"),
            new("Space Marines", "Stratagems", "Old tactic", "Old text", "Anvil Siege Force"),
            new("Space Marines", "Army Rules", "Keep army rule", "Keep text"),
            new("Other faction", "Stratagems", "Keep unrelated", "Keep text", "Anvil Siege Force")]);
    public static void Run(Action<string, Action> test, string root)
    {
        test("Retired detachments and their cards are removed without touching future, Deathwatch or other factions", () => {
            var data = Fixture(); var before = DataJson.Write(data); var updated = DetachmentCatalogue.Apply(data);
            Check(updated.Detachments!.Select(d => d.Name).SequenceEqual(["Gladius Task Force", "Future Force", "Deathwatch Support (ITS NOT READY)"]), "Wrong retirements");
            Check(updated.ReferenceCards!.Select(c => c.Name).SequenceEqual(["Keep army rule", "Keep unrelated"]), "Wrong cards removed");
            Check(DataJson.Write(data) == before && ReferenceEquals(updated, DetachmentCatalogue.Apply(updated)), "Mutation or non-idempotent repair");
            var ten = data with { Source = data.Source with { Edition = "10" } };
            Check(ReferenceEquals(ten, DetachmentCatalogue.Apply(ten)), "10e changed");
            var other = data with { Game = "Other game" };
            Check(ReferenceEquals(other, DetachmentCatalogue.Apply(other)), "Other game changed");
        });
        test("Shared reference cards retain surviving detachment scopes", () => {
            var shared = new ReferenceCard("Space Marines", "Stratagems", "Shared", "Shared text", Details: new(Detachments: [new("Anvil Siege Force"), new("Gladius Task Force")]));
            var updated = DetachmentCatalogue.Apply(Fixture() with { ReferenceCards = [shared] });
            Check(updated.ReferenceCards!.Single().Details!.Detachments!.Single().Name == "Gladius Task Force", "Shared rule lost or retired target retained");
            Check(ReferenceEquals(updated, DetachmentCatalogue.Apply(updated)), "Shared rule changes repeatedly");
        });
        test("Bundled real catalogue gains current definitions with Chapter restrictions and leaves placeholder absent", () => {
            var original = BootstrapRelease.ReadBundled().Dataset;
            var updated = DetachmentCatalogue.Apply(original);
            var marines = updated.Detachments!.Where(d => d.Faction == "Space Marines").ToList();
            Check(marines.Count == 34 && marines.All(d => !DetachmentCatalogue.IsRetired(updated, d.Faction, d.Name)), "Wrong current catalogue count");
            Check(marines.Any(d => d.Name == "Assault Brethren") && marines.Any(d => d.Name == "Medusa's Wrath"), "New options missing");
            Check(DetachmentRules.Options("Ultramarines", updated).Any(d => d.Name == "Assault Brethren"), "Core option unavailable");
            Check(!DetachmentRules.Options("Ultramarines", updated).Any(d => d.Name == "Vow-Sworn Crusaders"), "Chapter restriction lost");
            Check(DataJson.Write(updated.Units) == DataJson.Write(original.Units), "Unit/point data changed");
            Check(updated.ReferenceCards!.Any(c => c.Detachment == "Assault Brethren" && c.Section == "Detachments"), "New definition lacks available source rules");
            var future = updated with { Detachments = updated.Detachments!.Select(d => d.Name == "Assault Brethren" ? d with { Cost = "2DP", Source = d.Source! with { Date = "2026-12-01" } } : d).ToList() };
            Check(DetachmentCatalogue.Apply(future).Detachments!.Where(d => d.Name == "Assault Brethren").All(d => d.Cost == "2DP"), "Newer metadata overwritten");
        });
        test("Android reference recovery cannot restore retired detachments or stratagems", () => {
            var data = Fixture(); var pack = data with { Units = [], ReferenceOnly = true };
            var updated = ReferenceLibraryRecovery.Supplement(data, pack);
            Check(updated.Detachments!.All(d => d.Name != "Anvil Siege Force"), "Android restored retired definition");
            Check(updated.ReferenceCards!.All(c => c.Faction != "Space Marines" || c.Detachment != "Anvil Siege Force"), "Android restored retired rule");
        });
        test("Catalogue and points imports, export and SQLite restart retain retirement and saved army", () => {
            var path = Path.Combine(root, "detachment-catalogue");
            var data = Fixture(); var army = ArmyList.New("Space Marines") with { Id = "retired-army", Detachment = "Anvil Siege Force" };
            using (var store = WindowsServices.OpenStore(path)) {
                store.ApplyImport(data); store.Save(army);
                // Simulate an actual pre-update persisted JSON snapshot, bypassing
                // today's writer. Startup must repair it without a manual reimport.
                using (var db = new WindowsSqliteConnectionFactory().Open(Path.Combine(path, "grimlist.db")))
                    db.Run("UPDATE dataset_snapshots SET content=? WHERE source_id=(SELECT value FROM settings WHERE key='active_source')", DataJson.Write(data));
                Check(store.LoadDataset()!.Detachments!.All(d => d.Name != "Anvil Siege Force"), "Legacy stored snapshot was not repaired on read");
                var flow = new LibraryWorkflow(store);
                foreach (var layer in new[] { ImportLayer.Profiles, ImportLayer.Points, ImportLayer.Combined }) flow.Apply(flow.Prepare(data, layer));
                var current = store.LoadDataset()!;
                Check(current.Detachments!.All(d => d.Name != "Anvil Siege Force"), "Import restored retired option");
                Check(DataJson.Write(store.LoadArmies().Single()) == DataJson.Write(army), "Army was rewritten");
                Check(DetachmentRules.Issues(army, current).Any(s => s.Contains("retired after")), "No review diagnostic");
            }
            using var reopened = WindowsServices.OpenStore(path);
            var exported = DataJson.Read<Dataset>(DataJson.Write(ReferenceModel.ExportDataset(reopened.LoadDataset()!)));
            Check(exported.Detachments!.All(d => d.Name != "Anvil Siege Force") && reopened.LoadArmies().Single().Detachment == "Anvil Siege Force", "Restart/export changed result");
        });
    }
}
