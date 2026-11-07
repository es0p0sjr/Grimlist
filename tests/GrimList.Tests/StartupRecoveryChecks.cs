using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class StartupRecoveryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Recovery contracts fingerprint actual embedded evidence independently of module identities", () =>
        {
            var count = 0;
            foreach (var assembly in new[] { typeof(LibraryRecoveryVersion).Assembly, typeof(Dataset).Assembly })
            {
                foreach (var attribute in assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                    .Cast<System.Reflection.AssemblyMetadataAttribute>().Where(a => a.Key.StartsWith("GrimList.CacheResource.")))
                {
                    var resource = assembly.GetName().Name + "." + attribute.Key["GrimList.CacheResource.".Length..].Replace('\\', '.').Replace('/', '.');
                    using var stream = assembly.GetManifestResourceStream(resource) ?? throw new Exception("Missing fingerprinted resource " + resource);
                    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)) == attribute.Value, "Build-time evidence fingerprint differs from packaged bytes");
                    count++;
                }
                Check(!LibraryRecoveryVersion.Current.Contains(assembly.ManifestModule.ModuleVersionId.ToString()), "Recovery contract depends on unrelated compiled code");
            }
            Check(count >= 10 && LibraryRecoveryVersion.References != LibraryRecoveryVersion.Enhancements
                && LibraryRecoveryVersion.Effective.Contains(LibraryRecoveryVersion.Compilation), "Recovery contracts lost evidence or independent boundaries");
        });
        test("Library revision survives reopen and roster saves; snapshot edits/imports invalidate checks atomically", () =>
        {
            var path = Path.Combine(root, "startup-revision");
            string revision;
            using (var store = new ArmyStore(path, new WindowsSqliteConnectionFactory()))
            {
                Check(store.LoadLibrary().Revision == null, "Empty database has a trusted snapshot revision");
                store.Import(LoadoutChecks.Fixture());
                revision = store.LoadLibrary().Revision!;
                Check(revision != null, "Persisted snapshot lacks revision");
                store.RecordLibraryCheck("test", "v1", revision);
                var army = ArmyList.New("Fixture") with { Name = "Preserved" };
                store.Save(army, new(army.Id));
                Check(store.LoadLibrary().Revision == revision && store.HasLibraryCheck("test", "v1", revision), "Roster save invalidates library");
                Check(!store.HasLibraryCheck("test", "v2", revision), "Recovery version change accepted old check");
            }
            using var reopened = new ArmyStore(path, new WindowsSqliteConnectionFactory());
            Check(reopened.LoadLibrary().Revision == revision && reopened.HasLibraryCheck("test", "v1", revision), "Reopen invalidated unchanged library");
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            try { sql.Transaction(() => { sql.Run("UPDATE dataset_snapshots SET content=content"); throw new IOException("rollback"); }); }
            catch (IOException) { }
            Check(reopened.LoadLibrary().Revision == revision, "Rolled-back edit changed revision");
            sql.Run("UPDATE dataset_snapshots SET content=content");
            var edited = reopened.LoadLibrary().Revision;
            Check(edited != revision && !reopened.HasLibraryCheck("test", "v1", edited), "Direct SQL snapshot edit reused check");
            reopened.RecordLibraryCheck("stale", "v1", revision);
            Check(!reopened.HasLibraryCheck("stale", "v1", revision), "Stale reader recorded a check against replaced data");
            reopened.Import(LoadoutChecks.Fixture());
            Check(reopened.LoadLibrary().Revision != edited && reopened.LoadArmies().Single().Name == "Preserved", "Import failed to invalidate or preserve army");
            var imported = reopened.LoadLibrary().Revision;
            sql.Run("UPDATE settings SET value=value WHERE key='active_source'");
            Check(reopened.LoadLibrary().Revision != imported, "Active-source switch reused revision");
        });
        test("Android startup reuses one snapshot; cached recovery and corrupt overlays preserve complete library semantics", () =>
        {
            var path = Path.Combine(root, "startup-android");
            var factory = new ReadCountingFactory();
            using var store = LibraryBootstrap.Open(new WindowsPaths(path), factory);
            var first = ReferenceLibraryRecovery.OpenLibrary(store).Library;
            var session = new ArmySession(store, loadedLibrary: first);
            var expected = DataJson.Write(session.Dataset);
            factory.Reads = 0;
            var next = ReferenceLibraryRecovery.OpenLibrary(store);
            Check(!next.Changed, "Unchanged Android library imported again");
            session = new ArmySession(store, loadedLibrary: next.Library);
            Check(factory.Reads == 1, "Android + session loaded snapshot more than once");
            Check(DataJson.Write(session.Dataset) == expected, "Cached recovery changed rules or provenance");
            Check(store.HasLibraryCheck("enhancement-recovery", LibraryRecoveryVersion.Enhancements, next.Library.Revision), "Enhancement result was not persisted");
            // Keep a valid header/length but break the body checksum, rather than only truncating it.
            foreach (var file in Directory.GetFiles(Path.Combine(path, "derived-cache"), "effective-*.gz"))
            { var bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes); }
            session = new ArmySession(store, loadedLibrary: ReferenceLibraryRecovery.OpenLibrary(store).Library);
            Check(DataJson.Write(session.Dataset) == expected, "Corrupt overlay failed to recover canonical result");
            // Removing valid reference cards is a real library change, even with the same source metadata.
            var stripped = next.Library.Dataset! with { ReferenceCards = next.Library.Dataset!.ReferenceCards!.Where(c => c.Section != "Stratagems").ToList() };
            store.ApplyImport(stripped);
            Check(ReferenceLibraryRecovery.OpenLibrary(store).Changed, "Reference recovery was skipped after import");
        });
        test("Recovery version mismatch reruns the Android check and missing caches remain optional", () =>
        {
            var path = Path.Combine(root, "startup-version");
            using var store = new ArmyStore(path, new WindowsSqliteConnectionFactory());
            var data = LoadoutChecks.Fixture() with { Source = LoadoutChecks.Fixture().Source with { Edition = "11" } };
            store.Import(data);
            var loaded = store.LoadLibrary();
            store.RecordLibraryCheck("stratagem-references", "obsolete-version", loaded.Revision);
            var result = ReferenceLibraryRecovery.OpenLibrary(store);
            Check(store.HasLibraryCheck("stratagem-references", ReferenceLibraryRecovery.RecoveryVersion, result.Library.Revision), "Changed package did not recheck recovery");
            var session = new ArmySession(store, loadedLibrary: result.Library);
            Check(session.Dataset != null, "Missing optional derived cache prevented startup");
        });
        test("New source archives bypass an existing enhancement recovery marker", () =>
        {
            var path = Path.Combine(root, "startup-external-evidence");
            using var store = new ArmyStore(path, new WindowsSqliteConnectionFactory());
            store.Import(LoadoutChecks.Fixture()); var loaded = store.LoadLibrary();
            var probe = new CheckCountingRepository(store);
            EnhancementLibraryRepair.RestoreLibrary(probe, loaded);
            probe.Checks = 0;
            Directory.CreateDirectory(Path.Combine(path, "sources", "bsdata-11-new"));
            EnhancementLibraryRepair.RestoreLibrary(probe, loaded);
            Check(probe.Checks == 0, "Archive evidence was hidden behind cached recovery marker");
        });
        test("Lazy reference coverage preserves selected-army lookups and full coverage results", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            var index = DatasetSnapshot.For(data).References;
            var army = ArmyList.New(data.Units[0].Faction);
            var cards = DataJson.Write(index.ForArmy(army, data.Source.Edition));
            var coverage = index.Coverage;
            Check(ReferenceEquals(coverage, index.Coverage), "Coverage report is repeatedly rebuilt");
            Check(coverage.Factions.Count > 0 && cards == DataJson.Write(index.ForArmy(army, data.Source.Edition)), "Coverage changed reference selection");
        });
    }
    private sealed class ReadCountingFactory : ISqliteConnectionFactory
    {
        public int Reads;
        public ISqliteConnection Open(string path) => new Connection(new WindowsSqliteConnection(path), this);
        private sealed class Connection(ISqliteConnection inner, ReadCountingFactory owner) : ISqliteConnection
        {
            public List<string?[]> Run(string sql, params object?[] args)
            { if (sql.StartsWith("SELECT substr(content,1,256) FROM dataset_snapshots")) owner.Reads++; return inner.Run(sql, args); }
            public void Transaction(Action action) => inner.Transaction(action);
            public void Dispose() => inner.Dispose();
        }
    }
    private sealed class CheckCountingRepository(IArmyRepository inner) : IArmyRepository
    {
        public int Checks;
        public string DirectoryPath => inner.DirectoryPath;
        public Dataset? LoadDataset() => inner.LoadDataset();
        public LoadedLibrary LoadLibrary() => inner.LoadLibrary();
        public bool HasLibraryCheck(string name, string version, string? revision) { Checks++; return inner.HasLibraryCheck(name, version, revision); }
        public void RecordLibraryCheck(string name, string version, string? revision) => inner.RecordLibraryCheck(name, version, revision);
        public List<ArmyList> LoadArmies() => inner.LoadArmies();
        public void Save(ArmyList army, EditorContext? context = null) => inner.Save(army, context);
        public void Delete(string id) => inner.Delete(id);
        public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => inner.PreviewImport(data, layer, completeFactions);
        public void ApplyImport(Dataset data, string? expectedSnapshot = null) => inner.ApplyImport(data, expectedSnapshot);
        public EditorContext LoadContext() => inner.LoadContext();
        public void SaveContext(EditorContext context) => inner.SaveContext(context);
        public void Dispose() { }
    }
}
