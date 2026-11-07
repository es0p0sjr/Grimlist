using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;

internal static class SourceEnhancementChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var document = BsDataDocument.Parse("enhancements.json", """
        {"catalogue":{"id":"test-catalogue","name":"Space Marines","gameSystemId":"game",
          "categoryEntries":[{"id":"character","name":"Character"},{"id":"psyker","name":"Psyker"},{"id":"terminator","name":"Terminator"}],
          "sharedSelectionEntries":[{"id":"captain","name":"Captain","type":"model"}],
          "selectionEntryGroups":[
            {"id":"detachments","name":"Detachments","selectionEntries":[{"id":"host","name":"Test Host","type":"upgrade"}]},
            {"id":"enhancements","name":"Enhancements","modifiers":[{"field":"hidden","type":"set","value":true,
              "conditions":[{"scope":"roster","field":"selections","childId":"host","type":"lessThan","value":1}]}],
             "selectionEntries":[{"id":"gift","name":"Test Gift","type":"upgrade","costs":[{"name":"pts","value":15}],
               "profiles":[{"name":"Test Gift","characteristics":[{"$text":"The bearer gains a test ability."}]}],
               "modifiers":[{"field":"hidden","type":"set","value":true,"conditionGroups":[{"type":"or","conditions":[
                 {"scope":"ancestor","field":"selections","childId":"psyker","type":"notInstanceOf","value":1},
                 {"scope":"ancestor","field":"selections","childId":"terminator","type":"instanceOf","value":1}]}]}]}]}
          ]}}
        """);
        var source = new SourceInfo("BSData synthetic enhancements", "1", "", "", Edition: "11", ContentHash: "test-hash");
        var cards = BsDataImporter.ImportEnhancementReferences([document], source);
        var card = cards.Single();
        var unit = new UnitDefinition("Space Marines", "Test Psyker", [new(1, 80)], Category: "Characters",
            Profile: new(Keywords: ["Character", "Psyker"]));
        var army = ArmyList.New(unit.Faction) with { Detachment = "Test Host", Entries = [new("bearer", unit.Key, unit.Name, 1)] };
        var data = new Dataset(source, [unit], ReferenceCards: cards);
        test("Catalogue enhancement conditions enable effect-only cards and preserve keyword exclusions", () =>
        {
            Check(card.Eligibility != null && Enhancements.Options(army, data, unit).Count == 1, "Structured restriction was lost");
            Check(card.Details?.SourceCardId == "gift", "Selectable source entry identity was lost");
            Check(!Enhancements.Eligible(army, unit with { Profile = new(Keywords: ["Character"]) }, card), "Required keyword ignored");
            Check(!Enhancements.Eligible(army, unit with { Profile = new(Keywords: ["Character", "Psyker", "Terminator"]) }, card), "Exclusion ignored");
            Check(!Enhancements.Eligible(army, unit with { Category = "Epic Heroes", Profile = new(Keywords: ["Character", "Psyker", "Epic Hero"]) }, card), "Epic hero allowed");
            Check(!Enhancements.Eligible(army with { Detachment = "Other" }, unit, card), "Inactive detachment allowed");
            Check(!Enhancements.Eligible(army, unit with { Category = "Battleline", Profile = new(Keywords: ["Psyker"]) }, card), "Non-character allowed");
            Check(!Enhancements.Eligible(army, unit, card with { Faction = "Orks" }), "Foreign enhancement allowed");
            Check(!Enhancements.Eligible(army, unit, card with { Eligibility = null }), "Missing restrictions guessed");
            Check(!Enhancements.Eligible(army, unit, card with { Eligibility = new("unknown") }), "Unknown condition granted eligibility");
            Check(!Enhancements.Eligible(army, unit, card with { Text = "TERMINATOR model only. Test." }), "Explicit text restriction ignored");
        });
        test("Source enhancement selection survives shared session undo, redo and SQLite reopen", () =>
        {
            var path = Path.Combine(root, "source-enhancement-session");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(data); var session = new ArmySession(store); session.Open(army);
                session.SelectEnhancement("bearer", new(card.Detachment, card.Name));
                Check(session.Result.Total == 95, "Selection did not add enhancement cost");
                session.Undo(); Check(session.Result.Total == 80, "Undo did not clear enhancement");
                session.Redo(); Check(session.Result.Total == 95, "Redo did not restore enhancement");
            }
            using (var store = WindowsServices.OpenStore(path))
            {
                var session = new ArmySession(store); session.Restore();
                Check(session.Result.Total == 95 && session.SelectedEntry?.Enhancement?.Name == card.Name, "Reopen lost enhancement");
                session.SelectEnhancement("bearer", null); Check(session.Result.Total == 80, "Removal left enhancement cost");
            }
        });
        test("Source enhancement Boolean groups, named bearers and unknown conditions remain distinct", () =>
        {
            var altered = new BsDataDocument(document.FileName, (JsonObject)document.Root.DeepClone());
            var modifier = altered.Root["selectionEntryGroups"]![1]!["selectionEntries"]![0]!["modifiers"]![0]!;
            modifier["conditionGroups"] = JsonNode.Parse("""
            [{"type":"and","conditions":[
              {"scope":"ancestor","field":"selections","childId":"psyker","type":"notInstanceOf","value":1},
              {"scope":"root-entry","field":"selections","childId":"captain","type":"notInstanceOf","value":1}]}]
            """);
            var either = BsDataImporter.ImportEnhancementReferences([altered], source).Single();
            var captain = unit with { Profile = new(Keywords: ["Character"]), Aliases = ["bsdata:11:test-catalogue:captain"] };
            Check(Enhancements.Eligible(army, unit, either) && Enhancements.Eligible(army, captain, either), "OR bearer choices were flattened into AND");
            Check(!Enhancements.Eligible(army, captain with { Aliases = [] }, either), "Named bearer matched without source identity");
            modifier["conditionGroups"]![0]!["conditions"]![0]!["childId"] = "unresolved";
            var unknown = BsDataImporter.ImportEnhancementReferences([altered], source).Single();
            Check(unknown.Eligibility == null && !Enhancements.Eligible(army, unit, unknown), "Unresolved source restriction became permissive");
        });
        test("Legacy enhancement repair changes only matching-source eligibility", () =>
        {
            var legacy = data with { ReferenceCards = [card with { Eligibility = null }] };
            var repaired = EnhancementLibraryRepair.Apply(legacy, cards);
            Check(repaired.ReferenceCards![0].Eligibility != null && ReferenceEquals(repaired.Units, legacy.Units), "Repair did not preserve units");
            Check(repaired.ReferenceCards[0] with { Eligibility = null } == legacy.ReferenceCards[0], "Repair changed card content");
            Check(ReferenceEquals(repaired, EnhancementLibraryRepair.Apply(repaired, cards)), "Repair was not idempotent");
            Check(ReferenceEquals(legacy, EnhancementLibraryRepair.Apply(legacy, [card with { Source = source with { ContentHash = "different" } }])), "Different revision repaired");
            Check(EnhancementLibraryRepair.Apply(legacy, [card with { Text = "Different descriptive text" }]).ReferenceCards![0].Eligibility != null, "Descriptive text blocked recovery");
        });
        test("Existing libraries automatically recover enhancement eligibility from their exact cached archive", () =>
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("enhancements.json").Open()))
                writer.Write(new JsonObject { ["catalogue"] = document.Root.DeepClone() }.ToJsonString());
            var bytes = archive.ToArray(); var cachedSource = source with { ContentHash = Convert.ToHexString(SHA256.HashData(bytes)) };
            var path = Path.Combine(root, "source-enhancement-repair"); var cache = Path.Combine(path, "sources", "bsdata-11-test"); Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "repository.zip"), bytes); File.WriteAllText(Path.Combine(cache, "source.json"), DataJson.Write(cachedSource));
            using var store = WindowsServices.OpenStore(path);
            store.Import(data with { Source = cachedSource, ReferenceCards = [card with { Source = cachedSource, Eligibility = null }] }); store.Save(army);
            var before = DataJson.Write(store.LoadArmies()); var session = new ArmySession(store);
            Check(Enhancements.Options(army, session.Dataset, unit).Count == 1, "Cached source did not repair existing library");
            Check(store.LoadDataset()!.ReferenceCards![0].Eligibility != null, "Repair did not persist");
            Check(DataJson.Write(store.LoadArmies()) == before, "Repair changed saved armies");
        });
        var index = Array.IndexOf(args, "--enhancement-catalogue");
        if (index < 0 || index + 2 >= args.Length) return;
        test("Real all-faction enhancement recovery includes Daemon Prince Revolting Regeneration", () =>
        {
            var library = DataJson.Read<Dataset>(File.ReadAllText(args[index + 2]));
            var recovered = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(File.ReadAllBytes(args[index + 1])), library.Source);
            var repaired = EnhancementLibraryRepair.Apply(library, recovered);
            var bundled = EnhancementLibraryRepair.ApplyBundled(library);
            File.WriteAllText(Path.Combine(root, "enhancement-recovery-source.json"), DataJson.Write(recovered));
            var differences = bundled.ReferenceCards!.Zip(repaired.ReferenceCards!).Where(p => DataJson.Write(p.First) != DataJson.Write(p.Second)).ToList();
            File.WriteAllText(Path.Combine(root, "enhancement-recovery-differences.json"), DataJson.Write(differences));
            Check(DataJson.Write(bundled) == DataJson.Write(repaired), "Bundled offline repair differs from the exact source archive: "
                + string.Join(", ", differences.Select(p => p.First.Faction + "/" + p.First.Detachment + "/" + p.First.Name)));
            var originalCard = library.ReferenceCards!.First(c => c.Name == "Revolting Regeneration");
            var altered = library with { ReferenceCards = [originalCard with { Text = "Changed rule" }] };
            Check(EnhancementLibraryRepair.ApplyBundled(altered).ReferenceCards![0].Eligibility != null, "Descriptive text blocked bundled recovery");
            altered = library with { ReferenceCards = [originalCard with { Source = originalCard.Source! with { ContentHash = "different" } }] };
            Check(ReferenceEquals(altered, EnhancementLibraryRepair.ApplyBundled(altered)), "Bundled repair matched a different revision");
            var prince = library.Units.First(u => u.Faction == "Death Guard" && u.Name == "Daemon Prince of Nurgle");
            var roster = ArmyList.New(prince.Faction) with { Detachment = "Virulent Vectorium", Entries = [new("prince", prince.Key, prince.Name, 1)] };
            var regeneration = Enhancements.Options(roster, repaired, prince).Single(c => c.Name == "Revolting Regeneration");
            var selected = Enhancements.Select(roster, repaired, "prince", new(regeneration.Detachment, regeneration.Name));
            Check(ArmyCalculator.Calculate(selected, repaired).Total == ArmyCalculator.Calculate(roster, repaired).Total + 30, "Regeneration points incorrect");
            using (var store = WindowsServices.OpenStore(Path.Combine(root, "real-enhancement-offline-repair")))
            {
                store.Import(library); store.Save(roster);
                var before = store.LoadDataset()!; var armiesBefore = DataJson.Write(store.LoadArmies());
                var session = new ArmySession(store); session.Restore();
                var after = store.LoadDataset()!;
                Check(DataJson.Write(before.Units) == DataJson.Write(after.Units), "Offline repair changed real unit data");
                Check(DataJson.Write(before.Detachments) == DataJson.Write(after.Detachments), "Offline repair changed detachments");
                Check(DataJson.Write(before.ReferenceCards!.Where(c => c.Section != "Enhancements"))
                    == DataJson.Write(after.ReferenceCards!.Where(c => c.Section != "Enhancements")), "Offline repair changed other rules");
                Check(armiesBefore == DataJson.Write(store.LoadArmies()), "Offline repair changed saved armies");
                session.Open(roster); session.SelectEnhancement("prince", new(regeneration.Detachment, regeneration.Name));
                Check(session.Result.Total == ArmyCalculator.Calculate(selected, repaired).Total, "Repaired shared session could not equip regeneration");
            }
            var factions = new HashSet<string>();
            foreach (var c in repaired.ReferenceCards!.Where(c => c.Section == "Enhancements" && c.Eligibility != null))
            {
                var host = ArmyList.New(c.Faction) with { Detachment = c.Detachment };
                if (repaired.Units.Any(u => u.Faction == c.Faction && Enhancements.Eligible(host, u, c, repaired))) factions.Add(c.Faction);
            }
            Check(factions.Count >= 20, $"Only {factions.Count} factions have equippable enhancements");
            File.WriteAllText(Path.Combine(root, "enhancement-recovery-report.json"), DataJson.Write(new { Cards = recovered.Count, Recovered = recovered.Count(c => c.Eligibility != null), Factions = factions.Order().ToList() }));
            File.WriteAllText(Path.Combine(root, "enhancement-repair-pack.json"), DataJson.Write(new Dataset(library.Source, [], ReferenceCards: recovered.Where(c => c.Eligibility != null).ToList(), ReferenceOnly: true)));
        });
    }
}
