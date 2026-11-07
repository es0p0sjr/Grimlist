using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class NestedDetachmentChecks
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static readonly SourceInfo Source = new("BSData nested fixture", new string('a', 40), "2026-10-26", "https://github.com/BSData/wh40k-11e", Edition: "11", ContentHash: new string('a', 64));
    public static void Run(Action<string, Action> test, string root)
    {
        var game = BsDataDocument.Parse("Game.json", """{"gameSystem":{"id":"game","name":"Game"}}""");
        var document = BsDataDocument.Parse("Example.json", """
            {"catalogue":{"id":"example","name":"Example","gameSystemId":"game","selectionEntryGroups":[
              {"id":"root","name":"Detachments","selectionEntries":[{"id":"outside","name":"Independent","type":"upgrade"}],
               "selectionEntryGroups":[{"id":"limited","name":"An Arbitrary Group","constraints":[
                 {"id":"cap","field":"selections","type":"max","value":1,"scope":"parent"}],
                 "selectionEntries":[{"id":"one","name":"First","type":"upgrade","costs":[{"name":"Detachment Points","value":1}],
                    "rules":[{"id":"rule1","name":"First rule","description":"Test rule."}],
                    "selectionEntries":[{"id":"equipment","name":"Not a detachment","type":"upgrade"}]}],
                 "entryLinks":[{"id":"group-link","name":"Nested linked group","type":"selectionEntryGroup","targetId":"linked"}]}]}],
              "sharedSelectionEntryGroups":[{"id":"linked","name":"Shared group","selectionEntries":[
                {"id":"two","name":"Second","type":"upgrade","costs":[{"name":"Detachment Points","value":1}],
                 "rules":[{"id":"rule2","name":"Second rule","description":"Test rule."}]}]}]}}
            """);
        Dataset Parse(BsDataDocument doc) => BsDataImporter.ImportDetachmentMetadata([doc, game], Source, true);
        test("Nested detachment groups and linked subgroups retain arbitrary group limits without importing equipment", () =>
        {
            var data = Parse(document);
            Check(data.Detachments!.Select(d => d.Name).Order().SequenceEqual(new[] { "First", "Independent", "Second" }), "Discovery boundary");
            Check(data.DetachmentSources!.Single().Active.Count == 3, "Complete scope missing");
            var army = ArmyList.New("Example") with { Detachments = ["First", "Second"] };
            Check(DetachmentRules.CombinationIssues(army, data).Single().Contains("An Arbitrary Group"), "Limit lost");
            Check(!DetachmentRules.CanAdd(army with { Detachments = ["First"] }, "Second", data), "Picker allowed over-limit group");
            Check(DetachmentRules.CombinationIssues(army with { Detachments = ["First", "Independent"] }, data).Count == 0, "Unrelated choice blocked");
            var group = data.Detachments!.Single(d => d.Name == "Second").GroupLimits!.Single();
            Check(group.Id == "limited" && group.Maximum == 1, "Ancestor group lost through link");
            var roundTrip = DataJson.Read<Dataset>(DataJson.Write(data));
            Check(DetachmentRules.CombinationIssues(army, roundTrip).Count == 1, "Serialization lost limit");
        });
        test("Nested visibility stays inherited and preserves scoped rules rather than leaking faction rules", () =>
        {
            var hidden = document with { Root = (JsonObject)document.Root.DeepClone() };
            hidden.Root["selectionEntryGroups"]![0]!["selectionEntryGroups"]![0]!["hidden"] = true;
            hidden.Root["selectionEntryGroups"]!.AsArray().Add(JsonNode.Parse("""
              {"id":"gifts","name":"Enhancements","selectionEntries":[{"id":"gift","name":"Test gift","type":"upgrade","hidden":true,
                "rules":[{"id":"gift-rule","name":"Test gift","description":"Character model only."}],
                "modifiers":[{"field":"hidden","type":"set","value":false,"conditions":[{"scope":"force","childId":"one","type":"equalTo","value":1}]}]}]}
              """));
            var data = Parse(hidden);
            Check(data.DetachmentSources!.Single().Active.Select(a => a.Name).SequenceEqual(["Independent"]), "Hidden parent ignored");
            var hiddenReferences = BsDataImporter.ImportReferences(hidden, Source);
            var army = ArmyList.New("Example") with { Detachment = "First" };
            var bearer = new UnitDefinition("Example", "Captain", [new(1, 100)], Category: "Characters", Profile: new(new(), Keywords: ["Character"]));
            Check(!Enhancements.Eligible(army, bearer, hiddenReferences.ReferenceCards!.Single(c => c.Section == "Enhancements"), hiddenReferences), "Hidden parent's enhancement available");
            Check(new ReferenceIndex(hiddenReferences).ForArmy(army, "11").All(c => c.Detachment != "First"), "Hidden parent's cards visible");
            var conditional = hidden with { Root = (JsonObject)hidden.Root.DeepClone() };
            conditional.Root["selectionEntryGroups"]![0]!["selectionEntryGroups"]![0]!["modifiers"] = JsonNode.Parse("""
              [{"field":"hidden","type":"set","value":false,"conditions":[{"scope":"primary-catalogue","childId":"example","type":"equalTo","value":1}]}]
              """);
            var available = Parse(conditional);
            Check(available.DetachmentSources!.Single().Active.Count == 3 && DetachmentRules.Options("Example", available).Count == 3, "Conditional group hidden permanently");
            var references = BsDataImporter.ImportReferences(conditional, Source);
            Check(Enhancements.Eligible(army, bearer, references.ReferenceCards!.Single(c => c.Section == "Enhancements"), references), "Available parent's enhancement rejected");
            Check(references.ReferenceCards!.Count(c => c.Section == "Detachments") == 2
                && !references.ReferenceCards!.Any(c => c.Section == "Army Rules" && c.Name.EndsWith("rule")), "Nested detachment rules misattributed");
        });
        test("Missing links, cyclic groups and unsupported nested constraints cannot authorize retirement", () =>
        {
            var anonymous = document with { Root = (JsonObject)document.Root.DeepClone() };
            anonymous.Root["selectionEntryGroups"]![0]!.AsObject().Remove("id");
            var local = Parse(anonymous);
            Check(local.DetachmentSources == null && DetachmentRules.Options("Example", local).Count == 3, "ID-less local container lost readable choices or authorized retirement");
            var broken = document with { Root = (JsonObject)document.Root.DeepClone() };
            broken.Root["selectionEntryGroups"]![0]!["selectionEntryGroups"]![0]!["entryLinks"]![0]!["targetId"] = "missing";
            Check(Parse(broken).DetachmentSources == null, "Missing link accepted");
            var cyclic = document with { Root = (JsonObject)document.Root.DeepClone() };
            cyclic.Root["sharedSelectionEntryGroups"]![0]!["entryLinks"] = JsonNode.Parse("""[{"id":"cycle","type":"selectionEntryGroup","targetId":"limited"}]""");
            Check(Parse(cyclic).DetachmentSources == null, "Cycle accepted");
            var unsupported = document with { Root = (JsonObject)document.Root.DeepClone() };
            unsupported.Root["selectionEntryGroups"]![0]!["selectionEntryGroups"]![0]!["constraints"]![0]!["scope"] = "future-scope";
            var data = Parse(unsupported);
            Check(data.DetachmentSources == null && DetachmentRules.Options("Example", data).Select(d => d.Name).SequenceEqual(["Independent"]), "Unsupported limit silently ignored");
            var dynamicLimit = document with { Root = (JsonObject)document.Root.DeepClone() };
            var group = dynamicLimit.Root["selectionEntryGroups"]![0]!["selectionEntryGroups"]![0]!;
            group["constraints"]![0]!["value"] = -1;
            group["modifiers"] = JsonNode.Parse("""[{"field":"cap","type":"set","value":1,"conditions":[{"scope":"force","childId":"outside","type":"equalTo","value":1}]}]""");
            Check(Parse(dynamicLimit).DetachmentSources == null, "Conditional unlimited constraint treated as complete");
        });

        var directory = Path.Combine("tests", "fixtures", "bsdata", "11e", "79c2aff2dcf7663b53e6b1c52fb36a564d5ab054");
        var source = DataJson.Read<SourceInfo>(File.ReadAllText(Path.Combine(directory, "source.json")));
        var archive = File.ReadAllBytes(Path.Combine(directory, "repository.zip"));
        Check(Convert.ToHexString(SHA256.HashData(archive)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase), "Pinned archive hash");
        var documents = BsDataDocument.ReadArchive(archive);
        Dataset? imported = null;
        string[] nested = ["Aquilan Shield", "Dread Host", "Emperor's Chosen", "Emissaries Imperatus", "Shadow Keepers", "Solar Watch"];
        test("October 26 source imports all thirteen Custodes detachments, six nested rule/enhancement scopes and group limits", () =>
        {
            var primary = documents.Single(d => BsDataImporter.FactionName(d) == "Adeptus Custodes");
            var dependencies = new HashSet<string>();
            void Include(string id)
            {
                if (!dependencies.Add(id)) return;
                var doc = documents.Single(d => d.Id == id);
                foreach (var link in BsDataDocument.Children(doc.Root, "catalogueLinks")) Include(BsDataDocument.Text(link, "targetId"));
            }
            Include(primary.Id); Include(BsDataDocument.Text(primary.Root, "gameSystemId"));
            var parsed = BsDataImporter.ImportAll(documents.Where(d => dependencies.Contains(d.Id)).ToList(), source, authoritativeDownload: true).Dataset;
            var scope = parsed.DetachmentSources?.Single(s => s.Faction == "Adeptus Custodes");
            Check(scope?.Active.Count == 13 && parsed.Detachments!.Count(d => d.Faction == "Adeptus Custodes") == 13, "Incomplete real Custodes scope");
            imported = parsed with { DetachmentSources = [scope!], Units = parsed.Units.Where(u => u.Faction == "Adeptus Custodes").ToList(),
                Detachments = parsed.Detachments!.Where(d => d.Faction == "Adeptus Custodes").ToList(),
                ReferenceCards = parsed.ReferenceCards!.Where(c => c.Faction == "Adeptus Custodes").ToList() };
            Check(DetachmentRules.Options("Adeptus Custodes", imported).Count == 13, "Real visibility regression");
            foreach (var name in nested)
            {
                var detachment = imported.Detachments!.Single(d => d.Name == name);
                Check(detachment.Cost == "1 DP" && detachment.GroupLimits?.Single().Maximum == 1, name + " metadata");
                Check(imported.ReferenceCards!.Any(c => c.Detachment == name && c.Section == "Detachments"), name + " missing rules");
                Check(imported.ReferenceCards!.Any(c => c.Detachment == name && c.Section == "Enhancements" && c.Eligibility != null), name + " missing enhancement eligibility");
            }
            var army = ArmyList.New("Adeptus Custodes") with { Detachments = ["Aquilan Shield"] };
            Check(!DetachmentRules.CanAdd(army, "Solar Watch", imported) && DetachmentRules.CanAdd(army, "Auric Champions", imported), "Real combination limit");
            Check(RosterExport.Text(army with { Detachments = ["Aquilan Shield", "Solar Watch"] }, imported).Contains("select at most 1"), "Export missing group issue");
        });
        test("Complete nested import retires five old Custodes definitions and survives preview, persistence and recovery", () =>
        {
            Check(imported != null, "Real import prerequisite");
            var oldDir = Path.Combine("tests", "fixtures", "bsdata", "11e", "cc1830fbfead059f8059ed6c7f8e73cd4c390908");
            var oldSource = DataJson.Read<SourceInfo>(File.ReadAllText(Path.Combine(oldDir, "source.json")));
            var old = BsDataImporter.ImportDetachmentMetadata(BsDataDocument.ReadArchive(File.ReadAllBytes(Path.Combine(oldDir, "repository.zip"))), oldSource);
            var current = imported! with { SchemaVersion = 2, DetachmentSources = null, IsBsDataDownload = false,
                Detachments = old.Detachments!.Where(d => d.Faction == "Adeptus Custodes").ToList() };
            var army = ArmyList.New("Adeptus Custodes") with { Detachment = "Shield Host" };
            using var store = WindowsServices.OpenStore(Path.Combine(root, "nested-detachments"));
            store.ApplyImport(current); store.Save(army);
            var workflow = new LibraryWorkflow(store);
            var preview = workflow.Prepare(imported!, ImportLayer.Combined);
            Check(preview.Changes.Count(c => c.Contains("marked obsolete")) == 5, "Retirement preview");
            workflow.Apply(preview);
            var session = new ArmySession(store, useFactionLoading: true); session.Open(army);
            Check(DetachmentRules.Options(army, session.Dataset).Count == 13, "Recovery lost or resurrected choices");
            Check(session.Dataset!.DetachmentSources!.Single().Retired.Count == 5, "Retirement evidence");
            Check(store.LoadArmies().Single().Detachment == "Shield Host" && session.Result.Issues.Any(i => i.Contains("obsolete")), "Saved obsolete selection lost or not warned");
            Check(DetachmentRules.CombinationIssues(army with { Detachments = ["Dread Host", "Solar Watch"] }, session.Dataset).Count > 0, "Persistence lost constraint");
        });
    }
}
