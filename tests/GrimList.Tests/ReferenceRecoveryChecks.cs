using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class ReferenceRecoveryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root)
    {
        var baseline = ReferenceData.Normalize(BootstrapRelease.ReadBundled().Dataset);
        var pack = ReferenceData.Normalize(ReferenceLibraryRecovery.ReadBundled());
        test("Shared bundled supplement restores every faction and selected-detachment filtering", () =>
        {
            var updated = ReferenceLibraryRecovery.Supplement(baseline, pack);
            var expectedCards = baseline.ReferenceCards!.Where(c => c.Section == "Stratagems").Concat(pack.ReferenceCards!)
                .Select(c => ReferenceModel.Canonical(c, baseline.Source)).DistinctBy(ReferenceModel.LogicalKey).Count();
            Check(updated.ReferenceCards!.Count(c => c.Section == "Stratagems") == expectedCards, "Expected the union of current cards and the remaining supplement without duplicate identities");
            Check(DataJson.Write(baseline.Units) == DataJson.Write(updated.Units), "Units or points changed");
            Check(DataJson.Write(baseline.Source) == DataJson.Write(updated.Source), "Catalogue provenance changed");
            foreach (var old in baseline.Detachments!)
            {
                var next = updated.Detachments!.Single(d => d.Faction == old.Faction && d.Name == old.Name);
                Check(DataJson.Write(old) == DataJson.Write(next with { Id = old.Id, FactionId = old.FactionId }), "Existing detachment semantics changed");
            }
            foreach (var scope in pack.ReferenceCoverage!)
            {
                var army = ArmyList.New(scope.Faction) with { Detachment = scope.Detachment, Detachments = [scope.Detachment] };
                var actual = ArmyReferences.ForArmy(army, updated).Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
                var expected = pack.ReferenceCards!.Count(c => c.Faction == scope.Faction && c.Detachment == scope.Detachment);
                Check(actual.Count == expected, $"{scope.Faction}/{scope.Detachment}: expected {expected}, got {actual.Count}");
                Check(actual.All(c => c.Detachment == scope.Detachment), "Unselected cards leaked");
                Check(ArmyReferences.ForArmy(army, updated).Count(ArmyReferences.IsCore) == 10, "Core rules missing");
            }
            Check(ReferenceEquals(updated, ReferenceLibraryRecovery.Supplement(updated, pack)), "Repeat startup changes library");
        });
        test("Shared recovery preserves newer cards and removed identities", () =>
        {
            var originalCard = pack.ReferenceCards!.First();
            var changed = originalCard with { Text = "A newer user-imported rule", Details = originalCard.Details! with { Effect = "Newer effect" } };
            var removedCard = pack.ReferenceCards![1];
            var removed = removedCard with { Details = removedCard.Details! with { Status = "Removed" } };
            var current = baseline with { ReferenceCards = [.. baseline.ReferenceCards!, changed, removed] };
            var updated = ReferenceLibraryRecovery.Supplement(current, pack);
            Check(updated.ReferenceCards!.Contains(changed) && updated.ReferenceCards.Contains(removed), "Existing rule replaced");
            foreach (var original in current.ReferenceCards!)
                Check(updated.ReferenceCards.Contains(original), "Existing reference changed");
            var army = ArmyList.New(changed.Faction) with { Detachment = changed.Detachment };
            var visible = ArmyReferences.ForArmy(army, updated);
            Check(visible.Any(c => c.Name == changed.Name && c.Text == changed.Text), "Newer rule not shown");
            Check(visible.All(c => c.Name != removed.Name), "Removed rule resurrected");
            Check(!ReferenceLibraryRecovery.HasCompleteCoverage(updated, removed.Faction, removed.Detachment), "Incomplete scope claims complete coverage");
        });
        test("Shared recovery skips other games and editions", () =>
        {
            var oldEdition = baseline with { Source = baseline.Source with { Edition = "10" } };
            var otherGame = baseline with { Game = "Other game" };
            Check(ReferenceEquals(oldEdition, ReferenceLibraryRecovery.Supplement(oldEdition, pack)), "Cross-edition import");
            Check(ReferenceEquals(otherGame, ReferenceLibraryRecovery.Supplement(otherGame, pack)), "Cross-game import");
        });
        test("Reference recovery preserves a custom catalogue sharing an official faction name", () =>
        {
            var custom = LoadoutChecks.Fixture() with { Detachments = [new("Death Guard", "Custom detachment")] };
            var updated = ReferenceLibraryRecovery.Supplement(custom, pack);
            Check(DataJson.Write(updated.Detachments) == DataJson.Write(custom.Detachments), "Official definitions injected into a custom catalogue");
            Check(!DetachmentRules.UsesPoints("Death Guard", updated), "Recovery enabled DP validation for a custom catalogue");
            Check(DataJson.Write(updated.Units) == DataJson.Write(custom.Units), "Custom units changed");
        });
        test("Shared recovery respects omissions in an existing authoritative complete source", () =>
        {
            var declaration = pack.ReferenceCoverage!.First();
            var current = baseline with { ReferenceCoverage = [declaration] };
            var updated = ReferenceLibraryRecovery.Supplement(current, pack);
            Check(updated.ReferenceCards!.All(c => !ReferenceMerge.OwnedScope(ReferenceModel.Canonical(c, current.Source), declaration)),
                "Bundled cards overrode an existing complete source");
            Check(updated.ReferenceCoverage!.Contains(declaration), "Existing coverage changed");
        });
        test("Complete zero-card coverage does not claim missing Stratagems on either platform", () =>
        {
            var updated = ReferenceLibraryRecovery.Supplement(baseline, pack);
            var empty = pack.ReferenceCoverage!.Where(s => !pack.ReferenceCards!.Any(c => c.Faction == s.Faction && c.Detachment == s.Detachment)).ToList();
            Check(empty.Count > 0, "No current zero-card coverage scopes exercised");
            Check(empty.All(s => ReferenceLibraryRecovery.HasCompleteCoverage(updated, s.Faction, s.Detachment)), "Complete zero-card coverage missing");
            Check(empty.All(s => ArmyReferences.MissingStratagemDetachments(
                DetachmentRules.WithSelection(ArmyList.New(s.Faction), [s.Detachment]), updated).Count == 0), "False missing-card warning");
            var unknown = DetachmentRules.WithSelection(ArmyList.New("Custom faction"), ["Custom detachment"]);
            Check(ArmyReferences.MissingStratagemDetachments(unknown, updated).Count == 1, "Unknown coverage was hidden");
        });
        test("Reference upgrade persists, preserves saved army and context, and is idempotent after reopening", () =>
        {
            var directory = Path.Combine(root, "reference-upgrade");
            string saved, context, units;
            using (var repository = WindowsServices.OpenStore(directory))
            {
                repository.Import(baseline with { ReferenceCards = baseline.ReferenceCards!.Where(c => c.Section != "Stratagems").ToList() });
                var data = repository.LoadDataset()!;
                var unit = data.Units.First(u => u.Faction == "Adepta Sororitas" && u.Options.Count > 0);
                var army = ArmyList.New(unit.Faction) with { Name = "Upgrade preservation", Detachment = "Hallowed Martyrs",
                    Entries = [new("saved-entry", unit.Key, unit.Name, unit.Options[0].Models)] };
                repository.Save(army, new(army.Id, "saved-entry", Screen: "rules"));
                saved = DataJson.Write(repository.LoadArmies()); context = DataJson.Write(repository.LoadContext()); units = DataJson.Write(data.Units);
                Check(ReferenceLibraryRecovery.Restore(repository), "Upgrade did not run");
                Check(DataJson.Write(repository.LoadArmies()) == saved, "Army changed during upgrade");
                Check(DataJson.Write(repository.LoadContext()) == context, "Context changed during upgrade");
                Check(DataJson.Write(repository.LoadDataset()!.Units) == units, "Persisted unit data changed");
            }
            using var reopened = WindowsServices.OpenStore(directory);
            Check(!ReferenceLibraryRecovery.Restore(reopened), "Repeated upgrade wrote another snapshot");
            Check(DataJson.Write(reopened.LoadArmies()) == saved && DataJson.Write(reopened.LoadContext()) == context, "Reopen lost army/context");
            Check(ArmyReferences.ForArmy(reopened.LoadArmies().Single(), reopened.LoadDataset())
                .Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 6, "Persisted selected cards missing");
        });
        test("Default Windows startup recovers all selectable detachments at every game size and survives faction caches and imports", () =>
        {
            using var store = LibraryBootstrap.Open(new WindowsPaths(Path.Combine(root, "windows-all-stratagems")), new WindowsSqliteConnectionFactory());
            var savedArmy = DetachmentRules.WithSelection(ArmyList.New("Necrons") with { Name = "Incursion", Limit = 1000 }, ["Canoptek Court"]);
            store.Save(savedArmy, new(savedArmy.Id, Screen: "rules"));
            var saved = DataJson.Write(store.LoadArmies()); var context = DataJson.Write(store.LoadContext());
            // No Android loader or explicit recovery call: this is the Windows session path.
            var session = new ArmySession(store, useFactionLoading: true);
            var full = session.LoadFullDataset()!;
            var report = new List<object>(); var total = 0; var incursion = 0; var zero = 0;
            foreach (var faction in session.Factions)
            {
                var empty = DetachmentRules.WithSelection(ArmyList.New(faction) with { Limit = 1000 }, []);
                var view = session.DatasetForArmy(empty)!;
                foreach (var definition in DetachmentRules.Options(empty, view))
                {
                    var army = DetachmentRules.WithSelection(empty, [definition.Name]);
                    var expected = ArmyReferences.ForArmy(army, full).Where(c => c.Section == "Stratagems").ToList();
                    var count = expected.Count(c => !ArmyReferences.IsCore(c));
                    var coverage = ReferenceCoverageAudit.ForDetachment(view, DatasetSnapshot.For(view).References, faction, definition.Name);
                    Check(coverage.Coverage != "Unknown", $"{faction}/{definition.Name}: no coverage");
                    Check(count > 0 || coverage.Coverage is "Complete" or "VerifiedComplete", $"{faction}/{definition.Name}: missing cards");
                    Check(expected.Count(ArmyReferences.IsCore) == 10, $"{faction}/{definition.Name}: missing core cards");
                    Check(expected.Where(c => !ArmyReferences.IsCore(c)).All(c => c.Source != null && c.DisplayText.Length > 0), "Empty/unattributed card");
                    foreach (var limit in new[] { 1000, 2000, 3000 })
                    {
                        var actual = ArmyReferences.ForArmy(army with { Limit = limit }, view).Where(c => c.Section == "Stratagems");
                        Check(DataJson.Write(actual.ToList()) == DataJson.Write(expected), $"{faction}/{definition.Name}: cards lost at {limit} or in faction cache");
                    }
                    Check(ArmyReferences.MissingStratagemDetachments(army, view).Count == 0, $"{faction}/{definition.Name}: false missing warning");
                    var available = DetachmentRules.CanAdd(empty, definition.Name, view);
                    Check(available == (DetachmentRules.Cost(definition) is int cost && cost <= 3),
                        $"{faction}/{definition.Name}: single-detachment Incursion availability disagrees with its cost");
                    total++; if (available) incursion++; if (count == 0) zero++;
                    report.Add(new { Faction = faction, Detachment = definition.Name, Stratagems = count, Incursion = available, coverage.Coverage });
                }
            }
            Check(total == 416 && incursion > 370 && zero == 3, $"Unexpected coverage universe: {total} total, {incursion} Incursion, {zero} complete empty sets");
            File.WriteAllText(Path.Combine(root, "windows-stratagem-coverage.json"), DataJson.Write(report));
            Check(DataJson.Write(store.LoadArmies()) == saved && DataJson.Write(store.LoadContext()) == context, "Startup changed saved armies/context");
            var revision = store.LibraryRevision();
            var warm = new ArmySession(store, useFactionLoading: true);
            Check(warm.FactionLoading!.FullReads == 0 && store.LibraryRevision() == revision, "Warm startup rewrote library or rebuilt cache");
            Check(ArmyReferences.ForArmy(savedArmy, warm.DatasetForArmy(savedArmy)).Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 6,
                "Cached startup lost Necron cards");
            var multiple = DetachmentRules.WithSelection(ArmyList.New("Ultramarines") with { Limit = 1000 }, ["Assault Brethren", "Tactical Brethren"]);
            Check(ArmyReferences.ForArmy(multiple, warm.DatasetForArmy(multiple)).Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 6,
                "Combined Space Marine detachments lost cards");
            // Simulate replacing the catalogue with a source that supplies no references or completeness claim.
            var imported = store.LoadDataset()!;
            store.ApplyImport(imported with {
                ReferenceCards = imported.ReferenceCards!.Where(c => c.Faction != "Necrons" || c.Detachment != "Canoptek Court").ToList(),
                ReferenceCoverage = imported.ReferenceCoverage!.Where(c => c.Faction != "Necrons" || c.Detachment != "Canoptek Court").ToList()
            });
            warm.ReloadLibrary();
            Check(ArmyReferences.ForArmy(savedArmy, warm.DatasetForArmy(savedArmy)).Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 6,
                "Import/reload failed to rerun recovery and invalidate the faction cache");
            Check(DataJson.Write(store.LoadArmies()) == saved && DataJson.Write(store.LoadContext()) == context, "Recovery after import changed saved armies/context");
        });
    }
}
