using GrimList.Core;

internal static class MultiDispositionChecks
{
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid metadata accepted"); }
    public static void Run(Action<string, Action> test, string root)
    {
        var source = new SourceInfo("Disposition test fixture", "1", "2026-10-02", "", Edition: "11");
        var multi = new DetachmentDefinition("Death Guard", "Dual disposition fixture", "Take and Hold", "3 DP", ["Take and Hold", "Purge the Foe"], source);
        var single = new DetachmentDefinition("Death Guard", "Single disposition fixture", "Purge the Foe", "1 DP");
        var data = new Dataset(source, [new("Death Guard", "Fixture unit", [new(1, 50)])], Detachments: [multi, single]);
        var army = ArmyList.New("Death Guard") with { Detachments = [multi.Name] };
        test("One detachment unlocks all its dispositions while preserving one selected army disposition", () =>
        {
            Check(DetachmentRules.Dispositions(army, data).SequenceEqual(["Purge the Foe", "Take and Hold"]), "Second disposition missing");
            Check(DetachmentRules.NormalizeDisposition(army, data).Disposition == "", "Multiple options selected a disposition without a choice");
            var chosen = army with { Disposition = "Purge the Foe" };
            Check(DetachmentRules.NormalizeDisposition(chosen, data).Disposition == chosen.Disposition, "Second option not retained");
            Check(DetachmentRules.Dispositions(DetachmentRules.WithSelection(army, [multi.Name, single.Name]), data).Count == 2, "Shared options duplicated");
            Check(DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(chosen, [single.Name]), data).Disposition == "Purge the Foe", "Shared option lost on removal");
            Check(DetachmentRules.NormalizeDisposition(DetachmentRules.WithSelection(chosen, []), data).Disposition == "", "Removed options remained selected");
            Check(new DetachmentDefinition("Death Guard", "Legacy", "Reconnaissance").DispositionOptions.SequenceEqual(["Reconnaissance"]), "Legacy format broken");
            Check((multi with { Dispositions = ["Take and Hold", " take and hold ", "Purge the Foe"] }).DispositionOptions.Count == 2, "Duplicate metadata options not normalised");
        });
        test("Multiple disposition metadata and the second selected option persist in SQLite and survive points updates", () =>
        {
            var path = Path.Combine(root, "multi-disposition-persistence");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(data); store.Save(army with { Disposition = "Purge the Foe" });
                var points = data with { Units = [data.Units[0] with { Options = [new(1, 65)] }], Detachments = null };
                store.ApplyImport(store.PreviewImport(points, ImportLayer.Points).Dataset);
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                var loaded = store.LoadDataset()!; var saved = store.LoadArmies().Single();
                Check(loaded.Detachments!.Single(d => d.Name == multi.Name).Dispositions!.SequenceEqual(multi.Dispositions!), "Options lost in database");
                Check(loaded.Detachments!.Single(d => d.Name == multi.Name).Source == source, "Metadata provenance lost");
                Check(saved.Disposition == "Purge the Foe" && DetachmentRules.NormalizeDisposition(saved, loaded).Disposition == saved.Disposition, "Second selection lost on reload");
                Check(ArmyCalculator.Export(saved, loaded).Contains("Disposition: Purge the Foe"), "Export lost chosen disposition");
            }
        });
        test("MFM detachment import reads every disposition and rejects missing, duplicate or unknown metadata", () =>
        {
            const string text = "DETACHMENTS\nDUAL FIXTURE 3DP\nTAKE AND HOLD\nPURGE THE FOE\nENHANCEMENTS\nExample 15 pts\nSINGLE FIXTURE\n1DP\nRECONNAISSANCE\nENHANCEMENTS\nExample 10 pts";
            var parsed = MfmDetachmentImporter.ParseText(text, "Death Guard", source);
            Check(parsed.Detachments!.Count == 2 && parsed.Detachments[0].DispositionOptions.Count == 2, "MFM second disposition truncated");
            Check(parsed.ReferenceCards!.Count == 3, "Disposition reference cards missing");
            var merged = DatasetMerge.Prepare(data, parsed, ImportLayer.Profiles).Dataset;
            Check(DataJson.Write(data.Units) == DataJson.Write(merged.Units), "Metadata import changed units");
            Check(MfmDetachmentImporter.ParseText(text.Replace("TAKE AND HOLD\nPURGE THE FOE", "Force Dispositions: Take and Hold / Purge the Foe"), "Death Guard", source).Detachments![0].DispositionOptions.Count == 2, "Inline options not parsed");
            Reject(() => MfmDetachmentImporter.ParseText(text.Replace("PURGE THE FOE", "UNKNOWN DISPOSITION"), "Death Guard", source));
            Reject(() => MfmDetachmentImporter.ParseText("EMPTY 3DP\nENHANCEMENTS", "Death Guard", source));
            Reject(() => MfmDetachmentImporter.ParseText(text + "\nDUAL FIXTURE 3DP\nTAKE AND HOLD", "Death Guard", source));
        });
        test("BSData importer retains every disposition category for a detachment", () =>
        {
            const string json = """
            {"catalogue":{"id":"fixture","name":"Chaos - Death Guard","selectionEntryGroups":[{"name":"Detachment","selectionEntries":[
              {"id":"dual","name":"Dual fixture","type":"upgrade","categoryLinks":[{"name":"Take and Hold"},{"name":"Purge the Foe"},{"name":"3DP Detachment"}],"costs":[{"name":"Detachment Points","value":3}]}
            ]}]}}
            """;
            var imported = BsDataImporter.ImportReferences(BsDataDocument.Parse("fixture.json", json), source);
            Check(imported.Detachments!.Single().DispositionOptions.SequenceEqual(["Take and Hold", "Purge the Foe"]), "BSData importer still keeps only first disposition");
            Check(imported.ReferenceCards!.Count(c => c.Section == "Dispositions") == 2, "BSData second disposition card missing");
        });
        File.WriteAllText(Path.Combine(root, "multi-disposition-fixture.json"), DataJson.Write(data));
    }
}
