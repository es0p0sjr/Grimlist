using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class ImportStorageChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid library operation accepted."); }
    private static Dataset Large() => LoadoutChecks.Fixture() with
    {
        ReferenceCards = [new("Death Guard", "Army Rules", "Storage fixture",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(1_000_000)) + " Ελληνικά 🛡️ 中文")]
    };

    public static void Run(Action<string, Action> test, string root)
    {
        test("Large legacy snapshots migrate through bounded database values, preserving library, armies and context", () =>
        {
            var path = Path.Combine(root, "import-storage-legacy");
            var factory = new BoundedFactory();
            var data = Large(); string expected; string armyJson; EditorContext context;
            using (var store = new ArmyStore(path, factory))
            {
                store.Import(data);
                var current = store.LoadDataset()!;
                var army = ArmyList.New("Death Guard") with { Name = "Keep me", Entries = [new("entry", current.Units[0].Key, current.Units[0].Name, 5)] };
                context = new(army.Id, "entry"); store.Save(army, context);
                armyJson = DataJson.Write(store.LoadArmies());
                // Simulate a v1 database from before this fix. Only fixture setup
                // bypasses the bridge-size guard, including literal UTF-8 characters.
                using (var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db")))
                {
                    sql.Run("UPDATE dataset_snapshots SET content=?", JsonSerializer.Serialize(current,
                        new JsonSerializerOptions(DataJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    sql.Run("DELETE FROM dataset_chunks");
                    sql.Run("PRAGMA user_version=1");
                }
                Check(DataJson.Write(store.LoadDataset()) == DataJson.Write(current), "Legacy JSON changed while streaming.");
                var workflow = new LibraryWorkflow(store);
                var preview = workflow.Prepare(data, ImportLayer.Combined);
                Check(preview.BaseRevision != null && preview.BaseSnapshot.Length == 0, "Preview retained full-library JSON.");
                Check(DataJson.Write(store.LoadDataset()) == DataJson.Write(current), "Preview changed the library.");
                workflow.Apply(preview);
                expected = DataJson.Write(store.LoadDataset());
                Check(store.LoadDataset()!.ReferenceCards!.Single().Text == data.ReferenceCards!.Single().Text, "Large Unicode reference lost.");
                Reject(() => workflow.Apply(preview));
            }
            using var reopened = new ArmyStore(path, factory);
            Check(DataJson.Write(reopened.LoadDataset()) == expected, "Snapshot changed after reopening.");
            Check(DataJson.Write(reopened.LoadArmies()) == armyJson && reopened.LoadContext() == context, "Roster/context changed.");
            using var database = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            Check(database.Run("SELECT count(*) FROM source_details").Single()[0] == "0", "Duplicate profile snapshot stored.");
            Check(factory.LargestArgument <= 65536 && factory.LargestResult <= 524288, "Large JSON crossed the database bridge.");
        });
        test("Chunk write failure rolls back library, revision, source rows and saved armies atomically", () =>
        {
            var path = Path.Combine(root, "import-storage-rollback");
            using var store = new ArmyStore(path, new BoundedFactory()); store.Import(LoadoutChecks.Fixture());
            store.Save(ArmyList.New("Death Guard"));
            var before = DataJson.Write(store.LoadDataset()); var revision = store.LibraryRevision(); var armies = DataJson.Write(store.LoadArmies());
            var workflow = new LibraryWorkflow(store); var preview = workflow.Prepare(Large(), ImportLayer.Combined);
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            var chunks = sql.Run("SELECT count(*) FROM catalogue_chunks").Single()[0];
            sql.Run("CREATE TRIGGER fail_chunk BEFORE INSERT ON catalogue_chunks WHEN NEW.position=1 BEGIN SELECT RAISE(ABORT,'injected write failure'); END");
            try { workflow.Apply(preview); throw new Exception("Failure injection did not run."); } catch (IOException) { }
            Check(DataJson.Write(store.LoadDataset()) == before && store.LibraryRevision() == revision, "Partial library published.");
            Check(DataJson.Write(store.LoadArmies()) == armies, "Failure changed saved armies.");
            Check(sql.Run("SELECT count(*) FROM sources").Single()[0] == "1" && sql.Run("SELECT count(*) FROM catalogue_chunks").Single()[0] == chunks, "Failed import left records.");
            sql.Run("DROP TRIGGER fail_chunk"); workflow.Apply(preview);
            Check(store.LibraryRevision() != revision, "Retry did not commit.");
        });
        test("Revision guard accepts roster edits but rejects concurrent library and chunk edits", () =>
        {
            var path = Path.Combine(root, "import-storage-revision");
            using var store = new ArmyStore(path, new BoundedFactory()); store.Import(LoadoutChecks.Fixture());
            var workflow = new LibraryWorkflow(store);
            var preview = workflow.Prepare(LoadoutChecks.Fixture(), ImportLayer.Combined);
            store.Save(ArmyList.New("Death Guard")); workflow.Apply(preview);
            preview = workflow.Prepare(LoadoutChecks.Fixture(), ImportLayer.Combined);
            using var other = new ArmyStore(path, new WindowsSqliteConnectionFactory());
            other.Import(LoadoutChecks.Fixture()); Reject(() => workflow.Apply(preview));
            preview = workflow.Prepare(LoadoutChecks.Fixture(), ImportLayer.Combined);
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            sql.Run("UPDATE catalogue_chunks SET content=content"); Reject(() => workflow.Apply(preview));
        });
        test("A post-commit reload failure reports that the update was saved", () =>
        {
            using var store = new ArmyStore(Path.Combine(root, "import-storage-reload-error"), new WindowsSqliteConnectionFactory());
            store.Import(LoadoutChecks.Fixture());
            var repository = new ReloadFailureRepository(store);
            using var workspace = new ArmyWorkspace(repository);
            var incoming = LoadoutChecks.Fixture() with { Source = LoadoutChecks.Fixture().Source with { Version = "committed-update" } };
            var preview = workspace.Library.Prepare(incoming, ImportLayer.Combined);
            Check(preview.BaseRevision == null && preview.BaseSnapshot.Length > 0, "Repository without revisions lost its snapshot guard.");
            try { workspace.ApplyLibraryAsync(preview).GetAwaiter().GetResult(); throw new Exception("Reload failure did not propagate."); }
            catch (LibraryRefreshException error) { Check(error.InnerException is IOException, "Original reload error lost."); }
            Check(store.LoadDataset()!.Source.Version == "committed-update", "Committed update was reported as rolled back.");
        });
        foreach (var corruption in new[] { "missing", "truncated", "checksum", "extra", "oversized" })
            test("Corrupt snapshot fails closed: " + corruption, () =>
            {
                var path = Path.Combine(root, "import-storage-" + corruption);
                using var store = new ArmyStore(path, new BoundedFactory()); store.Import(Large());
                using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
                if (corruption == "missing") sql.Run("DELETE FROM catalogue_chunks WHERE position=0");
                if (corruption == "truncated") sql.Run("UPDATE catalogue_chunks SET content=substr(content,1,length(content)-4) WHERE position=0");
                if (corruption == "checksum") sql.Run("UPDATE dataset_snapshots SET content=substr(content,1,length(content)-64)||?", new string('0', 64));
                if (corruption == "extra") sql.Run("INSERT INTO catalogue_chunks SELECT hash,999999,x'00' FROM catalogue_objects");
                if (corruption == "oversized") sql.Run("UPDATE catalogue_chunks SET content=? WHERE position=0", new string('A', 1_000_000));
                Reject(() => store.LoadDataset());
            });
    }

    private sealed class BoundedFactory : ISqliteConnectionFactory
    {
        public int LargestArgument, LargestResult;
        public ISqliteConnection Open(string path) => new Connection(new WindowsSqliteConnection(path), this);
        private sealed class Connection(ISqliteConnection inner, BoundedFactory owner) : ISqliteConnection
        {
            public List<string?[]> Run(string sql, params object?[] args)
            {
                foreach (var text in args.OfType<string>())
                { owner.LargestArgument = Math.Max(owner.LargestArgument, text.Length); Check(text.Length <= 65536, "Unbounded database argument."); }
                var result = inner.Run(sql, args);
                foreach (var text in result.SelectMany(r => r).OfType<string>())
                { owner.LargestResult = Math.Max(owner.LargestResult, text.Length); Check(text.Length <= 524288, "Unbounded database result."); }
                return result;
            }
            public void Transaction(Action action) => inner.Transaction(action);
            public void Dispose() => inner.Dispose();
        }
    }
    private sealed class ReloadFailureRepository(IArmyRepository inner) : IArmyRepository
    {
        private bool committed;
        public string DirectoryPath => inner.DirectoryPath;
        public Dataset? LoadDataset() => committed ? throw new IOException("Injected post-commit reload failure") : inner.LoadDataset();
        public List<ArmyList> LoadArmies() => inner.LoadArmies();
        public void Save(ArmyList army, EditorContext? context = null) => inner.Save(army, context);
        public void Delete(string id) => inner.Delete(id);
        public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => inner.PreviewImport(data, layer, completeFactions);
        public void ApplyImport(Dataset data, string? expectedSnapshot = null) { inner.ApplyImport(data, expectedSnapshot); committed = true; }
        public EditorContext LoadContext() => inner.LoadContext();
        public void SaveContext(EditorContext context) => inner.SaveContext(context);
        public void Dispose() { }
    }
}
