using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class BootstrapChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        var factory = new WindowsSqliteConnectionFactory();
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Reject(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or EndOfStreamException) { return; }
            throw new Exception("Invalid bootstrap was accepted.");
        }
        var release = BootstrapRelease.ReadBundled();
        var paths = new WindowsPaths(Path.Combine(root, "bootstrap"));
        test("Bundled release identity, schemas, integrity and neutral library content", () =>
        {
            release.Validate();
            Assert(release.Manifest.ReleaseId == "grimlist-11e-2026.10.16.2", "Unexpected release.");
            Assert(release.Dataset.Units.Count == 2194 && release.Manifest.Factions == 36, "Incomplete library.");
            Assert(release.Dataset.ReferenceCards!.Count == 3879, "Missing references.");
            Assert(release.Dataset.Source.ImportedAt == "", "Personal import timestamp retained.");
            Assert(release.Dataset.Units.All(u => string.IsNullOrEmpty(u.PointsSource?.ImportedAt) && string.IsNullOrEmpty(u.ProfileSource?.ImportedAt)), "Personal unit metadata retained.");
            Assert(release.Dataset.ReferenceCards.All(c => string.IsNullOrEmpty(c.Source?.ImportedAt)), "Personal reference metadata retained.");
        });
        test("No database initializes the production bundled library with no armies", () =>
        {
            using var store = LibraryBootstrap.Open(paths, factory);
            Assert(store.InstalledDataRelease == release.Manifest.ReleaseId, "Release not recorded.");
            Assert(store.LoadDataset()!.Units.Count == release.Manifest.Units, "Import missing units.");
            Assert(store.LoadArmies().Count == 0, "Bootstrap contains user armies.");
            var session = new ArmySession(store);
            var princes = session.Dataset!.Units.Where(u => u.Faction is "Chaos Daemons" or "Chaos Space Marines" && u.Name.Contains("Daemon Prince")).ToList();
            Assert(princes.Count == 4 && princes.All(u => u.Profile?.Semantics?.Choices.Any(g => g.Options.Any(o => o.Name == "Khorne")) == true), "Bootstrapped library lost effective-datasheet choices.");
            using var db = factory.Open(Path.Combine(paths.DataDirectory, "grimlist.db"));
            Assert(db.Run("PRAGMA user_version").Single()[0] == ArmyStore.SchemaVersion.ToString(), "Schema not recorded.");
        });
        test("Bootstrap is idempotent and never reloads a release for existing files", () =>
        {
            // ArmySession above can legitimately persist a bundled recovery snapshot.
            // Reopening must preserve the existing source identities, not assume that
            // the library has never received any post-bootstrap recovery.
            string Sources()
            {
                using var connection = factory.Open(Path.Combine(paths.DataDirectory, "grimlist.db"));
                return string.Join("|", connection.Run("SELECT id FROM sources ORDER BY id").Select(row => row[0]));
            }
            var before = Sources();
            Assert(before.Length > 0, "Bootstrap sources missing.");
            for (var i = 0; i < 2; i++)
            {
                using var store = LibraryBootstrap.Open(paths, factory, () => throw new Exception("Re-bootstrap attempted"));
                Assert(store.InstalledDataRelease == release.Manifest.ReleaseId, "Release changed.");
            }
            Assert(Sources() == before, "Reopening added or replaced a source snapshot.");
        });
        string armyJson = "", armyId = "";
        test("Bootstrapped library supports detachment, equipment, enhancement, references and normal army validation", () =>
        {
            using var store = LibraryBootstrap.Open(paths, factory);
            var session = new ArmySession(store);
            session.Create("Death Guard", "Standalone acceptance army", 2000, ["Virulent Vectorium"]);
            var unit = session.Dataset!.Units.First(u => u.Faction == "Death Guard" && u.Name == "Malignant Plaguecaster");
            session.AddUnit(unit.Key, 1);
            session.SelectWarlord(session.Army.Entries.Single().Id);
            Assert(session.Army.Entries.Single().Loadout != null, "Equipment not initialized.");
            var options = Enhancements.Options(session.Army, session.Dataset, unit);
            Assert(options.Count > 0, "No usable enhancements.");
            session.SelectEnhancement(session.Army.Entries.Single().Id, new(options[0].Detachment, options[0].Name));
            Assert(session.Result.Complete && session.Result.Total > 0, "No points.");
            Assert(session.Result.Validated, string.Join("; ", session.Result.Issues));
            var cards = ArmyReferences.ForArmy(session.Army, session.Dataset);
            Assert(cards.Any(c => c.Section == "Stratagems" && c.Detachment == "Virulent Vectorium"), "Detachment stratagems missing.");
            Assert(cards.Any(ArmyReferences.IsCore), "Core references missing.");
            armyId = session.Army.Id; armyJson = DataJson.Write(session.Army);
        });
        test("Save, close, reopen preserves army and validation after bootstrap", () =>
        {
            using var store = LibraryBootstrap.Open(paths, factory, () => throw new Exception("Re-bootstrap attempted"));
            var session = new ArmySession(store); session.Open(armyId);
            Assert(DataJson.Write(session.Army) == armyJson, "Saved army changed.");
            Assert(session.Result.Validated, "Reopened roster does not validate.");
            Assert(ArmyReferences.ForArmy(session.Army, session.Dataset).Any(c => c.Section == "Stratagems"), "Reopened references missing.");
        });
        var small = release with
        {
            Dataset = new Dataset(new("Bootstrap test", "1", "", ""), [new("Test", "Unit", [new(1, 5)])],
                Detachments: [new("Test", "Detachment")], ReferenceCards: [new("Test", "Army Rules", "Rule", "Text")]),
            Manifest = release.Manifest with { Units = 1, Factions = 1, Detachments = 1, ReferenceCards = 1 }
        };
        test("Existing legacy library and existing empty library never bootstrap or alter armies", () =>
        {
            foreach (var populated in new[] { false, true })
            {
                var oldPaths = new WindowsPaths(Path.Combine(root, "legacy-" + populated));
                var army = ArmyList.New("Test") with { Name = "Keep this army" };
                using (var old = new ArmyStore(oldPaths.DataDirectory, factory))
                { if (populated) { old.Import(small.Dataset); old.Save(army); } }
                using var reopened = LibraryBootstrap.Open(oldPaths, factory, () => throw new Exception("Existing DB bootstrapped"));
                Assert(reopened.InstalledDataRelease == null, "Legacy release changed.");
                Assert(populated ? DataJson.Write(reopened.LoadArmies().Single()) == DataJson.Write(army) : reopened.LoadDataset() == null, "Legacy content changed.");
            }
        });
        foreach (var failure in new[] { "CREATE TABLE", "INSERT INTO units", "INSERT INTO settings VALUES('installed_data_release'" })
            test("Bootstrap rollback and retry after failure at " + failure, () =>
            {
                var failedPaths = new WindowsPaths(Path.Combine(root, "failed-" + Guid.NewGuid()));
                Reject(() => { using var store = LibraryBootstrap.Open(failedPaths, new FailingFactory(failure), () => small); });
                Assert(!File.Exists(Path.Combine(failedPaths.DataDirectory, "grimlist.db")), "Partial database published.");
                using var retry = LibraryBootstrap.Open(failedPaths, factory, () => small);
                Assert(retry.LoadDataset()!.Units.Count == 1, "Retry failed.");
            });
        test("Abandoned initialization from process death is discarded before retry", () =>
        {
            var abandoned = new WindowsPaths(Path.Combine(root, "abandoned"));
            using (var partial = new ArmyStore(Path.Combine(abandoned.DataDirectory, ".bootstrap-initializing"), factory)) partial.Save(ArmyList.New("Test"));
            using var store = LibraryBootstrap.Open(abandoned, factory, () => small);
            Assert(store.LoadArmies().Count == 0 && store.InstalledDataRelease != null, "Partial state was reused.");
        });
        test("Concurrent first starts publish exactly one import", () =>
        {
            var concurrent = new WindowsPaths(Path.Combine(root, "concurrent"));
            var loads = 0;
            Parallel.For(0, 2, _ =>
            {
                using var store = LibraryBootstrap.Open(concurrent, factory, () => { Interlocked.Increment(ref loads); return small; });
                Assert(store.LoadDataset() != null, "Missing concurrent library.");
            });
            Assert(loads == 1, "Bootstrap applied twice.");
        });
        test("Incompatible schema, corrupt payload and private envelope fields are rejected", () =>
        {
            Reject(() => (release with { Manifest = release.Manifest with { DatabaseSchemaVersion = 999 } }).Validate());
            Reject(() => (release with { Manifest = release.Manifest with { FormatVersion = 999 } }).Validate());
            Reject(() => (release with { Dataset = release.Dataset with { SchemaVersion = 999 } }).Validate());
            using var metadata = new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(release.Manifest)));
            using var corrupt = new MemoryStream();
            using (var gzip = new GZipStream(corrupt, CompressionMode.Compress, true)) gzip.Write([1, 2, 3]);
            corrupt.Position = 0;
            Reject(() => BootstrapRelease.Read(metadata, corrupt));
            var privateJson = JsonNode.Parse(DataJson.Write(small.Dataset))!;
            privateJson["Armies"] = new JsonArray();
            var privateBytes = Encoding.UTF8.GetBytes(privateJson.ToJsonString());
            var privateManifest = small.Manifest with { ContentBytes = privateBytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(privateBytes)) };
            using var privateMetadata = new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(privateManifest)));
            using var privatePayload = new MemoryStream();
            using (var gzip = new GZipStream(privatePayload, CompressionMode.Compress, true)) gzip.Write(privateBytes);
            privatePayload.Position = 0;
            Reject(() => BootstrapRelease.Read(privateMetadata, privatePayload));
            metadata.Position = 0;
            using var incompatibleMetadata = new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(release.Manifest with { DatasetSchemaVersion = 999 })));
            corrupt.Position = 0;
            Reject(() => BootstrapRelease.Read(incompatibleMetadata, corrupt));
        });
    }

    private sealed class FailingFactory(string statement) : ISqliteConnectionFactory
    {
        public ISqliteConnection Open(string path) => new FailingConnection(new WindowsSqliteConnection(path), statement);
    }
    private sealed class FailingConnection(ISqliteConnection inner, string statement) : ISqliteConnection
    {
        public List<string?[]> Run(string sql, params object?[] arguments)
        {
            if (sql.StartsWith(statement, StringComparison.Ordinal)) throw new IOException("Injected interruption");
            return inner.Run(sql, arguments);
        }
        public void Transaction(Action action) => inner.Transaction(action);
        public void Dispose() => inner.Dispose();
    }
}
