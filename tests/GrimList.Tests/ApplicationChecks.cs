using GrimList.Core;

internal static class ApplicationChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException) { return; }
        throw new Exception("Rejected operation unexpectedly succeeded.");
    }
    private static Dataset Fixture()
    {
        var data = LoadoutChecks.Fixture();
        var squad = data.Units.Single();
        var leader = new UnitDefinition(squad.Faction, "Session leader", [new(1, 50)], Category: "Characters",
            Profile: new(Keywords: ["Character", "Leader"], Abilities: [new("Leader", $"This model can be attached to the following units:\n• {squad.Name}."),
                new("Leader", "Each bodyguard unit can only have one leader unit and one support unit attached to it.")]));
        var support = leader with { Name = "Session support", Profile = leader.Profile! with { Keywords = ["Character", "Support"] } };
        return data with { Units = [squad, leader, support], Detachments = [new(squad.Faction, "Session detachment")],
            ReferenceCards = [new(squad.Faction, "Enhancements", "Session enhancement", "Death Guard model only.", "Session detachment", "15 pts")] };
    }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Application points-limit edit recalculates, persists and supports undo", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "limit-edit")); store.Import(Fixture());
            var session = new ArmySession(store); session.Create("Death Guard", "Limit edit", 2000, ["Session detachment"]);
            session.AddUnit(session.Dataset!.Units.First().Key, 5);
            session.SetMetadata("Limit edit", 50);
            Check(session.Result.Issues.Any(i => i.Contains("over", StringComparison.OrdinalIgnoreCase)), "Limit change did not validate");
            Check(store.LoadArmies().Single().Limit == 50, "Limit was not committed");
            session.Undo(); Check(session.Army.Limit == 2000, "Limit undo failed");
            session.Redo(); Check(session.Army.Limit == 50, "Limit redo failed");
        });
        test("Application can remove an enhancement orphaned by a library update", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "orphan-enhancement")); store.Import(Fixture());
            var session = new ArmySession(store); session.Create("Death Guard", "Orphan", 2000, ["Session detachment"]);
            session.AddUnit(session.Dataset!.Units.Single(u => u.Name == "Session leader").Key, 1);
            var id = session.Context.EntryId!;
            session.SelectEnhancement(id, new("Session detachment", "Session enhancement"));
            // Simulate a retained selection whose imported card is unavailable.
            var orphan = session.Army with { Entries = session.Army.Entries.Select(e => e with { Enhancement = new("Removed detachment", "Removed card") }).ToList() };
            store.Save(orphan);
            session.Open(orphan);
            session.SelectEnhancement(id, null);
            Check(session.Army.Entries.Single().Enhancement == null && store.LoadArmies().Single().Entries.Single().Enhancement == null, "Orphan could not be removed");
        });
        test("Indexed library merge retains alias ambiguity and same-batch collision checks", () =>
        {
            var fixture = Fixture(); var u = fixture.Units.First() with { Id = "one", Aliases = ["old-name"] };
            var current = fixture with { Units = [u] };
            var rename = u with { Name = "Renamed", Aliases = ["old-name"] };
            var merged = DatasetMerge.Prepare(current, fixture with { Units = [rename] }, ImportLayer.Combined).Dataset;
            Check(merged.Units.Single().Id == "one" && merged.Units.Single().Aliases!.Contains("old-name"), "Rename identity lost");
            var other = u with { Id = "two", Name = "Other", Aliases = ["collision"] };
            Reject(() => DatasetMerge.Prepare(fixture with { Units = [u, other] }, fixture with { Units = [rename with { Aliases = ["old-name", "collision"] }] }, ImportLayer.Combined));
            Reject(() => DatasetMerge.Prepare(null, fixture with { Units = [u, other with { Aliases = ["old-name"] }] }, ImportLayer.Combined));
        });
        test("Application session completes equipment, Leader/Support and process recreation workflow", () =>
        {
            var path = Path.Combine(root, "application-lifecycle"); string armyId, squadId, leaderId, supportId;
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(Fixture());
                var session = new ArmySession(store); var data = session.Dataset!;
                Check(session.Factions.Contains("Death Guard"), "Faction discovery failed");
                session.Create("Death Guard", "Shared workflow", 2000, ["Session detachment"]); armyId = session.Army.Id;
                session.AddUnit(data.Units.Single(u => u.Name == "Loadout fixture squad").Key, 5); squadId = session.Context.EntryId!;
                session.ChooseEquipment(squadId, 1, "ranged", "heavy", 1);
                session.AddUnit(data.Units.Single(u => u.Name == "Session leader").Key, 1); leaderId = session.Context.EntryId!;
                session.AddUnit(data.Units.Single(u => u.Name == "Session support").Key, 1); supportId = session.Context.EntryId!;
                Check(session.AttachmentCandidates(leaderId).Any(e => e.Id == squadId), "Bodyguard not offered");
                session.Attach(leaderId, squadId); session.Attach(supportId, squadId); session.SelectWarlord(leaderId);
                session.SelectEnhancement(leaderId, new("Session detachment", "Session enhancement"));
                session.SelectEntry(squadId, 1, "ranged"); session.Navigate("equipment"); session.Save();
                Check(session.Result.Total == 215 && session.Result.Complete, "Equipment/group pricing differs");
                Check(session.Result.Validated, string.Join("; ", session.Result.Issues));
                Check(session.CanUndo && !session.CanRedo, "History not exposed");
            }
            // New connection and session simulate process loss; no old UI/window/state objects survive.
            using (var store = WindowsServices.OpenStore(path))
            {
                var restored = new ArmySession(store);
                Check(restored.Restore(), "Session did not restore");
                Check(restored.Army.Id == armyId && restored.Context.EntryId == squadId && restored.Context.ModelIndex == 1
                    && restored.Context.LoadoutSection == "ranged" && restored.Context.Screen == "equipment", "Editing context lost");
                Check(restored.AttachedEntries.Count == 2 && restored.Army.WarlordEntryId == leaderId, "Group/warlord lost");
                Check(restored.Army.Entries.Single(e => e.Id == supportId).AttachedToId == squadId, "Support lost");
                Check(restored.Result.Total == 215 && restored.Result.Validated, "Restored calculation differs");
                Check(Loadouts.Weapons(restored.Dataset!.FindUnit(restored.SelectedEntry!.UnitKey)!, restored.SelectedEntry.Loadout).Any(w => w.Profile.Name == "Heavy gun"), "Equipment lost");
                Check(!restored.CanUndo && !restored.CanRedo, "Transient history must start empty after process recreation");
            }
        });
        test("Application undo and redo persist complete group and equipment snapshots", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-history")); store.Import(Fixture());
            var session = new ArmySession(store); session.Create("Death Guard", "History", 2000, ["Session detachment"]);
            var unit = session.Dataset!.Units.Single(u => u.Name == "Loadout fixture squad"); session.AddUnit(unit.Key, 5);
            var id = session.Context.EntryId!; session.ChooseEquipment(id, 1, "ranged", "heavy", 1);
            var before = DataJson.Write(session.Army); session.ResizeUnit(id, 10); var after = DataJson.Write(session.Army);
            session.Undo(); Check(DataJson.Write(session.Army) == before && session.CanRedo, "Undo lost selections");
            Check(DataJson.Write(store.LoadArmies().Single()) == before, "Undo was not saved");
            session.Redo(); Check(DataJson.Write(session.Army) == after, "Redo differs");
            session.Undo(); session.ResetLoadout(id); Check(!session.CanRedo, "New edit did not clear redo");
            session.Open(session.Army.Id); Check(!session.CanUndo, "Opening did not clear history");
        });
        test("Rejected edits and failed saves do not change application state or undo history", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-failures")); store.Import(Fixture());
            var faults = new FaultingRepository(store); var session = new ArmySession(faults);
            session.Create("Death Guard", "Failures", 2000, ["Session detachment"]);
            session.AddUnit(session.Dataset!.Units.First(u => u.Name == "Loadout fixture squad").Key, 5);
            var id = session.Context.EntryId!; session.ChooseEquipment(id, 1, "ranged", "heavy", 1);
            var before = DataJson.Write(session.Army); var context = session.Context;
            Reject(() => session.ChooseEquipment(id, 2, "ranged", "heavy", 1));
            Check(DataJson.Write(session.Army) == before, "Invalid equipment mutated session");
            faults.FailSave = true;
            Reject(() => session.RemoveUnit(id)); Reject(session.Undo);
            Check(DataJson.Write(session.Army) == before && session.Context == context && session.CanUndo && !session.CanRedo, "Save failure mutated state/history");
            faults.FailSave = false; session.Undo();
            Check(!Loadouts.Weapons(session.Dataset.FindUnit(session.SelectedEntry!.UnitKey)!, session.SelectedEntry.Loadout).Any(w => w.Profile.Name == "Heavy gun"), "Failed write polluted undo stack");
        });
        test("Library service previews without mutation, rejects stale apply and respects cancellation", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-import")); store.Import(Fixture());
            var workflow = new LibraryWorkflow(store); var before = DataJson.Write(store.LoadDataset());
            var incoming = Fixture() with { Source = Fixture().Source with { Version = "2" } };
            var preview = workflow.PrepareAsync(incoming, ImportLayer.Combined).GetAwaiter().GetResult();
            Check(DataJson.Write(store.LoadDataset()) == before, "Preview changed library");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { workflow.ApplyAsync(preview, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled apply succeeded"); }
            catch (OperationCanceledException) { }
            Check(DataJson.Write(store.LoadDataset()) == before, "Cancellation changed library");
            workflow.Apply(preview); Reject(() => workflow.Apply(preview));
        });
        test("Repository rolls back army and editing context together after a mid-save SQLite failure", () =>
        {
            var path = Path.Combine(root, "application-atomic-save");
            using var store = WindowsServices.OpenStore(path); store.Import(Fixture());
            var session = new ArmySession(store); session.Create("Death Guard", "Atomic", 2000);
            session.AddUnit(session.Dataset!.Units.First(u => u.Name == "Loadout fixture squad").Key, 5);
            var before = DataJson.Write(store.LoadArmies()); var context = store.LoadContext();
            using var database = new SqliteDb(Path.Combine(path, "grimlist.db"));
            database.Run("CREATE TRIGGER reject_session_leader BEFORE INSERT ON entries WHEN NEW.name='Session leader' BEGIN SELECT RAISE(ABORT, 'injected entry failure'); END");
            Reject(() => session.AddUnit(session.Dataset.Units.Single(u => u.Name == "Session leader").Key, 1));
            Check(DataJson.Write(store.LoadArmies()) == before && store.LoadContext() == context && session.Context == context,
                "A partial save changed the army or selected entry");
        });
        test("Shared document import uses streams and the exact BSData importer", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-stream"));
            var workflow = new LibraryWorkflow(store); var data = Fixture();
            using var document = new PickedDocument("library.json", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(DataJson.Write(data))));
            var read = workflow.ReadDocumentAsync(document, "11").GetAwaiter().GetResult();
            Check(DataJson.Write(read.Dataset) == DataJson.Write(data), "Structured rules changed during stream import");
            var preview = workflow.PrepareAsync(read.Dataset, ImportLayer.Combined).GetAwaiter().GetResult();
            workflow.ApplyAsync(preview).GetAwaiter().GetResult();
            Check(store.LoadDataset()!.Units.Count == data.Units.Count, "Stream library not saved");
        });
        test("Shared stream import accepts full-library-sized JSON without applying it", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-large-document"));
            var data = Fixture();
            var json = System.Text.Encoding.UTF8.GetBytes(DataJson.Write(data));
            var bytes = new byte[51_000_000]; Array.Fill(bytes, (byte)' '); json.CopyTo(bytes, 0);
            using var document = new PickedDocument("exported-library.json", new MemoryStream(bytes, writable: false));
            var parsed = new LibraryWorkflow(store).ReadDocumentAsync(document, "11").GetAwaiter().GetResult();
            Check(DataJson.Write(parsed.Dataset) == DataJson.Write(data), "Large document changed imported rules");
            Check(store.LoadDataset() == null, "Reading a large document applied it without confirmation");
        });
        test("Application recreation handles deleted armies and stale selected entries", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-stale-context")); store.Import(Fixture());
            var session = new ArmySession(store); session.Create("Death Guard", "Context", 2000);
            store.SaveContext(new(session.Army.Id, "deleted-entry", 99, "old", "equipment"));
            var next = new ArmySession(store); Check(next.Restore(), "Army not restored");
            Check(next.Context.EntryId == null && next.Context.ModelIndex == null, "Stale selection not cleared");
            store.Delete(next.Army.Id); Check(!new ArmySession(store).Restore(), "Deleted army restored");
        });
        test("Async BSData archive workflow preserves importer semantics and edition checks", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "application-bsdata"));
            var workflow = new LibraryWorkflow(store); var archive = AllArmyChecks.Zip();
            var imported = workflow.ReadBsDataAsync(archive, "11").GetAwaiter().GetResult();
            var direct = BsDataImporter.ImportAll(BsDataDocument.ReadArchive(archive), imported.Dataset.Source);
            Check(DataJson.Write(imported.Dataset) == DataJson.Write(direct.Dataset), "Application used a different BSData interpretation");
            Reject(() => workflow.ReadBsDataAsync(archive, "10").GetAwaiter().GetResult());
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { workflow.ReadBsDataAsync(archive, "11", cancellation: cancellation.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled parse ran"); }
            catch (OperationCanceledException) { }
            Check(store.LoadDataset() == null, "Acquisition applied a library");
        });
        test("Workspace applies imports and refreshes the shared session before returning", () =>
        {
            var path = Path.Combine(root, "application-workspace");
            using var workspace = ArmyWorkspace.OpenAsync(() => WindowsServices.OpenStore(path)).GetAwaiter().GetResult();
            var preview = workspace.Library.PrepareAsync(Fixture(), ImportLayer.Combined).GetAwaiter().GetResult();
            workspace.ApplyLibraryAsync(preview).GetAwaiter().GetResult();
            Check(workspace.Session.Factions.Contains("Death Guard"), "Session retained old library");
            workspace.Session.Create("Death Guard", "Workspace", 2000);
            workspace.Session.AddUnit(workspace.Session.Dataset!.Units.Single(u => u.Name == "Loadout fixture squad").Key, 5);
            var before = workspace.Session.Result.Total;
            var updated = Fixture() with { Units = Fixture().Units.Select(u => u with { Options = u.Options.Select(o => o with { Points = o.Points + 5 }).ToList() }).ToList() };
            workspace.ApplyLibraryAsync(workspace.Library.Prepare(updated, ImportLayer.Points)).GetAwaiter().GetResult();
            Check(workspace.Session.Result.Total == before + 5 && !workspace.Session.CanUndo, "Library update did not recalculate or clear roster history");
        });
        test("Shared application and persistence assemblies do not reference Windows or WPF", () =>
        {
            foreach (var assembly in new[] { typeof(ArmySession).Assembly, typeof(ArmyStore).Assembly, typeof(ArmyCalculator).Assembly })
                Check(!assembly.GetReferencedAssemblies().Any(a => a.Name!.Contains("Windows") || a.Name.StartsWith("Presentation")), "Windows dependency leaked into " + assembly.GetName().Name);
        });
    }

    private sealed class FaultingRepository(IArmyRepository inner) : IArmyRepository
    {
        public bool FailSave { get; set; }
        public string DirectoryPath => inner.DirectoryPath;
        public Dataset? LoadDataset() => inner.LoadDataset();
        public List<ArmyList> LoadArmies() => inner.LoadArmies();
        public void Save(ArmyList army, EditorContext? context = null)
        { if (FailSave) throw new IOException("Injected save failure"); inner.Save(army, context); }
        public void Delete(string id) => inner.Delete(id);
        public ImportPreview PreviewImport(Dataset data, ImportLayer layer, bool completeFactions = false) => inner.PreviewImport(data, layer, completeFactions);
        public void ApplyImport(Dataset data, string? expectedSnapshot = null) => inner.ApplyImport(data, expectedSnapshot);
        public EditorContext LoadContext() => inner.LoadContext();
        public void SaveContext(EditorContext context) => inner.SaveContext(context);
        public void Dispose() { }
    }
}
