using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using GrimList.Core;

internal static class AllArmyChecks
{
    private static readonly SourceInfo Source = new("All-army fixture", "1", "", "", Edition: "11");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid source accepted"); }
    private static JsonObject Unit(string id, string name, int points) => new()
    {
        ["id"] = id, ["name"] = name, ["type"] = "model",
        ["costs"] = new JsonArray(new JsonObject { ["name"] = "pts", ["typeId"] = "points", ["value"] = points }),
        ["profiles"] = JsonNode.Parse("""[{"id":"stat","name":"Fixture model","typeName":"Unit","characteristics":[{"name":"M","$text":"6\""},{"name":"T","$text":"4"}]}]"""),
        ["categoryLinks"] = JsonNode.Parse("""[{"name":"Character"}]""")
    };
    private static BsDataDocument Catalogue(string id, string name, params JsonObject[] units) => new(name + ".json", new()
    {
        ["id"] = id, ["name"] = name, ["gameSystemId"] = "game", ["library"] = false,
        ["selectionEntries"] = new JsonArray(units.Select(u => (JsonNode)u).ToArray())
    });
    internal static List<BsDataDocument> Documents()
    {
        var marines = Catalogue("sm", "Imperium - Space Marines", Unit("captain", "Captain", 80), Unit("psyker", "Librarian", 70));
        var templars = Catalogue("bt", "Imperium - Adeptus Astartes - Black Templars", Unit("marshal", "Marshal", 75));
        templars.Root["catalogueLinks"] = JsonNode.Parse("""[{"name":"Imperium - Space Marines","targetId":"sm","importRootEntries":true},{"name":"Orks","targetId":"orks","importRootEntries":true}]""");
        templars.Root["entryLinks"] = JsonNode.Parse("""[{"targetId":"captain","name":"Chapter Captain","modifiers":[{"field":"points","type":"set","value":90}]},{"targetId":"psyker","hidden":true}]""");
        var orks = Catalogue("orks", "Xenos - Orks", Unit("boss", "Warboss", 65));
        orks.Root["catalogueLinks"] = JsonNode.Parse("""[{"name":"Orks Library","targetId":"ork-library"}]""");
        orks.Root["entryLinks"] = JsonNode.Parse("""[{"name":"Detachment","targetId":"detachments","type":"selectionEntryGroup"}]""");
        var library = BsDataDocument.Parse("Orks Library.json", """
            {"catalogue":{"id":"ork-library","name":"Orks Library","gameSystemId":"game","library":true,
              "categoryEntries":[{"id":"hold","name":"Take and Hold"},{"id":"purge","name":"Purge the Foe"}],
              "sharedSelectionEntryGroups":[{"id":"detachments","name":"Detachment","selectionEntries":[
                {"id":"ork-detachment","name":"Fixture Mob","type":"upgrade","costs":[{"name":"Detachment Points","value":3}],
                 "categoryLinks":[{"targetId":"hold"},{"targetId":"purge"}],"rules":[{"name":"Fixture rule","description":"Fixture rule text."}]}]}]}}
            """);
        var daemons = Catalogue("daemons", "Chaos - Chaos Daemons");
        daemons.Root["catalogueLinks"] = JsonNode.Parse("""[{"name":"Chaos - Daemons Library","targetId":"daemon-library","importRootEntries":true}]""");
        var daemonLibrary = Catalogue("daemon-library", "Chaos - Daemons Library", Unit("daemon", "Fixture daemon", 90));
        daemonLibrary.Root["library"] = true;
        var deathGuard = Catalogue("dg", "Chaos - Death Guard", Unit("dg-unit", "Fixture plague lord", 100));
        var tau = Catalogue("tau", "Xenos - T'au Empire", Unit("commander", "Fixture commander", 85));
        return [templars, marines, orks, library, daemons, daemonLibrary, deathGuard, tau,
            new("game.json", new() { ["id"] = "game", ["name"] = "Warhammer 40,000" })];
    }
    internal static byte[] Zip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var document in Documents())
            {
                using var writer = new StreamWriter(zip.CreateEntry("repository/" + document.FileName).Open());
                writer.Write(new JsonObject { [document.Id == "game" ? "gameSystem" : "catalogue"] = document.Root.DeepClone() }.ToJsonString());
            }
        return output.ToArray();
    }
    public static void Run(Action<string, Action> test, string root)
    {
        test("All-army import discovers playable catalogues, canonical names and shared native units", () =>
        {
            var result = BsDataImporter.ImportAll(Documents(), Source).Dataset;
            Check(result.Units.Select(u => u.Faction).Distinct().Count() == 6, "Missing armies or libraries exposed as armies");
            Check(result.Units.Any(u => u.Faction == "T’au Empire") && result.Units.Any(u => u.Faction == "Orks"), "Faction prefixes/apostrophes not canonical");
            var chapter = result.Units.Where(u => u.Faction == "Black Templars").ToList();
            Check(chapter.Count == 2 && chapter.Single(u => u.Name == "Chapter Captain").Options.Single().Points == 90, "Chapter override or inherited units incorrect");
            Check(!chapter.Any(u => u.Name is "Librarian" or "Warboss"), "Hidden chapter unit or unrelated ally leaked");
            Check(result.Units.Single(u => u.Faction == "Space Marines" && u.Name == "Captain").Options.Single().Points == 80, "Chapter mutated shared data");
            Check(result.Units.Single(u => u.Faction == "Chaos Daemons").Name == "Fixture daemon", "Native daemon library omitted");
            Check(result.Units.All(u => u.Profile?.Models?.Count > 0 || u.Profile?.Characteristics?.Count > 0), "Shared import lost profiles");
            var legacy = BsDataImporter.Import(Documents(), Source).Dataset.Units.Single();
            Check(legacy.Aliases!.SequenceEqual(result.Units.Single(u => u.Faction == "Death Guard").Aliases!), "Existing Death Guard identity changed");
        });
        test("Chapter units replace same-name inherited units even when their BSData IDs differ", () =>
        {
            var documents = Documents();
            var templars = documents.Single(d => d.Id == "bt");
            var marines = documents.Single(d => d.Id == "sm");
            var replacement = Unit("chapter-tank", "Fixture Tank", 120);
            replacement["profiles"]![0]!["characteristics"]![1]!["$text"] = "10";
            ((JsonArray)templars.Root["selectionEntries"]!).Add(replacement);
            ((JsonArray)marines.Root["selectionEntries"]!).Add(Unit("shared-tank", "Fixture Tank", 100));
            var result = BsDataImporter.ImportAll(documents, Source);
            var tank = result.Dataset.Units.Single(u => u.Faction == "Black Templars" && u.Name == "Fixture Tank");
            Check(tank.Options.Single().Points == 120, "Inherited price replaced the chapter price");
            Check(tank.Profile!.Characteristics?.GetValueOrDefault("T") == "10"
                || tank.Profile.Models!.Any(m => m.Characteristics.GetValueOrDefault("T") == "10"), "Chapter datasheet replaced");
            Check(tank.Aliases!.Contains("bsdata:11:bt:chapter-tank") && tank.Aliases.Contains("bsdata:11:bt:shared-tank"), "Inherited identity lost");
            Check(result.Dataset.Units.Single(u => u.Faction == "Space Marines" && u.Name == "Fixture Tank").Options.Single().Points == 100, "Parent army changed");
            Check(result.Warnings.Any(w => w.Contains("Fixture Tank") && w.Contains("inherited")), "Replacement not explained in review");
        });
        test("Hidden chapter replacements suppress inherited names with different IDs", () =>
        {
            var documents = Documents();
            var hidden = Unit("chapter-tank", "Fixture Tank", 120); hidden["hidden"] = true;
            ((JsonArray)documents.Single(d => d.Id == "bt").Root["selectionEntries"]!).Add(hidden);
            ((JsonArray)documents.Single(d => d.Id == "sm").Root["selectionEntries"]!).Add(Unit("shared-tank", "Fixture Tank", 100));
            var result = BsDataImporter.ImportAll(documents, Source).Dataset;
            Check(!result.Units.Any(u => u.Faction == "Black Templars" && u.Name == "Fixture Tank"), "Hidden chapter replacement leaked from parent");
            Check(result.Units.Any(u => u.Faction == "Space Marines" && u.Name == "Fixture Tank"), "Parent unit disappeared");
        });
        test("Same-catalogue Space Wolves variants retain separate prices and stable identities", () =>
        {
            var documents = Documents();
            var wolves = Catalogue("sw", "Imperium - Adeptus Astartes - Space Wolves",
                Unit("d4ef-1ddc-1ed5-5156", "Wolf Guard Headtakers", 100),
                Unit("0f93-6850-8f14-a351", "Wolf Guard Headtakers", 150));
            documents.Add(wolves);
            var imported = BsDataImporter.ImportAll(documents, Source);
            var variants = imported.Dataset.Units.Where(u => u.Faction == "Space Wolves").ToList();
            Check(variants.Count == 2 && variants.Select(u => u.Name).Distinct().Count() == 2, "Variants lost or still ambiguous");
            Check(variants.Select(u => u.Options.Single().Points).Order().SequenceEqual(new[] { 100, 150 }), "Variant prices combined");
            Check(variants.All(u => u.Aliases!.Count == 1 && u.Profile != null), "Variant identities or profiles merged");
            Check(imported.Warnings.Any(w => w.Contains("Wolf Guard Headtakers") && w.Contains("variants")), "Variant warning missing");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "space-wolves-variants"));
            store.Import(imported.Dataset);
            var first = store.LoadDataset()!.Units.Single(u => u.Aliases!.Contains("bsdata:11:sw:d4ef-1ddc-1ed5-5156"));
            var army = ArmyList.New("Space Wolves") with { Entries = [new("wolf-entry", first.Key, first.Name, 1)] };
            store.Save(army); var saved = DataJson.Write(store.LoadArmies());
            wolves.Root["selectionEntries"] = new JsonArray(BsDataDocument.Children(wolves.Root, "selectionEntries").AsEnumerable().Reverse().Select(n => n.DeepClone()).ToArray());
            var reordered = BsDataImporter.ImportAll(documents, Source).Dataset;
            Check(variants.All(v => reordered.Units.Any(u => u.Name == v.Name && u.Aliases!.SequenceEqual(v.Aliases!))), "Order changed variant identity");
            store.ApplyImport(store.PreviewImport(reordered, ImportLayer.Combined).Dataset);
            Check(DataJson.Write(store.LoadArmies()) == saved && ArmyCalculator.Calculate(army, store.LoadDataset()).Total == 100, "Saved roster changed variant");
        });
        test("Hidden same-name entries do not conflict with visible entries in either order", () =>
        {
            foreach (var hiddenFirst in new[] { true, false })
            {
                var hidden = Unit("hidden-wolves", "Wolf Guard Headtakers", 150); hidden["hidden"] = true;
                var visible = Unit("visible-wolves", "Wolf Guard Headtakers", 100);
                var wolves = Catalogue("sw", "Imperium - Adeptus Astartes - Space Wolves", hiddenFirst ? [hidden, visible] : [visible, hidden]);
                var result = BsDataImporter.ImportAll([wolves], Source).Dataset.Units.Single();
                Check(result.Name == "Wolf Guard Headtakers" && result.Options.Single().Points == 100, "Hidden entry changed the visible unit");
                Check(result.Aliases!.SequenceEqual(new[] { "bsdata:11:sw:visible-wolves" }), "Hidden identity assigned to visible entry");
            }
        });
        test("Unit display modifiers are applied before checking duplicate names and visibility", () =>
        {
            var renamed = Unit("wolves-plus", "Wolf Guard Headtakers", 150);
            renamed["modifiers"] = JsonNode.Parse("""[{"field":"name","type":"append","value":" with hunting wolves"}]""");
            var hidden = Unit("hidden-wolves", "Wolf Guard Headtakers", 90);
            hidden["modifiers"] = JsonNode.Parse("""[{"field":"hidden","type":"set","value":true}]""");
            var wolves = Catalogue("sw", "Imperium - Adeptus Astartes - Space Wolves", Unit("wolves", "Wolf Guard Headtakers", 100), renamed, hidden);
            var result = BsDataImporter.ImportAll([wolves], Source).Dataset.Units;
            Check(result.Count == 2 && result.Any(u => u.Name == "Wolf Guard Headtakers with hunting wolves" && u.Options.Single().Points == 150), "Source display overrides lost");
        });
        test("Shared detachment definitions retain faction ownership and every disposition", () =>
        {
            var result = BsDataImporter.ImportAll(Documents(), Source).Dataset;
            var detachment = result.Detachments!.Single();
            Check(detachment.Faction == "Orks" && detachment.Cost == "3 DP" && detachment.DispositionOptions.Count == 2, "Shared detachment metadata lost");
            Check(result.ReferenceCards!.All(c => c.Faction == "Orks") && result.ReferenceCards!.Any(c => c.Text == "Fixture rule text."), "Rules assigned to wrong army");
        });
        test("All-army updates preserve stored roster identity and unrelated reference packs", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "all-armies"));
            var old = BsDataImporter.Import(Documents(), Source).Dataset with { ReferenceCards = [new("Death Guard", "Stratagems", "Retained", "Existing supplemental rule.")] };
            store.Import(old);
            var unit = store.LoadDataset()!.Units.Single();
            var army = ArmyList.New("Death Guard") with { Entries = [new("entry", unit.Key, unit.Name, 1)] };
            store.Save(army); var saved = DataJson.Write(store.LoadArmies());
            store.ApplyImport(store.PreviewImport(BsDataImporter.ImportAll(Documents(), Source).Dataset, ImportLayer.Combined).Dataset);
            Check(DataJson.Write(store.LoadArmies()) == saved && store.LoadDataset()!.FindUnit(unit.Key) != null, "Saved roster changed or lost its unit");
            Check(store.LoadDataset()!.ReferenceCards!.Any(c => c.Name == "Retained"), "Supplemental references erased");
        });
        test("Incomplete or cancelled all-army imports fail before producing a partial dataset", () =>
        {
            Reject(() => BsDataImporter.ImportAll(Documents().Where(d => d.Id != "sm").ToList(), Source));
            Reject(() => BsDataImporter.ImportAll(Documents().Where(d => d.Id != "ork-library").ToList(), Source));
            Reject(() => BsDataImporter.ImportAll([Documents().Single(d => d.Id == "ork-library")], Source));
            Reject(() => BsDataImporter.ImportAll(Documents(), Source with { Edition = "10" }));
            try { BsDataImporter.ImportAll(Documents(), Source, cancellation: new CancellationToken(true)); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        });
        test("BSData refresh retains complete supplemental enhancement eligibility with a review warning", () =>
        {
            var original = BsDataImporter.Import(Documents(), Source).Dataset;
            var card = new ReferenceCard("Death Guard", "Enhancements", "Fixture gift", "Death Guard model only. Existing effect.", "Fixture detachment", "15 pts", Source);
            original = original with { ReferenceCards = [card] };
            var imported = original with { Source = Source with { Name = "BSData · All armies" }, ReferenceCards = [card with { Text = "New effect without a bearer restriction.", Cost = "20 pts" }] };
            var merged = DatasetMerge.Prepare(original, imported, ImportLayer.Combined);
            Check(merged.Dataset.ReferenceCards!.Single() == card && merged.Warnings.Any(w => w.Contains("bearer restriction")), "Incomplete BSData card erased eligibility or lost provenance");
            var complete = imported with { ReferenceCards = [card with { Text = "Terminator model only. Updated effect.", Cost = "20 pts" }] };
            Check(DatasetMerge.Prepare(original, complete, ImportLayer.Combined).Dataset.ReferenceCards!.Single().Cost == "20 pts", "Complete enhancement update was blocked");
        });
        test("BSData download imports every army in one pinned repository archive", () =>
        {
            var zip = Zip(); const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
                ? new StringContent("{\"sha\":\"" + sha + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-02T12:00:00Z\"}}}")
                : request.RequestUri.AbsolutePath.EndsWith(sha) ? new ByteArrayContent(zip) : throw new Exception("Download not pinned")));
            var result = new BsDataClient(client).Download("11", Path.Combine(root, "all-armies-download")).GetAwaiter().GetResult();
            Check(result.Dataset.Units.Select(u => u.Faction).Distinct().Count() == 6, "Downloader still imports only Death Guard");
            Check(DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(result.CacheDirectory, "dataset.json"))).Units.Count == result.Dataset.Units.Count, "Offline cache incomplete");
        });
        File.WriteAllBytes(Path.Combine(root, "all-armies-fixture.zip"), Zip());
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpContent> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = respond(request) });
    }
}
