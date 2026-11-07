using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class FactionLoadingChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Dataset Fixture()
    {
        var data = LoadoutChecks.Fixture();
        var source = data.Source with { Edition = "11" };
        var squad = data.Units.Single() with { ProfileSource = source, PointsSource = source };
        return data with { Source = source, Units = [squad,
            new("Chaos Knights", "Ally", [new(1, 140)], Profile: new(Keywords: ["Chaos", "War Dog", "Faction: Chaos Knights"])),
            new("Plague Legions", "Detachment ally", [new(1, 100)], RequiredDetachments: ["Tallyband Summoners"]),
            new("Orks", "Other faction", [new(1, 90)], Profile: new(Abilities: [new("Shared keyword", "Shared definition from an unloaded faction.")])),
            new("Custom faction", "Custom unit", [new(1, 10)])],
            Detachments = [new("Death Guard", "Virulent Vectorium"), new("Death Guard", "Tallyband Summoners"), new("Custom faction", "Custom detachment")],
            ReferenceCards = [new("Death Guard", "Army Rules", "Faction rule", "Faction text"), new("Orks", "Army Rules", "Other rule", "Other text")] };
    }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Pooled startup headers preserve exact provenance and migrate a compatible manifest without full recovery", () =>
        {
            var path = Path.Combine(root, "faction-header-migration");
            using var store = WindowsServices.OpenStore(path); var fixture = Fixture();
            fixture = fixture with { Detachments = fixture.Detachments!.Select(d => d with { Source = fixture.Source }).ToList(),
                ReferenceCoverage = [new("coverage-owner", "11", Faction: "Death Guard", Source: fixture.Source)] };
            store.Import(fixture); var first = new ArmySession(store, useFactionLoading: true);
            var folder = Path.Combine(path, "derived-cache", "factions");
            var currentPath = Path.Combine(folder, first.FactionLoading!.CacheIdentity + ".json.gz");
            var packed = FactionLibrary.Read<FactionLibrary.Manifest>(currentPath);
            Check(packed.HeaderSources != null && packed.Header.Detachments!.All(d => d.Source == null)
                && packed.Header.ReferenceCoverage!.All(c => c.Source == null), "Startup header still repeats source payloads");
            var warm = new ArmySession(store, useFactionLoading: true);
            Check(DataJson.Write(warm.FactionLoading!.Header) == DataJson.Write(first.FactionLoading.Header), "Pooled header changed exact source attribution");
            var legacy = packed with { Version = packed.Version.Replace("factions-4/", "factions-3/"), Header = first.FactionLoading.Header, HeaderSources = null };
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacy.Version + "/" + legacy.Revision)));
            var legacyPath = Path.Combine(folder, identity + ".json.gz");
            FactionLibrary.Write(legacyPath, legacy); File.Delete(currentPath);
            var migrated = new ArmySession(store, useFactionLoading: true);
            Check(migrated.FactionLoading!.MigratedManifest && migrated.FactionLoading.FullReads == 0 && File.Exists(currentPath), "Compatible layout upgrade reran full recovery");
            Check(DataJson.Write(migrated.FactionLoading.Header) == DataJson.Write(first.FactionLoading.Header), "Migrated header changed rules or sources");
            var corrupt = FactionLibrary.Read<FactionLibrary.Manifest>(currentPath);
            corrupt.HeaderSources!.Detachments[0] = int.MaxValue; FactionLibrary.Write(currentPath, corrupt); File.Delete(legacyPath);
            var repaired = new ArmySession(store, useFactionLoading: true);
            Check(repaired.FactionLoading!.FullReads == 1 && DataJson.Write(repaired.FactionLoading.Header) == DataJson.Write(first.FactionLoading.Header),
                "Invalid source binding was accepted or recovery changed provenance");
        });
        test("Unrelated faction revisions preserve summaries; allies and global rules invalidate dependants", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-summary-dependencies")); store.Import(Fixture());
            var session = new ArmySession(store, useFactionLoading: true); var data = session.LoadFullDataset()!;
            foreach (var faction in new[] { "Death Guard", "Orks", "Custom faction" })
            {
                var unit = data.Units.First(u => u.Faction == faction);
                store.Save(ArmyList.New(faction) with { Entries = [new("summary-" + faction, unit.Key, unit.Name, unit.Options[0].Models)] });
            }
            void Verify()
            {
                var full = session.LoadFullDataset();
                foreach (var summary in session.LoadArmySummaries())
                {
                    var result = ArmyCalculator.Calculate(summary.Army, full);
                    Check(summary.Result == new ArmySummary(result.Total, result.Complete, result.Validated), "Dependency summary differs from full evaluator");
                }
            }
            Verify(); var count = session.SummaryCalculationCount;
            var old = session.FactionLoading!.SummaryRevision(ArmyList.New("Death Guard"));
            data = data with { Units = data.Units.Select(u => u.Faction == "Orks" ? u with { Options = [new(1, 125)] } : u).ToList() };
            store.ApplyImport(data); session.ReloadLibrary(); Verify();
            Check(session.SummaryCalculationCount == count + 1, "Unrelated import recalculated every army"); count++;
            Check(session.FactionLoading!.SummaryRevision(ArmyList.New("Death Guard")) == old, "Unrelated change invalidated Death Guard");
            Check(session.FactionLoading.DependencyCalculations == 0 && session.FactionLoading.DependencyReuses > 0, "Price edit rebuilt availability routes");
            data = data with { Units = data.Units.Select(u => u.Faction == "Chaos Knights" ? u with { Options = [new(1, 180)] } : u).ToList() };
            store.ApplyImport(data); session.ReloadLibrary(); Verify();
            Check(session.SummaryCalculationCount == count + 1, "Allied revision did not invalidate exactly its dependant"); count++;
            data = data with { ReferenceCards = [.. data.ReferenceCards!, new("Core", "Army Rules", "Shared rule", "Global reference")] };
            store.ApplyImport(data); session.ReloadLibrary(); Verify();
            Check(session.SummaryCalculationCount == count + 3, "Global rule failed to invalidate dependent summaries");
            var warm = new ArmySession(store, useFactionLoading: true); warm.LoadArmySummaries();
            Check(warm.SummaryCalculationCount == 0 && warm.FactionLoading!.FullReads == 0 && warm.FactionLoading.ShardReads == 0,
                "Reopen lost persisted dependency summaries");
        });
        test("Shared definitions, foreign roster entries and changed eligibility have independent revisions", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-revision-boundaries")); store.Import(Fixture());
            var session = new ArmySession(store, useFactionLoading: true); var data = session.LoadFullDataset()!;
            var custom = ArmyList.New("Custom faction"); var oldView = session.FactionLoading!.ViewRevision(custom);
            var oldSummary = session.FactionLoading.SummaryRevision(custom);
            var foreign = data.Units.First(u => u.Faction == "Orks");
            var foreignArmy = custom with { Entries = [new("foreign-revision", foreign.Key, foreign.Name, 1)] };
            var foreignRevision = session.FactionLoading.SummaryRevision(foreignArmy);
            data = data with { Units = data.Units.Select(u => u.Faction == "Orks" ? u with { Profile = u.Profile! with {
                Abilities = [new("Shared keyword", "New explanation")] } } : u).ToList() };
            store.ApplyImport(data); session.ReloadLibrary();
            Check(session.FactionLoading!.ViewRevision(custom) != oldView && session.FactionLoading.SummaryRevision(custom) == oldSummary,
                "Display-only shared definitions invalidated unrelated calculations or failed to update views");
            Check(session.FactionLoading.SummaryRevision(foreignArmy) != foreignRevision, "Saved foreign unit omitted from dependencies");
            data = data with { Units = data.Units.Select(u => u.Faction == "Chaos Knights" ? u with { Profile = u.Profile! with { Keywords = ["Reference"] } } : u).ToList() };
            store.ApplyImport(data); session.ReloadLibrary();
            Check(session.FactionLoading!.DependencyCalculations > 0 && session.FactionLoading.DependencyReuses > 0,
                "Eligibility edit rebuilt everything or reused every route");
            var view = session.DatasetForArmy(ArmyList.New("Death Guard"))!;
            Check(view.Units.All(u => u.Faction != "Chaos Knights"), "Removed allied eligibility retained a dependency");
        });
        test("Provenance-only imports preserve semantic summary revisions and exact displayed sources", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-provenance-revision")); store.Import(Fixture());
            var session = new ArmySession(store, useFactionLoading: true); var data = session.LoadFullDataset()!;
            var army = ArmyList.New("Death Guard"); var revision = session.FactionLoading!.SummaryRevision(army);
            var displayRevision = session.FactionLoading.ViewRevision(army);
            SourceInfo Update(SourceInfo source) => source with { Version = "new commit", Date = "2026-10-28", ContentHash = "new archive" };
            data = data with { Source = Update(data.Source), Units = data.Units.Select(u => u with {
                ProfileSource = u.ProfileSource == null ? null : Update(u.ProfileSource), PointsSource = u.PointsSource == null ? null : Update(u.PointsSource) }).ToList(),
                ReferenceCards = data.ReferenceCards!.Select(c => c with { Source = c.Source == null ? null : Update(c.Source) }).ToList() };
            store.ApplyImport(data); session.ReloadLibrary();
            Check(session.FactionLoading!.SummaryRevision(army) == revision, "Display provenance invalidated a semantic summary");
            Check(session.FactionLoading.ViewRevision(army) != displayRevision, "Displayed source changes retained an old view revision");
            Check(session.DatasetForArmy(army)!.Units.First(u => u.Faction == "Death Guard").ProfileSource!.ContentHash == "new archive",
                "Semantic reuse discarded current source evidence");
        });
        test("Metadata-only faction owners and unknown factions cannot reuse stale summary revisions", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-metadata-owners"));
            var fixture = Fixture(); store.Import(fixture with { Detachments = [.. fixture.Detachments!, new("Metadata owner", "Host", Cost: "1 DP")] });
            var session = new ArmySession(store, useFactionLoading: true); var data = session.LoadFullDataset()!;
            var owner = ArmyList.New("Metadata owner"); var unknown = ArmyList.New("Absent owner");
            var previous = session.FactionLoading!.SummaryRevision(owner); var missing = session.FactionLoading.SummaryRevision(unknown);
            store.ApplyImport(data with { Detachments = data.Detachments!.Select(d => d.Faction == owner.Faction ? d with { Cost = "3 DP" } : d).ToList() });
            session.ReloadLibrary();
            Check(session.FactionLoading!.SummaryRevision(owner) != previous, "Header-only faction change missed its owner");
            Check(session.FactionLoading.SummaryRevision(unknown) != missing, "Unknown scope was optimistically reused");
        });
        test("Unchanged in-memory faction profiles survive reload without stale header or provenance", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-profile-reuse")); store.Import(Fixture());
            var session = new ArmySession(store, useFactionLoading: true); var full = session.LoadFullDataset()!;
            var army = ArmyList.New("Death Guard"); var before = session.DatasetForArmy(army)!;
            store.ApplyImport(full with { Detachments = [.. full.Detachments!, new("Orks", "New detachment")],
                Units = full.Units.Select(u => u.Faction == "Orks" ? u with { Options = [new(1, 133)] } : u).ToList() });
            session.ReloadLibrary(); var after = session.DatasetForArmy(army)!;
            Check(ReferenceEquals(before.Units.First(u => u.Faction == "Death Guard").Profile, after.Units.First(u => u.Faction == "Death Guard").Profile),
                "Unchanged profile interpretations lost their immutable source identity");
            Check(session.FactionLoading!.ShardReads == 0 && session.FactionLoading.ShardReuses > 0, "Unchanged loaded faction files were reread");
            Check(after.Detachments!.Any(d => d.Name == "New detachment"), "Reused view retained an old global header");
            Directory.CreateDirectory(Path.Combine(store.DirectoryPath, "sources", "bsdata-new"));
            var warm = new ArmySession(store, useFactionLoading: true);
            Check(warm.FactionLoading!.FullReads == 1, "New local recovery evidence was hidden by a warm manifest");
        });
        test("Recovery-version rebuild reuses verified content and repairs corrupt payloads", () =>
        {
            var path = Path.Combine(root, "faction-recovery-reuse");
            using var store = WindowsServices.OpenStore(path); store.Import(Fixture());
            var first = new ArmySession(store, useFactionLoading: true, libraryVariant: "recovery-one");
            var identity = first.FactionLoading!.FactionContentRevision("Death Guard")!;
            var next = new ArmySession(store, useFactionLoading: true, libraryVariant: "recovery-two");
            Check(next.FactionLoading!.FullReads == 1 && next.FactionLoading.ContentWrites == 0
                && next.FactionLoading.ContentReuses > 0 && next.FactionLoading.FactionContentRevision("Death Guard") == identity,
                "Recovery skipped verification or duplicated unchanged content");
            File.WriteAllText(Path.Combine(path, "derived-cache", "factions", identity + ".json.gz"), "interrupted content");
            var repaired = new ArmySession(store, useFactionLoading: true, libraryVariant: "recovery-three");
            Check(repaired.FactionLoading!.ContentWrites == 1 && repaired.FactionLoading.ContentReuses > 0,
                "Rebuild reused damaged content or rewrote every faction");
            Check(repaired.DatasetForArmy(ArmyList.New("Death Guard"))!.Units.Any(u => u.Faction == "Death Guard"), "Repaired faction missing");
        });
        test("Unavailable cache publication falls back without changing authoritative data", () =>
        {
            var path = Path.Combine(root, "faction-publication-failure");
            using var store = WindowsServices.OpenStore(path); store.Import(Fixture());
            _ = new ArmySession(store); // Complete legitimate source recovery before isolating cache publication.
            var revision = store.LibraryRevision();
            Directory.CreateDirectory(Path.Combine(path, "derived-cache"));
            File.WriteAllText(Path.Combine(path, "derived-cache", "factions"), "Prevent cache directory creation");
            var session = new ArmySession(store, useFactionLoading: true);
            var view = session.DatasetForArmy(ArmyList.New("Death Guard"))!;
            Check(view.Units.Count == store.LoadDataset()!.Units.Count && store.LibraryRevision() == revision,
                "Failed publication changed the source or exposed partial content");
        });
        test("Faction content survives unrelated imports, ordering and provenance changes", () =>
        {
            var path = Path.Combine(root, "faction-content-reuse");
            using var store = WindowsServices.OpenStore(path); var fixture = Fixture(); store.Import(fixture);
            var initial = new ArmySession(store, useFactionLoading: true);
            fixture = initial.LoadFullDataset()!; // Preserve canonical IDs/aliases established by the first import.
            var previous = initial.FactionLoading!.FactionContentRevision("Death Guard");
            Check(initial.FactionLoading.SharedAbilityReads == 0, "Home loaded shared definitions");
            var source = fixture.Source with { Version = "changed", ContentHash = "changed-source-hash" };
            var changed = fixture with { Source = source,
                Units = [new("Orks", "Inserted before other factions", [new(1, 15)]), .. fixture.Units.Select(u =>
                    u.Faction == "Death Guard" ? u with { ProfileSource = source, PointsSource = source } : u)],
                ReferenceCards = [new("Orks", "Army Rules", "Inserted card", "New content"), .. fixture.ReferenceCards!.Select(c =>
                    c.Faction == "Death Guard" ? c with { Source = source } : c)] };
            store.ApplyImport(changed);
            var next = new ArmySession(store, useFactionLoading: true);
            Check(next.FactionLoading!.FactionContentRevision("Death Guard") == previous && next.FactionLoading.ContentReuses > 0,
                "Unrelated ordering/provenance change rewrote faction content");
            var full = next.LoadFullDataset()!; var army = ArmyList.New("Death Guard"); var scoped = next.DatasetForArmy(army)!;
            var keys = scoped.Units.Select(u => u.Key).ToHashSet();
            Check(DataJson.Write(scoped.Units) == DataJson.Write(full.Units.Where(u => keys.Contains(u.Key)).ToList()), "Unit ordering or exact provenance changed");
            Check(DataJson.Write(scoped.ReferenceCards!.Where(c => c.Faction == "Death Guard").ToList())
                == DataJson.Write(full.ReferenceCards!.Where(c => c.Faction == "Death Guard").ToList()), "Reference provenance changed");
            Check(next.FactionLoading.SharedAbilityReads == 1, "Definitions not loaded once on demand");
            var changedAgain = changed with { Units = changed.Units.Select(u => u.Faction == "Orks" && u.Profile?.Abilities != null
                ? u with { Profile = u.Profile with { Abilities = [new("Shared keyword", "Changed shared definition")] } } : u).ToList() };
            store.ApplyImport(changedAgain); next.ReloadLibrary();
            Check(next.FactionLoading!.FactionContentRevision("Death Guard") == previous, "Shared definition rewrote unrelated unit content");
            scoped = next.DatasetForArmy(army)!;
            var weapon = new WeaponProfile("Test", "Ranged", "12", "1", "3+", "4", "0", "1", "Shared keyword");
            Check(WeaponRuleReference.ForWeapon(weapon, null, scoped).Any(r => r.Text == "Changed shared definition"), "Shared dependency reused stale text");
            var oldRevision = next.FactionLoading.FactionContentRevision("Death Guard");
            store.ApplyImport(changedAgain with { Units = changedAgain.Units.Select(u => u.Faction == "Death Guard" ? u with { Options = [new(5, 333)] } : u).ToList() });
            next.ReloadLibrary();
            Check(next.FactionLoading!.FactionContentRevision("Death Guard") != oldRevision, "Changed faction price reused content");
            Check(next.DatasetForArmy(army)!.Units.First(u => u.Faction == "Death Guard").Options[0].Points == 333, "New prices missing");
        });
        test("Failed scoped edits retain the previous army, dataset, result and history", () =>
        {
            var path = Path.Combine(root, "faction-save-failure");
            using var store = WindowsServices.OpenStore(path); store.Import(Fixture());
            var session = new ArmySession(store, useFactionLoading: true);
            session.Create("Death Guard", "Keep", 2000, ["Virulent Vectorium"]);
            var unit = session.Dataset!.Units.Single(u => u.Faction == "Death Guard"); session.AddUnit(unit.Key, 5);
            var data = session.Dataset; var army = session.Army; var result = session.Result;
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            sql.Run("CREATE TRIGGER reject_faction_save BEFORE INSERT ON armies WHEN NEW.name='Reject' BEGIN SELECT RAISE(ABORT, 'Injected failure'); END");
            var rejected = false;
            try { session.Open(ArmyList.New("Orks") with { Name = "Reject" }); }
            catch (Exception) { rejected = true; }
            Check(rejected && ReferenceEquals(session.Dataset, data) && ReferenceEquals(session.Army, army)
                && ReferenceEquals(session.Result, result) && session.CanUndo, "Failed save published a different faction");
            var other = Fixture().Units.Single(u => u.Faction == "Orks"); rejected = false;
            try { session.Commit(army with { Name = "", Entries = [.. army.Entries, new("foreign-rejected", other.Key, other.Name, 1)] }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && ReferenceEquals(session.Dataset, data) && ReferenceEquals(session.Army, army), "Rejected edit changed faction view");
            rejected = false; try { store.ApplyImport(data!); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && store.LoadDataset()!.Units.Count == Fixture().Units.Count, "Scoped view replaced complete library");
        });
        test("Faction loading matches full rules, allies, references and foreign roster entries", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-parity")); store.Import(Fixture());
            var eager = new ArmySession(store); var full = eager.Dataset!;
            var lazy = new ArmySession(store, useFactionLoading: true);
            Check(lazy.Dataset!.Units.Count == 0 && lazy.LibraryUnitCount == full.Units.Count, "Home loaded unit profiles");
            Check(lazy.Factions.SequenceEqual(eager.Factions), "Faction picker lost factions");
            var army = ArmyList.New("Death Guard") with { Detachments = ["Virulent Vectorium"] };
            var view = lazy.DatasetForArmy(army)!;
            Check(view.Units.Count < full.Units.Count && view.Units.Any(u => u.Name == "Detachment ally"), "Missing future detachment dependency");
            foreach (var detachment in new[] { "Virulent Vectorium", "Tallyband Summoners" })
            {
                var draft = army with { Detachments = [detachment] };
                Check(AlliedRules.Library(draft, full).Select(u => u.Key).SequenceEqual(AlliedRules.Library(draft, view).Select(u => u.Key)), "Allied library differs");
                Check(DataJson.Write(ArmyReferences.ForArmy(draft, full)) == DataJson.Write(ArmyReferences.ForArmy(draft, view)), "References differ");
            }
            var other = full.Units.Single(u => u.Faction == "Orks");
            army = army with { Entries = [new("foreign", other.Key, other.Name, 1)] };
            Check(DataJson.Write(ArmyCalculator.Calculate(army, full)) == DataJson.Write(ArmyCalculator.Calculate(army, lazy.DatasetForArmy(army))), "Invalid foreign selection disappeared");
            var weapon = new WeaponProfile("Test", "Ranged", "12", "1", "3+", "4", "0", "1", "Shared keyword");
            Check(DataJson.Write(WeaponRuleReference.ForWeapon(weapon, null, full)) == DataJson.Write(WeaponRuleReference.ForWeapon(weapon, null, view)), "Cross-faction shared definition disappeared");
            Check(DataJson.Write(lazy.LoadFullDataset()) == DataJson.Write(full), "Full export was narrowed");
            lazy.Create("Custom faction", "New faction", 2000, ["Custom detachment"]);
            Check(lazy.BrowseLibrary(new()).Single().Unit.Name == "Custom unit", "New unsaved faction unavailable");
        });
        test("Warm Home uses persisted summaries without reading any faction profiles", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "faction-summaries")); store.Import(Fixture());
            var lazy = new ArmySession(store, useFactionLoading: true);
            lazy.Create("Death Guard", "Saved", 2000, ["Virulent Vectorium"]);
            var unit = lazy.Dataset!.Units.Single(u => u.Faction == "Death Guard"); lazy.AddUnit(unit.Key, 5);
            var saved = lazy.Army;
            var expected = lazy.LoadArmySummaries().Single().Result;
            var warm = new ArmySession(store, useFactionLoading: true);
            Check(warm.LoadArmySummaries().Single().Result == expected, "Persisted summary differs");
            Check(warm.FactionLoading!.FullReads == 0 && warm.FactionLoading.ShardReads == 0 && warm.SummaryCalculationCount == 0, "Warm Home read profiles/recalculated");
            store.Save(saved with { Limit = 1 });
            Check(!warm.LoadArmySummaries().Single().Result.Validated && warm.SummaryCalculationCount == 1, "External army edit left a stale summary");
            warm.Open(saved); var id = warm.SelectedEntry!.Id;
            warm.ResizeUnit(id, 10); warm.Undo(); Check(warm.SelectedEntry!.Models == 5, "Scoped undo failed");
            warm.Redo(); Check(warm.SelectedEntry!.Models == 10, "Scoped redo failed");
        });
        test("Faction views stay bounded and imports invalidate disk caches and summaries", () =>
        {
            var path = Path.Combine(root, "faction-invalidation");
            using var store = WindowsServices.OpenStore(path); var fixture = Fixture(); store.Import(fixture);
            var lazy = new ArmySession(store, useFactionLoading: true);
            foreach (var f in lazy.Factions) lazy.DatasetForArmy(ArmyList.New(f));
            Check(lazy.FactionLoading!.CachedViews <= 2, "Faction cache grew unbounded");
            var army = ArmyList.New("Orks"); var original = lazy.DatasetForArmy(army);
            store.ApplyImport(fixture with { Units = fixture.Units.Select(u => u.Faction == "Orks" ? u with { Options = [new(1, 99)] } : u).ToList() });
            lazy.ReloadLibrary(); var next = lazy.DatasetForArmy(army)!;
            Check(!ReferenceEquals(original, next) && next.Units.Single(u => u.Faction == "Orks").Options[0].Points == 99, "Import reused stale faction");
            using var sql = new WindowsSqliteConnection(Path.Combine(path, "grimlist.db"));
            var before = lazy.FactionLoading.Revision; sql.Run("UPDATE dataset_snapshots SET content=content");
            var reopened = new ArmySession(store, useFactionLoading: true);
            Check(reopened.FactionLoading!.Revision != before && reopened.FactionLoading.FullReads == 1, "External library edit reused cache");
        });
        test("Corrupt or missing faction cache recovers from the authoritative library", () =>
        {
            var path = Path.Combine(root, "faction-corruption");
            using var store = WindowsServices.OpenStore(path); store.Import(Fixture());
            var lazy = new ArmySession(store, useFactionLoading: true);
            foreach (var file in Directory.GetFiles(Path.Combine(path, "derived-cache", "factions"), "*.json.gz")) File.WriteAllText(file, "damaged");
            var warm = new ArmySession(store, useFactionLoading: true);
            Check(warm.FactionLoading!.FullReads == 1, "Corrupt manifest accepted");
            foreach (var file in Directory.GetFiles(Path.Combine(path, "derived-cache", "factions"), "*.json.gz")) File.Delete(file);
            var scope = warm.DatasetForArmy(ArmyList.New("Death Guard"))!;
            Check(scope.FindUnit(Fixture().Units[0].Key) != null, "Missing faction file removed a unit");
            Check(store.LoadDataset()!.Units.Count == Fixture().Units.Count, "Derived cache changed source data");
        });
    }
}
