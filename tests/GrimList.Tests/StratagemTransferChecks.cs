using System.Text;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;

internal static class StratagemTransferChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var source = new SourceInfo("Reference transfer fixture", "1", "", "", Edition: "11");
        var card = new ReferenceCard("Fixture faction", "Stratagems", "Fixture tactic", "WHEN: Your turn. TARGET: One unit. EFFECT: Fixture.", "Alpha", "1 CP", source);
        var library = new Dataset(source, [new("Fixture faction", "Fixture unit", [new(1, 50)], Profile: new(Keywords: ["Infantry"]))],
            Detachments: [new("Fixture faction", "Alpha"), new("Fixture faction", "Beta")], ReferenceCards: [card]);
        test("Points-only preview explicitly skips stratagems; combined import restores them and retains them on BSData updates", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "stratagem-layers"));
            store.Import(library with { ReferenceCards = [] });
            var workflow = new LibraryWorkflow(store);
            var army = ArmyList.New("Fixture faction") with { Detachment = "Alpha" };
            var points = workflow.Prepare(library, ImportLayer.Points);
            Check(points.Stratagems!.Message.Contains("0 included (Points only)") && points.Warnings.Any(w => w.Contains("skips all 1")), "Points-only omission hidden");
            workflow.Apply(points);
            Check(ArmyReferences.MissingStratagemDetachments(army, store.LoadDataset()).Count == 1, "Points-only unexpectedly imported cards");
            var combined = workflow.Prepare(library, ImportLayer.Combined);
            Check(combined.Stratagems!.Coverage.Any(c => c.Contains("Alpha: 1")), "Preview missing detachment coverage");
            workflow.Apply(combined);
            var bsdata = library with { Source = source with { Name = "BSData fixture" }, ReferenceCards = [] };
            workflow.Apply(workflow.Prepare(bsdata, ImportLayer.Combined));
            Check(ArmyReferences.ForArmy(army, store.LoadDataset()).Any(c => c with { Details = null } == card), "Subsequent BSData import lost supplemental reference");
            Check(ArmyReferences.ForArmy(army with { Detachment = "Beta" }, store.LoadDataset()).All(c => c.Name != card.Name), "Unselected detachment card leaked");
        });
        test("Stratagem preview distinguishes mismatched editions without advertising hidden cards", () =>
        {
            var mixed = library with { ReferenceCards = [card, card with { Name = "Old", Source = source with { Edition = "10" } }] };
            var summary = StratagemImportSummary.Create(mixed, mixed, ImportLayer.Combined);
            Check(summary.Coverage.Any(c => c.Contains("Alpha: 1 stratagem cards")) && summary.Warnings.Single().Contains("another edition"), "Wrong-edition cards advertised as available");
        });
        test("Reference-only stream import preserves units, army context and data until explicit apply and after reopening", () =>
        {
            var pack = library with { Units = [], Detachments = [], ReferenceOnly = true };
            VerifyTransfer(library, pack, root, "stratagem-reference-stream", "Fixture faction", "Alpha", 1);
        });
        var index = Array.IndexOf(args, "--stratagem-library");
        if (index >= 0 && index + 1 < args.Length)
            test("Real Windows stratagem pack restores all six Virulent Vectorium cards through the mobile shared workflow", () =>
            {
                var data = DataJson.Read<Dataset>(File.ReadAllText(args[index + 1]));
                var packIndex = Array.IndexOf(args, "--stratagem-pack");
                Check(packIndex >= 0 && packIndex + 1 < args.Length, "Provide the actual phone reference pack");
                var pack = DataJson.Read<Dataset>(File.ReadAllText(args[packIndex + 1]));
                Check(pack.ReferenceOnly && pack.Units.Count == 0 && pack.Detachments!.Count == 0, "Pack would replace unit/detachment data");
                Check(pack.ReferenceCards!.Count == 45, "Expected exact existing Windows Death Guard references");
                var expected = data.ReferenceCards!.Where(c => c.Section == "Stratagems" && c.Faction == "Death Guard").ToList();
                Check(DataJson.Write(expected) == DataJson.Write(pack.ReferenceCards), "Pack differs from existing Windows-exported cards");
                VerifyTransfer(data, pack, root, "real-stratagem-transfer", "Death Guard", "Virulent Vectorium", 6);
            });
    }

    private static void VerifyTransfer(Dataset data, Dataset pack, string root, string folder, string faction, string detachment, int expectedCount)
    {
        var path = Path.Combine(root, folder);
        string armyId, savedArmy, savedContext, savedUnits;
        using (var workspace = new ArmyWorkspace(WindowsServices.OpenStore(path)))
        {
            var without = data with { ReferenceCards = data.ReferenceCards!.Where(c => c.Section != "Stratagems").ToList() };
            workspace.Repository.ApplyImport(without);
            workspace.Session.ReloadLibrary();
            var unit = workspace.Session.Dataset!.Units.First(u => u.Faction == faction && u.Options.Count > 0);
            var army = ArmyList.New(faction) with { Detachment = detachment,
                Entries = [new("transfer-entry", unit.Key, unit.Name, unit.Options[0].Models)] };
            armyId = army.Id;
            workspace.Repository.Save(army, new(army.Id, "transfer-entry", Screen: "equipment"));
            workspace.Session.Restore();
            savedArmy = DataJson.Write(workspace.Repository.LoadArmies());
            savedContext = DataJson.Write(workspace.Repository.LoadContext());
            savedUnits = DataJson.Write(workspace.Session.Dataset.Units);
            Check(ArmyReferences.MissingStratagemDetachments(army, workspace.Session.Dataset).Contains(detachment), "Fixture already has stratagems");
            using var document = new PickedDocument("rules.json", new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(pack)), writable: false));
            var parsed = workspace.Library.ReadDocumentAsync(document, data.Source.Edition).GetAwaiter().GetResult();
            var preview = workspace.Library.Prepare(parsed.Dataset, ImportLayer.Profiles);
            Check(ArmyReferences.MissingStratagemDetachments(army, workspace.Repository.LoadDataset()).Contains(detachment), "Preview applied changes before confirmation");
            Check(preview.Prices.Count == 0 && preview.Armies.Count == 0, "Reference transfer changed points or validation");
            workspace.ApplyLibraryAsync(preview).GetAwaiter().GetResult();
            Check(ArmyReferences.ForArmy(workspace.Session.Army, workspace.Session.Dataset)
                .Count(c => c.Section == "Stratagems" && c.Detachment == detachment) == expectedCount, "Live mobile workspace was not refreshed");
        }
        using var restored = new ArmyWorkspace(WindowsServices.OpenStore(path));
        Check(restored.Session.Restore() && restored.Session.Army.Id == armyId, "Army context failed to restore");
        Check(DataJson.Write(restored.Repository.LoadArmies()) == savedArmy, "Saved army changed");
        Check(DataJson.Write(restored.Repository.LoadContext()) == savedContext, "Editor context changed");
        Check(DataJson.Write(restored.Session.Dataset!.Units) == savedUnits, "Unit data changed");
        Check(ArmyReferences.MissingStratagemDetachments(restored.Session.Army, restored.Session.Dataset).Count == 0, "Reopened library lost reference cards");
    }
}
