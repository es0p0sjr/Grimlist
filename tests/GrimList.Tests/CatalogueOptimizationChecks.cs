using System.IO.Compression;
using System.Security.Cryptography;
using System.Diagnostics;
using GrimList.Application;
using GrimList.Core;
using GrimList.Persistence;
using GrimList.Platform.Windows;

internal static class CatalogueOptimizationChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Incremental attachment, enhancement, undo/redo and context transitions match the complete evaluator", () =>
        {
            var guard = new UnitDefinition("Test", "Guard", [new(5, 100), new(10, 200)], Profile: new(Keywords: ["Shield"],
                Characteristics: new() { ["OC"] = "2" }, Semantics: new([], [new("grant", "Protection", "ability", "From bodyguard", "add", "Feel No Pain 4+",
                    Condition: new("keyword", "Shield"), Recipient: new("leaders", new("keyword", "Medic")))], 3)));
            var medic = new UnitDefinition("Test", "Medic", [new(1, 50)], Profile: new(Keywords: ["Character", "Medic"],
                Weapons: [new("Blade", "Melee", "Melee", "3", "3+", "5", "-1", "2")],
                Abilities: [new("Leader", "This model can be attached to the following unit: Guard.")]));
            var card = new ReferenceCard("Test", "Enhancements", "Blade upgrade", "CHARACTER model only. Add 1 to the Strength and Damage characteristics of the bearer's melee weapons.", "Host", "10 pts");
            var unknown = card with { Name = "Conditional upgrade", Text = "CHARACTER model only.", Modifiers = [new("conditional", "Unknown", "model", "OC", "add", "1", Condition: new("future-dependency"))] };
            var data = new Dataset(new("Synthetic", "1", "", ""), [guard, medic], Detachments: [new("Test", "Host")], ReferenceCards: [card, unknown]);
            var army = ArmyList.New("Test") with { Detachment = "Host", Entries = [new("guard", guard.Key, guard.Name, 5), new("medic", medic.Key, medic.Name, 1), new("spare", guard.Key, guard.Name, 5)] };
            void Compare(ArmyList current)
            {
                var full = EffectiveDatasheets.EvaluateFull(current, data);
                foreach (var entry in current.Entries) Check(DataJson.Write(EffectiveDatasheets.Project(current, entry, data)) == DataJson.Write(full[entry.Id]), "Transition mismatch: " + entry.Id);
            }
            Compare(army);
            var attached = army with { Entries = army.Entries.Select(e => e.Id == "medic" ? e with { AttachedToId = "guard" } : e).ToList() };
            Compare(attached); Check(EffectiveDatasheets.Work(attached, data).Reused == 1, "Unrelated group was recalculated.");
            var enhanced = attached with { Entries = attached.Entries.Select(e => e.Id == "medic" ? e with { Enhancement = new(card.Detachment, card.Name) } : e).ToList() };
            Compare(enhanced); Compare(attached); Compare(enhanced); // Undo and redo immutable snapshots.
            var detached = enhanced with { Entries = enhanced.Entries.Select(e => e.Id == "medic" ? e with { AttachedToId = "" } : e).ToList() };
            Compare(detached);
            var changedContext = detached with { Detachment = "", Limit = 1000 }; Compare(changedContext);
            Check(!EffectiveDatasheets.Work(changedContext, data).Incremental, "Context change reused old rules.");
            Compare(enhanced);
            var unresolved = enhanced with { Entries = enhanced.Entries.Select(e => e.Id == "medic" ? e with { Enhancement = new(unknown.Detachment, unknown.Name) } : e).ToList() };
            Compare(unresolved); Check(!EffectiveDatasheets.Work(unresolved, data).Incremental, "New unknown dependency did not restart full evaluation.");
        });
        test("Content manifests share unchanged definitions and preserve exact historical provenance", () =>
        {
            var path = Path.Combine(root, "content-history");
            using var store = WindowsServices.OpenStore(path);
            var data = LoadoutChecks.Fixture();
            data = data with { Units = data.Units.Select(u => u with { ProfileSource = data.Source, PointsSource = data.Source }).ToList() };
            store.Import(data); data = store.LoadDataset()!; var original = DataJson.Write(data);
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            var oldId = sql.Run("SELECT value FROM settings WHERE key='active_source'").Single()[0];
            var objects = int.Parse(sql.Run("SELECT count(*) FROM catalogue_objects").Single()[0]!);
            var source = data.Source with { Version = "new provenance", ContentHash = "exact-next-source", ImportedAt = "recorded" };
            store.ApplyImport(data with { Source = source, Units = data.Units.Select(u => u with { ProfileSource = source, PointsSource = source }).ToList() });
            Check(int.Parse(sql.Run("SELECT count(*) FROM catalogue_objects").Single()[0]!) == objects + 1, "Provenance-only update duplicated definitions.");
            Check(store.LoadDataset()!.Units[0].ProfileSource == source, "Current source bindings changed.");
            sql.Run("UPDATE settings SET value=? WHERE key='active_source'", oldId);
            Check(DataJson.Write(store.LoadDataset()) == original, "Historical revision changed.");
            Check(sql.Run("SELECT max(length(content)),min(typeof(content)) FROM catalogue_chunks").Single() is var limits
                && int.Parse(limits[0]!) <= 49152 && limits[1] == "blob", "Object transfers are not bounded BLOBs.");
            Check(sql.Run("EXPLAIN QUERY PLAN SELECT * FROM entries WHERE army_id=? ORDER BY position", "any").Any(r => r.Last()!.Contains("entries_army_position")), "Roster query does not use the index.");
        });
        test("GLDS1 snapshots remain readable and subsequent import migrates without replacing historical source IDs", () =>
        {
            var path = Path.Combine(root, "content-glds1"); using var store = WindowsServices.OpenStore(path);
            store.Import(LoadoutChecks.Fixture()); var data = store.LoadDataset()!;
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            var id = sql.Run("SELECT value FROM settings WHERE key='active_source'").Single()[0]!;
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true))
                System.Text.Json.JsonSerializer.Serialize(gzip, data, DataJson.Options);
            var bytes = stream.ToArray(); var chunks = (bytes.Length - 1) / 49152 + 1;
            for (var i = 0; i < chunks; i++) sql.Run("INSERT INTO dataset_chunks VALUES(?,?,?)", id, i, Convert.ToBase64String(bytes.AsSpan(i * 49152, Math.Min(49152, bytes.Length - i * 49152))));
            var header = $"GLDS1:{chunks}:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
            sql.Run("UPDATE dataset_snapshots SET content=? WHERE source_id=?", header, id);
            Check(DataJson.Write(store.LoadDataset()) == DataJson.Write(data), "Legacy compressed revision changed.");
            store.ApplyImport(data);
            Check(sql.Run("SELECT count(*) FROM sources").Single()[0] == "2"
                && sql.Run("SELECT content FROM dataset_snapshots WHERE source_id=?", id).Single()[0] == header, "Migration rewrote history.");
        });
        test("Prepared projections are available at first open and rejected preparation rolls back activation", () =>
        {
            var path = Path.Combine(root, "content-preparation"); using var store = WindowsServices.OpenStore(path);
            store.Import(LoadoutChecks.Fixture());
            var workflow = new LibraryWorkflow(store); var preview = workflow.Prepare(LoadoutChecks.Fixture(), ImportLayer.Combined);
            workflow.Apply(preview);
            var session = new ArmySession(store, useFactionLoading: true);
            Check(session.FactionLoading!.FullReads == 0 && session.FactionLoading.DependencyCalculations == 0, "Committed import rebuilt projections at first open.");
            var revision = store.LibraryRevision(); var before = DataJson.Write(store.LoadDataset());
            using var observer = WindowsServices.OpenStore(path);
            try
            {
                store.ApplyPreparedImport(preview.Dataset, null, revision, staged => {
                    Check(staged.Revision != revision && observer.LibraryRevision() == revision, "Uncommitted import became visible.");
                    throw new InvalidDataException("Reject staged projection");
                });
                throw new Exception("Rejected preparation committed.");
            }
            catch (InvalidDataException) { }
            Check(store.LibraryRevision() == revision && DataJson.Write(store.LoadDataset()) == before, "Projection failure changed active data.");
        });
        test("Leased old faction views survive retention across multiple imports", () =>
        {
            var path = Path.Combine(root, "content-leases"); using var store = WindowsServices.OpenStore(path);
            var data = LoadoutChecks.Fixture(); store.Import(data);
            var first = new ArmySession(store, useFactionLoading: true); var firstHeader = first.FactionLoading!.Revision;
            for (var i = 0; i < 4; i++)
            {
                store.ApplyImport(data with { Units = data.Units.Select(u => u with { Name = u.Name + i }).ToList() });
                _ = new ArmySession(store, useFactionLoading: true);
            }
            var army = ArmyList.New(data.Units[0].Faction);
            Check(first.DatasetForArmy(army)!.Units.Any(u => u.Name == data.Units[0].Name) && first.FactionLoading.Revision == firstHeader,
                "Retention deleted an older reader's content.");
            GC.KeepAlive(first);
        });
        test("Incremental roster evaluation agrees with full passes for local edits and falls back for global conditions", () =>
        {
            var data = LoadoutChecks.Fixture(); var unit = data.Units[0];
            // This fixture has no association constraints; equipment/model edits are local.
            var army = ArmyList.New(unit.Faction) with { Entries = Enumerable.Range(0, 20).Select(i => new ArmyEntry("entry-" + i, unit.Key, unit.Name, 5, Loadout: Loadouts.Create(unit, 5))).ToList() };
            _ = EffectiveDatasheets.Project(army, army.Entries[0], data);
            var stopwatch = Stopwatch.StartNew(); long projected = 0, reused = 0;
            for (var i = 0; i < 20; i++)
            {
                var entry = army.Entries[i];
                army = army with { Entries = army.Entries.Select(e => e.Id == entry.Id ? e with { Models = 10, Loadout = Loadouts.Create(unit, 10) } : e).ToList() };
                var full = EffectiveDatasheets.EvaluateFull(army, data);
                foreach (var member in army.Entries) Check(DataJson.Write(EffectiveDatasheets.Project(army, member, data)) == DataJson.Write(full[member.Id]), "Incremental state disagrees with full evaluator.");
                var work = EffectiveDatasheets.Work(army, data); projected += work.Projected; reused += work.Reused;
                Check(work.Incremental && work.Reused == 19, "Local edit recalculated unrelated entries.");
            }
            Console.WriteLine($"MEASURE incremental-local 20 edits: {stopwatch.ElapsedMilliseconds} ms incl. oracle, {projected} projections, {reused} reused entries");
            var modifier = new DatasheetModifier("global", "Global test", "model", "OC", "add", "1", Condition: new("roster-unit", "any", "atLeast", 1));
            data = data with { Units = [unit with { Profile = unit.Profile! with { Semantics = new([], [modifier]) } }] };
            _ = EffectiveDatasheets.Project(army, army.Entries[0], data);
            army = army with { Entries = army.Entries.Select((e, i) => i == 0 ? e with { Notes = "changed" } : e).ToList() };
            _ = EffectiveDatasheets.Project(army, army.Entries[0], data);
            Check(!EffectiveDatasheets.Work(army, data).Incremental, "Roster-wide condition reused unrelated states.");
        });
    }
}
