using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class PerformanceChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Presentation value retains identity and only publishes changed values", () =>
        {
            var state = new ObservableValue<int>(0); var same = state; var calls = 0;
            state.PropertyChanged += (_, e) => { Check(e.PropertyName == "Value", "Wrong binding notification"); calls++; };
            state.Update(0); state.Update(1); state.Update(1);
            Check(ReferenceEquals(same, state) && calls == 1 && same.Value == 1, "Projection was replaced or over-notified");
        });
        test("Paid equipment refreshes remaining-points filter without rescanning source eligibility", () =>
        {
            var data = LoadoutChecks.Fixture(); var unit = data.Units[0];
            var costs = unit.Profile!.Loadout!.Roles.SelectMany(r => r.Groups).Concat(unit.Profile.Loadout.UnitGroups)
                .SelectMany(g => g.Options).DistinctBy(o => o.Id).Select(o => new EquipmentCost(o.Id, o.Id == "plasma" ? 10 : 0)).ToList();
            unit = unit with { WargearCosts = costs }; data = data with { Units = [unit] };
            using var store = WindowsServices.OpenStore(Path.Combine(root, "fits-equipment-cache")); store.Import(data);
            var session = new ArmySession(store); unit = session.Dataset!.Units[0]; session.Create(unit.Faction, "Fits", 2000); session.AddUnit(unit.Key, 5);
            var normal = session.BrowseLibrary(new()); var fits = session.BrowseLibrary(new(FitsRemaining: true)); var builds = session.EligibilityBuildCount;
            var before = session.Result.Total; var role = unit.Profile!.Loadout!.Roles.Single(r => r.Id == session.SelectedEntry!.Loadout!.Models[0].RoleId);
            var group = role.Groups.Single(g => g.Options.Any(o => o.Id == "plasma"));
            session.ChooseEquipment(session.SelectedEntry!.Id, 0, group.Id, "plasma", 1);
            Check(session.Result.Total == before + 10, "Paid fixture did not exercise points change");
            Check(ReferenceEquals(normal, session.BrowseLibrary(new())), "Unfiltered rows were invalidated");
            Check(!ReferenceEquals(fits, session.BrowseLibrary(new(FitsRemaining: true))), "Fits filter reused stale remaining points");
            Check(session.EligibilityBuildCount == builds, "Paid equipment rescanned all catalogue eligibility");
        });
        test("Army persistence performs no dataset read, even without a readable library snapshot", () =>
        {
            var factory = new CountingFactory();
            using var store = new ArmyStore(Path.Combine(root, "save-no-library"), factory);
            var army = ArmyList.New("Fixture") with { Name = "Durable save" };
            factory.Connection!.RejectDatasetReads = true;
            store.Save(army, new(army.Id));
            Check(store.LoadArmies().Single().Id == army.Id && store.LoadContext().ArmyId == army.Id, "Army/context not persisted");
        });
        test("Equipment command retains dataset and availability identity and calculates once", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "edit-work-counts"));
            store.Import(LoadoutChecks.Fixture()); var repository = new CountingRepository(store);
            var calls = 0;
            var session = new ArmySession(repository, (army, data) => { calls++; return ArmyCalculator.Calculate(army, data); });
            var unit = session.Dataset!.Units.First(); session.Create(unit.Faction, "Edit", 2000);
            session.AddUnit(unit.Key, unit.Options.First().Models);
            var snapshot = session.Snapshot; var rows = session.BrowseLibrary(new());
            var builds = session.EligibilityBuildCount; var projections = session.LibraryBuildCount;
            repository.Loads = 0; calls = 0;
            var referenceReads = ReferenceSourceReader.ReadCount; var referenceParses = ReferenceSourceReader.ParseCount;
            var referenceMerges = ReferenceMerge.EvaluationCount; var referenceIndex = snapshot!.References;
            session.ResetLoadout(session.SelectedEntry!.Id);
            Check(referenceReads == ReferenceSourceReader.ReadCount && referenceParses == ReferenceSourceReader.ParseCount
                && referenceMerges == ReferenceMerge.EvaluationCount && ReferenceEquals(referenceIndex, session.Snapshot!.References),
                "Equipment edit parsed, merged, read or rebuilt reference content");
            Check(calls == 1 && repository.Loads == 0, "Edit recalculated or loaded dataset redundantly");
            Check(ReferenceEquals(snapshot, session.Snapshot) && ReferenceEquals(rows, session.BrowseLibrary(new())), "Equipment invalidated snapshot/library rows");
            Check(builds == session.EligibilityBuildCount && projections == session.LibraryBuildCount, "Equipment rebuilt availability");
            Check(session.Mutation.Changes.HasFlag(ArmyChange.Equipment), "Equipment change metadata missing");
            session.AddUnit(unit.Key, unit.Options.First().Models);
            Check(!ReferenceEquals(rows, session.BrowseLibrary(new())), "Add did not refresh copy/limit rows");
            Check(builds == session.EligibilityBuildCount, "Add unnecessarily rescanned source eligibility");
            session.RemoveUnit(session.SelectedEntry!.Id); session.BrowseLibrary(new());
            Check(session.LibraryBuildCount == projections + 2, "Remove did not refresh limits");
            session.Commit(session.Army with { Faction = "Other faction" }); session.BrowseLibrary(new());
            Check(session.EligibilityBuildCount == builds + 1, "Faction change reused wrong eligibility");
        });
        test("Dataset replacement rebuilds identity indexes without stale record-copy facts", () =>
        {
            var first = LoadoutChecks.Fixture(); var unit = first.Units[0]; var snapshot = DatasetSnapshot.For(first);
            var replacement = unit with { Name = "Renamed indexed unit", Id = "new-identity", Profile = unit.Profile! with { Keywords = ["Character"] } };
            var next = first with { Units = [replacement] };
            Check(!ReferenceEquals(snapshot, DatasetSnapshot.For(next)), "Source indexes reused");
            Check(next.FindUnit(replacement.Key) == replacement && next.FindUnit(unit.Key) == null, "Stale identities retained");
            Check(replacement.NameKey != unit.NameKey && AlliedRules.Has(replacement, "Character"), "Record copy reused static facts");
        });
        test("Failed durable commit does not publish mutation, history or calculated state", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "failed-performance-save")); store.Import(LoadoutChecks.Fixture());
            var repository = new CountingRepository(store); var session = new ArmySession(repository);
            var unit = session.Dataset!.Units[0]; session.Create(unit.Faction, "Failure", 2000); session.AddUnit(unit.Key, unit.Options[0].Models);
            var army = session.Army; var result = session.Result; var mutation = session.Mutation; var rows = session.BrowseLibrary(new());
            repository.Fail = true;
            try { session.ResetLoadout(session.SelectedEntry!.Id); throw new Exception("Save unexpectedly succeeded"); } catch (IOException) { }
            Check(ReferenceEquals(army, session.Army) && ReferenceEquals(result, session.Result) && mutation == session.Mutation, "Failure published state");
            Check(ReferenceEquals(rows, session.BrowseLibrary(new())), "Failure invalidated rows");
            repository.Fail = false; session.Undo(); Check(session.Army.Entries.Count == 0, "Failed command changed undo history");
        });
        test("Indexed session restores durable army/context and session-local history after recreation", () =>
        {
            var path = Path.Combine(root, "indexed-recreation"); string id;
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(LoadoutChecks.Fixture()); var session = new ArmySession(store); var unit = session.Dataset!.Units[0];
                session.Create(unit.Faction, "Restore", 2000); session.AddUnit(unit.Key, unit.Options[0].Models);
                session.SelectEntry(session.SelectedEntry!.Id, 0, "equipment"); session.Navigate("unit"); id = session.Army.Id;
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                var repository = new CountingRepository(store); var session = new ArmySession(repository); session.Restore();
                Check(repository.Loads == 1, "Workspace startup loaded the same dataset twice");
                Check(session.Army.Id == id && session.Context.LoadoutSection == "equipment" && session.Context.Screen == "unit", "Context lost");
                Check(!session.CanUndo && !session.CanRedo, "History persisted across recreation");
                var previous = session.Snapshot; session.ReloadLibrary();
                Check(!ReferenceEquals(previous, session.Snapshot), "Explicit source reload failed to replace snapshot");
            }
        });
    }
    private sealed class CountingRepository(IArmyRepository inner) : IArmyRepository
    {
        public int Loads; public bool Fail;
        public string DirectoryPath => inner.DirectoryPath;
        public Dataset? LoadDataset() { Loads++; return inner.LoadDataset(); }
        public List<ArmyList> LoadArmies() => inner.LoadArmies();
        public void Save(ArmyList army, EditorContext? context = null) { if (Fail) throw new IOException("Injected"); inner.Save(army, context); }
        public void SaveContext(EditorContext context) => inner.SaveContext(context);
        public EditorContext LoadContext() => inner.LoadContext();
        public void Delete(string id) => inner.Delete(id);
        public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => inner.PreviewImport(data, layer, completeFactions);
        public void ApplyImport(Dataset data, string? expectedSnapshot = null) => inner.ApplyImport(data, expectedSnapshot);
        public void Dispose() { }
    }
    private sealed class CountingFactory : ISqliteConnectionFactory
    {
        public CountingConnection? Connection;
        public ISqliteConnection Open(string path) => Connection = new(new WindowsSqliteConnectionFactory().Open(path));
    }
    private sealed class CountingConnection(ISqliteConnection inner) : ISqliteConnection
    {
        public bool RejectDatasetReads;
        public List<string?[]> Run(string sql, params object?[] arguments)
        {
            if (RejectDatasetReads && sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && (sql.Contains("active_source") || sql.Contains("dataset_snapshots") || sql.Contains("source_details"))) throw new Exception("Save accessed imported library");
            return inner.Run(sql, arguments);
        }
        public void Transaction(Action action) => inner.Transaction(action);
        public void Dispose() => inner.Dispose();
    }
}
