using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GrimList.Core;

static class BsDataChecks
{
    // Reduced schema fixtures based on BSData/wh40k-11e Death Guard catalogue revision 14.
    // Values exercise the adapter; this is not a complete/current playable dataset.
    private const string Catalogue = """
    {"catalogue":{"name":"Chaos - Death Guard","id":"dg","revision":14,"gameSystemId":"game",
      "catalogueLinks":[{"targetId":"shared"}],
      "entryLinks":[
        {"name":"Typhus","targetId":"typhus","type":"selectionEntry"},
        {"name":"Poxwalkers","targetId":"pox","type":"selectionEntry"},
        {"name":"Plague Marines","targetId":"marines","type":"selectionEntry"},
        {"name":"Chaos Rhino","targetId":"rhino","type":"selectionEntry"}],
      "sharedSelectionEntries":[
        {"name":"Typhus","id":"typhus","type":"model","costs":[{"name":"pts","typeId":"points","value":100}],
         "entryLinks":[{"targetId":"warlord","name":"Warlord","type":"selectionEntry"}]},
        {"name":"Poxwalkers","id":"pox","type":"unit","costs":[{"name":"pts","typeId":"points","value":65}],
         "modifiers":[{"field":"points","type":"set","value":130,"conditions":[{"childId":"model","field":"selections","includeChildSelections":true,"scope":"pox","type":"greaterThan","value":10}]}],
         "selectionEntryGroups":[{"id":"pox-group","constraints":[{"field":"selections","scope":"parent","type":"min","value":10}],
           "selectionEntries":[{"id":"pox-model","type":"model","constraints":[{"field":"selections","scope":"parent","type":"min","value":10},{"field":"selections","scope":"parent","type":"max","value":20}]}]}]},
        {"name":"Plague Marines","id":"marines","type":"unit","costs":[{"name":"pts","typeId":"points","value":90}],
         "modifiers":[
           {"field":"points","type":"set","value":125,"conditions":[{"childId":"model","field":"selections","includeChildSelections":true,"scope":"marines","type":"atLeast","value":6}]},
           {"field":"points","type":"set","value":180,"conditions":[{"childId":"model","field":"selections","includeChildSelections":true,"scope":"marines","type":"atLeast","value":8}]}],
         "selectionEntries":[{"id":"champion","type":"model","constraints":[{"field":"selections","scope":"parent","type":"min","value":1},{"field":"selections","scope":"parent","type":"max","value":1}]}],
         "selectionEntryGroups":[{"id":"marine-group","constraints":[{"field":"selections","scope":"parent","type":"min","value":4},{"field":"selections","scope":"parent","type":"max","value":9}],
           "selectionEntries":[{"id":"marine-model","type":"model","constraints":[{"field":"selections","scope":"parent","type":"max","value":9}]},{"id":"alternate-model","type":"model","constraints":[{"field":"selections","scope":"parent","type":"max","value":9}]}]}]},
        {"name":"Chaos Rhino","id":"rhino","type":"model","costs":[{"name":"pts","typeId":"points","value":75}],
         "modifiers":[{"field":"points","type":"increment","value":10,"conditionGroups":[{"type":"and","localConditionGroups":[
           {"field":"selections","scope":"parent","type":"atLeast","value":3,"repeats":1,"conditions":[
             {"childId":"any","field":"selections","scope":"self","type":"before","value":1},
             {"childId":"rhino","field":"selections","scope":"self","type":"instanceOf","value":1}]}]}]}]}
      ]}}
    """;
    private const string Shared = """{"catalogue":{"name":"Shared library","id":"shared","sharedSelectionEntries":[{"name":"Warlord","id":"warlord","type":"upgrade","costs":[{"name":"pts","typeId":"points","value":0}]}]}}""";
    private static readonly SourceInfo Source = new("BSData test fixture", "revision 14 fixture", "2026-09-01", "https://github.com/BSData/wh40k-11e", Edition: "11");
    private static List<BsDataDocument> Documents(string? text = null)
    {
        List<BsDataDocument> documents = [BsDataDocument.Parse("Chaos - Death Guard.json", text ?? Catalogue), BsDataDocument.Parse("Shared.json", Shared)];
        BsDataProfileChecks.AddProfiles(documents); return documents;
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid input accepted"); }
    private static byte[] Zip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in Documents().Select(d => (d.FileName, new JsonObject { ["catalogue"] = d.Root.DeepClone() }.ToJsonString())).Append(("package.json", "{\"name\":\"metadata\"}")))
            { using var writer = new StreamWriter(zip.CreateEntry("wh40k-11e-test/" + file.Item1).Open()); writer.Write(file.Item2); }
        return output.ToArray();
    }
    public static void Run(Action<string, Action> test, string root)
    {
        BsDataProfileChecks.Run(test, root, () => Documents());
        test("BSData resolves shared entries, squad price bands and fourth-copy surcharge", () =>
        {
            var data = BsDataImporter.Import(Documents(), Source).Dataset;
            Equal(4, data.Units.Count); Equal(100, data.Units[0].Options[0].Points);
            Equal("10:65,20:130", string.Join(',', data.Units[1].Options.Select(o => $"{o.Models}:{o.Points}")));
            Equal("5:90,7:125,10:180", string.Join(',', data.Units[2].Options.Select(o => $"{o.Models}:{o.Points}")));
            Equal(new PointOption(1, 85, 4), data.Units[3].Options[1]);
            var rhino = data.Units[3]; var army = ArmyList.New("Death Guard") with { Entries = Enumerable.Range(0, 4).Select(i => new ArmyEntry(i.ToString(), rhino.Key, rhino.Name, 1)).ToList() };
            Equal(310, ArmyCalculator.Calculate(army, data).Total);
        });
        test("Unknown BSData pricing clears stale prices while retaining profiles and roster identity", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "bsdata-unknown"));
            var data = BsDataImporter.Import(Documents(), Source).Dataset;
            var typhus = data.Units[0] with { Profile = new(Notes: "Keep this profile"), ProfileSource = Source };
            store.Import(data with { Units = [typhus] }); var id = store.LoadDataset()!.Units[0].Id;
            var changed = JsonNode.Parse(Catalogue)!;
            changed["catalogue"]!["sharedSelectionEntries"]![0]!["modifiers"] = JsonNode.Parse("""[{"field":"points","type":"set","value":1,"conditions":[{"field":"selections","scope":"roster","childId":"detachment","type":"atLeast","value":1}]}]""");
            var incoming = BsDataImporter.Import(Documents(changed.ToJsonString()), Source);
            Equal(0, incoming.Dataset.Units[0].Options.Count);
            store.ApplyImport(store.PreviewImport(incoming.Dataset, ImportLayer.Points).Dataset);
            var updated = store.LoadDataset()!.FindUnit(id)!;
            Equal("Keep this profile", updated.Profile!.Notes); Equal(true, updated.PointsWarning.Length > 0); Equal(0, updated.Options.Count);
            var army = ArmyList.New("Death Guard") with { Entries = [new("one", id, "Typhus", 1)] };
            var result = ArmyCalculator.Calculate(army, store.LoadDataset()); Equal(false, result.Complete); Equal(true, result.Issues.Any(i => i.Contains("Unsupported composition price scope")));
        });
        test("BSData per-model points support absent and zero unit costs and every squad size", () =>
        {
            foreach (var baseCost in new int?[] { null, 0, 10 })
            {
                var docs = Documents();
                var unit = docs[0].Root["sharedSelectionEntries"]![1]!.AsObject();
                unit.Remove("modifiers"); unit.Remove("selectionEntryGroups"); unit.Remove("costs");
                if (baseCost != null) unit["costs"] = JsonNode.Parse($$"""[{"name":"pts","typeId":"points","value":{{baseCost}}}]""");
                unit["selectionEntries"] = JsonNode.Parse("""[{"id":"priced-model","type":"model","costs":[{"name":"pts","typeId":"points","value":95}],"constraints":[{"field":"selections","scope":"parent","type":"min","value":1},{"field":"selections","scope":"parent","type":"max","value":2}]}]""");
                var data = BsDataImporter.Import(docs, Source).Dataset;
                var imported = data.Units[1];
                Equal($"1:{95 + (baseCost ?? 0)},2:{190 + (baseCost ?? 0)}", string.Join(',', imported.Options.Select(o => $"{o.Models}:{o.Points}")));
                var army = ArmyList.New("Death Guard") with { Entries = [new("one", imported.Key, imported.Name, 2)] };
                Equal(190 + (baseCost ?? 0), ArmyCalculator.Calculate(army, data).Total);
                Equal(true, ArmyCalculator.Calculate(army, data).Complete);
                var model = unit["selectionEntries"]![0]!.AsObject();
                model["selectionEntries"] = JsonNode.Parse("""[{"id":"paid-weapon","type":"upgrade","costs":[{"name":"pts","typeId":"points","value":15}]}]""");
                var paid = BsDataImporter.Import(docs, Source).Dataset.Units[1];
                Equal(2, paid.Options.Count);
                var selected = new ArmyEntry("paid", paid.Key, paid.Name, 2, Loadout: Loadouts.Create(paid, 2));
                selected = selected with { Loadout = Loadouts.Choose(paid, selected, 0, "fixed:paid-weapon", "paid-weapon", 1) };
                Equal(15, WargearPricing.Cost(paid, selected));
                model.Remove("selectionEntries");
                model["modifiers"] = JsonNode.Parse("""[{"field":"points","type":"increment","value":5}]""");
                Equal(0, BsDataImporter.Import(docs, Source).Dataset.Units[1].Options.Count);
                model.Remove("modifiers");
                var otherModel = model.DeepClone(); otherModel["id"] = "other-model";
                unit["selectionEntries"]!.AsArray().Add(otherModel);
                var mixed = BsDataImporter.Import(docs, Source).Dataset;
                if (baseCost == null) Equal(0, mixed.Units[1].Options.Count);
                else
                {
                    Equal(2, mixed.Units[1].ModelCosts!.Count);
                    Equal("2,3,4", string.Join(',', mixed.Units[1].Options.Select(o => o.Models)));
                    Equal(baseCost.Value + 190, ArmyCalculator.Calculate(army, mixed).Total);
                    Equal(true, ArmyCalculator.Calculate(army, mixed).Complete);
                }
            }
        });
        test("BSData missing references and paid equipment are explicit unknown prices", () =>
        {
            var partial = BsDataImporter.Import([Documents()[0]], Source);
            Equal(0, partial.Dataset.Units[0].Options.Count); Equal(true, partial.Warnings.Any(w => w.Contains("repository ZIP")));
            var docs = Documents(); docs[1].Root["sharedSelectionEntries"]![0]!["costs"]![0]!["value"] = 20;
            Equal(0, BsDataImporter.Import(docs, Source).Dataset.Units[0].Options.Count);
        });
        test("BattleScribe XML normalizes to the same points model and prohibits external entities", () =>
        {
            const string xml = """<catalogue xmlns="http://www.battlescribe.net/schema/catalogueSchema" name="Chaos - Death Guard" id="dg"><selectionEntries><selectionEntry id="test" name="Fixture model" type="model"><costs><cost name="pts" typeId="points" value="50.0"/></costs></selectionEntry></selectionEntries></catalogue>""";
            var parsed = BsDataImporter.Import([BsDataDocument.Parse("test.cat", xml)], Source with { Edition = "10" });
            Equal(new PointOption(1, 50), parsed.Dataset.Units[0].Options[0]);
            var failed = false; try { BsDataDocument.Parse("bad.cat", "<!DOCTYPE catalogue [<!ENTITY x SYSTEM 'file:///private'>]><catalogue id='a'>&x;</catalogue>"); } catch (System.Xml.XmlException) { failed = true; } Equal(true, failed);
        });
        test("BSData archive imports actual catalogue files and requires Death Guard", () =>
        {
            var documents = BsDataDocument.ReadArchive(Zip()); Equal(2, documents.Count); Equal(4, BsDataImporter.Import(documents, Source).Dataset.Units.Count);
            Reject(() => BsDataImporter.Import([documents[1]], Source));
            Reject(() => BsDataImporter.Import(documents, Source with { Edition = "" }));
            File.WriteAllBytes(Path.Combine(root, "bsdata-schema-fixture.zip"), Zip());
        });
        test("BSData download pins shared files to a commit and caches points with datasheets", () =>
        {
            var sha = new string('a', 40); var calls = new List<string>();
            using var client = new HttpClient(new FixtureHandler(request =>
            {
                calls.Add(request.RequestUri!.AbsoluteUri);
                return new(HttpStatusCode.OK) { Content = request.RequestUri.Host == "api.github.com"
                    ? new StringContent(DataJson.Write(new { sha, commit = new { committer = new { date = "2026-09-01T12:00:00Z" } } }))
                    : new ByteArrayContent(Zip()) };
            }));
            var download = new BsDataClient(client).Download("11", Path.Combine(root, "bsdata-download")).GetAwaiter().GetResult();
            Equal(2, calls.Count); Equal(true, calls[1].EndsWith(sha)); Equal(sha, download.Dataset.Source.Version);
            Equal("2026-09-01", download.Dataset.Source.Date); Equal(64, download.Dataset.Source.ContentHash.Length);
            var cached = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(download.CacheDirectory, "dataset.json")));
            Equal(true, cached.Units[0].Profile!.Weapons!.Count > 0); Equal(4, Directory.GetFiles(download.CacheDirectory).Length);
            var offline = BsDataClient.ReadCached(BsDataClient.CachedArchive(download.Dataset, Path.Combine(root, "bsdata-download"))!).GetAwaiter().GetResult();
            Equal(true, offline.Dataset.Units[0].Options.SequenceEqual(download.Dataset.Units[0].Options));
        });
        test("BSData network failures and cancellation preserve the current library", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "bsdata-failure")); store.Import(BsDataImporter.Import(Documents(), Source).Dataset);
            var before = DataJson.Write(store.LoadDataset());
            foreach (var cancelled in new[] { false, true })
            {
                using var client = new HttpClient(new FixtureHandler(_ => new(HttpStatusCode.Forbidden)));
                var failed = false;
                try { new BsDataClient(client).Download("11", Path.Combine(root, "failed-bsdata"), cancellation: new CancellationToken(cancelled)).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { failed = true; }
                Equal(true, failed); Equal(before, DataJson.Write(store.LoadDataset()));
            }
        });
    }
}
