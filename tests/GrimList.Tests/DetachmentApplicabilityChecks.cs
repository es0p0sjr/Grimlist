using GrimList.Application;
using GrimList.Core;
using System.Text.Json.Nodes;

internal static class DetachmentApplicabilityChecks
{
    public static void Run(Action<string, Action> test, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var source = new SourceInfo("Synthetic", "1", "", "", Edition: "11", ContentHash: "hash");
        test("Detachment link-local cost overrides definition and resolves cost type IDs", () =>
        {
            var doc = new BsDataDocument("example.json", JsonNode.Parse("""
            {"id":"catalogue","name":"Example","costTypes":[{"id":"dp","name":"Detachment Points"}],
            "sharedSelectionEntries":[{"id":"definition","name":"Shared","type":"upgrade","costs":[{"typeId":"dp","value":1}]}],
            "selectionEntryGroups":[{"id":"group","name":"Detachments","entryLinks":[
              {"id":"one","name":"Linked","targetId":"definition","type":"selectionEntry","costs":[{"typeId":"dp","value":3.0}]},
              {"id":"two","name":"Inherited","targetId":"definition","type":"selectionEntry"}]}]}
            """)!.AsObject());
            var data = BsDataImporter.ImportReferences(doc, source);
            Check(DetachmentRules.Cost(data.Detachments!.Single(d => d.Name == "Linked")) == 3, "Link cost lost");
            Check(DetachmentRules.Cost(data.Detachments!.Single(d => d.Name == "Inherited")) == 1, "Definition cost lost");
        });
        test("Detachment applicability evaluates complete context, boolean conditions and unavailable semantics", () =>
        {
            var data = new Dataset(source, [], Detachments: [new("Example", "Restricted", Cost: "2 DP", Applicability:
                new("and", Children: [new("catalogue", "cat"), new("factionKeyword", "SHARED"), new("not", Children: [new("factionKeyword", "EXCLUDED")]),
                    new("or", Children: [new("detachment", "Partner"), new("catalogue", "other")])]))], FactionContexts: [new("Example", "cat", ["SHARED"])]);
            var army = DetachmentRules.WithSelection(ArmyList.New("Example"), ["Partner"]);
            Check(DetachmentRules.Options(army, data).Count == 1, "Complete context ignored");
            Check(DetachmentRules.Options(army with { Detachments = [] }, data).Count == 0, "Selection condition ignored");
            Check(DetachmentRules.Options(army, data with { FactionContexts = [new("Example", "cat", ["SHARED", "EXCLUDED"])] }).Count == 0, "Exclusion ignored");
            var unknown = data with { Detachments = [data.Detachments![0] with { Applicability = new("unknown") }] };
            Check(DetachmentRules.Options(army, unknown).Count == 0, "Unknown restrictions treated as permission");
            Check(DetachmentRules.Issues(DetachmentRules.WithSelection(army, ["Restricted"]), unknown).Any(s => s.Contains("catalogue")), "No explanation for saved illegal selection");
        });
        test("Bundled source detachment costs and chapter applicability survive legacy recovery and JSON", () =>
        {
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip"));
            var bytes = File.ReadAllBytes(fixture);
            var imported = BsDataImporter.ImportDetachmentMetadata(BsDataDocument.ReadArchive(bytes), source with { ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) });
            var legacy = imported with { FactionContexts = null, Detachments = imported.Detachments!.Select(d => d with { Applicability = null, Cost = d.Name == "Armoured Speartip" ? "" : d.Cost }).ToList() };
            var repaired = DetachmentMetadataRecovery.ApplyBundled(legacy);
            var data = DataJson.Read<Dataset>(DataJson.Write(repaired));
            foreach (var faction in new[] { "Space Marines", "Imperial Fists", "Blood Angels", "Black Templars", "Dark Angels", "Space Wolves" })
            {
                var options = DetachmentRules.Options(faction, data);
                Check(options.Any(d => d.Name == "Gladius Task Force"), faction + " lost generic detachment");
                Check(DetachmentRules.Cost(options.Single(d => d.Name == "Armoured Speartip")) == 3, faction + " missing DP cost");
                Check(options.Any(d => d.Name == "Angelic Inheritors") == (faction == "Blood Angels"), faction + " wrong exclusive detachment");
            }
            foreach (var (faction, name) in new[] { ("Black Templars", "Wrathful Procession"), ("Dark Angels", "Unforgiven Task Force"), ("Space Wolves", "Saga of the Hunter") })
            {
                Check(DetachmentRules.Options(faction, data).Any(d => d.Name == name), faction + " lost exclusive detachment");
                Check(!DetachmentRules.Options("Imperial Fists", data).Any(d => d.Name == name), name + " leaked to unrelated catalogue");
            }
            Check(legacy.FactionContexts == null && legacy.Detachments!.All(d => d.Applicability == null), "Recovery mutated source");
            Check(DataJson.Write(DetachmentMetadataRecovery.ApplyBundled(repaired)) == DataJson.Write(repaired), "Recovery not idempotent");
            var unmatched = legacy with { Source = source, Detachments = legacy.Detachments!.Select(d => d with { Source = source }).ToList() };
            Check(ReferenceEquals(unmatched, DetachmentMetadataRecovery.ApplyBundled(unmatched)), "Unrelated source repaired");
            File.WriteAllText(Path.Combine(root, "detachment-applicability.json"), DataJson.Write(data));
        });
    }
}
