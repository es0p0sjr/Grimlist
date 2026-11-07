using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;

// Same isolated transaction/cache checks on both native adapters.
public static class CatalogueWorkflowWorkload
{
    public static List<string> Run(string directory, ISqliteConnectionFactory factory)
    {
        List<string> checks = [];
        void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); checks.Add(message); }
        using var store = new ArmyStore(directory, factory);
        var unit = new UnitDefinition("Fixture", "Unit", [new(1, 100)], Profile: new(Characteristics: new() { ["OC"] = "1" }));
        var data = new Dataset(new("Synthetic catalogue transaction check", "1", "", "", Edition: "10"), [unit]);
        store.Import(data);
        var army = ArmyList.New("Fixture") with { Entries = [new("fixture-entry", unit.Key, unit.Name, 1)] };
        store.Save(army, new(army.Id, "fixture-entry"));
        var workflow = new LibraryWorkflow(store);
        workflow.Apply(workflow.Prepare(data, ImportLayer.Combined));
        var first = new ArmySession(store, useFactionLoading: true);
        Check(first.FactionLoading!.FullReads == 0, "First open uses the prepared projection");
        var firstPath = Path.Combine(directory, "derived-cache", "factions", first.FactionLoading.CacheIdentity + ".json.gz");
        var revision = store.LibraryRevision(); var sourceBefore = DataJson.Write(store.LoadDataset());
        using (var other = new ArmyStore(directory, factory))
        {
            try
            {
                store.ApplyPreparedImport(data, null, revision, loaded => {
                    Check(other.LibraryRevision() == revision, "Another connection sees only the committed revision");
                    throw new InvalidDataException("Injected projection rejection");
                });
                throw new Exception("Rejected preparation committed");
            }
            catch (InvalidDataException error) when (error.Message == "Injected projection rejection") { }
        }
        Check(store.LibraryRevision() == revision && DataJson.Write(store.LoadDataset()) == sourceBefore, "Rejected projection rolls back source content and revision");
        for (var i = 0; i < 4; i++)
        {
            var next = data with { Source = data.Source with { Version = "revision-" + i }, Units = [unit with { Options = [new(1, 110 + i)] }] };
            workflow.Apply(workflow.Prepare(next, ImportLayer.Combined));
            _ = new ArmySession(store, useFactionLoading: true);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var last = new ArmySession(store, useFactionLoading: true);
        Check(first.DatasetForArmy(army)!.Units.Single().Options.Single().Points == 100 && File.Exists(firstPath), "Active old reader survives multiple imports and collection");
        var markers = Directory.GetFiles(Path.GetDirectoryName(firstPath)!, "*.lease");
        Check(markers.Length <= 3, "Retention keeps two recent manifests plus the active old reader");
        Check(last.DatasetForArmy(army)!.Units.Single().Options.Single().Points == 113, "New reader sees current content");
        Check(store.LoadContext().ArmyId == army.Id && store.LoadArmies().Single().Entries.Single().Id == "fixture-entry", "Imports preserve saved entry IDs and editor context");
        GC.KeepAlive(first); GC.KeepAlive(last); return checks;
    }
}
