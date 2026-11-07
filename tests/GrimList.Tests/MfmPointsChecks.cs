using System.IO.Compression;
using System.Net;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class MfmPointsChecks
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly SourceInfo Mirror = new("Munitorum Field Manual · BSData mirror", Sha, "2026-09-30",
        $"https://github.com/BSData/wh40k-11e-mfm/blob/{Sha}", Edition: "11");
    private static readonly SourceInfo Bs = new("BSData · fixture", "catalogue", "2026-10-07", "https://github.com/BSData/wh40k-11e", Edition: "11");
    private const string Yaml = """
        name: Dark Angels
        slug: dark-angels
        version: "1.5"
        firstSeen: 2026-09-30
        units:
          - name: Deathwing Knights
            pricing:
              - range: "[1,2]"
                label: Your 1st To 2nd Units Cost
                costs:
                  - { models: 5, points: 255 }
              - range: "[3,)"
                label: Your 3rd + Unit Costs
                costs:
                  - { models: 5, points: 275 }
        """;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid MFM accepted"); }
    private static Dataset Catalogue() => new(Bs, [new("Dark Angels", "Deathwing Knights", [new(5, 240), new(5, 260, 3)],
        Profile: new(Notes: "Catalogue datasheet"), PointsSource: Bs, ProfileSource: Bs)]);
    private static Dataset Prices() => MfmMirrorParser.Parse(Yaml, Mirror).Dataset;
    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry("snapshot/data/meta.yaml").Open()))
                manifest.Write("version: \"1.5\"\nfactions:\n" + string.Join("\n", files.Select(f => "  - " + Path.GetFileNameWithoutExtension(f.Name))) + "\n");
            foreach (var file in files)
            { using var writer = new StreamWriter(zip.CreateEntry("snapshot/data/" + file.Name).Open()); writer.Write(file.Text); }
        }
        return output.ToArray();
    }
    public static void Run(Action<string, Action> test, string root)
    {
        test("Real MFM 1.5 Dark Angels snapshot parses Deathwing 255/275 copy tiers", () =>
        {
            var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/fixtures/mfm/dark-angels-1.5.yaml"));
            var parsed = MfmMirrorParser.Parse(File.ReadAllText(fixture), Mirror);
            var knights = parsed.Dataset.Units.Single(u => u.Name == "Deathwing Knights");
            Check(knights.Options.SequenceEqual(new[] { new PointOption(5, 255), new PointOption(5, 275, 3) }), "Wrong real tiers");
            Check(knights.PointsSource!.Date == "2026-09-30" && knights.PointsSource.Version.StartsWith("MFM 1.5"), "Source lost");
            Check(parsed.Dataset.Units.All(u => u.Profile == null) && parsed.Dataset.ReferenceCards == null, "MFM altered rules");
        });
        test("MFM points change three-unit roster to 785 without changing its entries", () =>
        {
            var data = Catalogue(); var unit = data.Units[0];
            var army = ArmyList.New("Dark Angels") with { Entries = Enumerable.Range(1, 3).Select(i => new ArmyEntry(i.ToString(), unit.Key, unit.Name, 5)).ToList() };
            var before = DataJson.Write(army); var next = MfmPoints.Overlay(data, Prices()).Dataset;
            Check(ArmyCalculator.Calculate(army, data).Total == 740, "Bad catalogue fixture");
            var result = ArmyCalculator.Calculate(army, next);
            Check(result.Total == 785 && result.Entries.Select(e => e.Points).SequenceEqual(new int?[] { 255, 255, 275 }), "Wrong roster points");
            Check(DataJson.Write(army) == before, "Roster mutated");
        });
        test("MFM precedence survives BSData imports, persistence and export; newer MFM replaces it", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "mfm-precedence")); store.Import(Catalogue());
            var workflow = new LibraryWorkflow(store); var preview = workflow.Prepare(Prices(), ImportLayer.Points, completeFactions: true);
            Check(store.LoadDataset()!.Units[0].Options[0].Points == 240, "Preview applied data");
            workflow.Apply(preview);
            var fresh = Catalogue() with { Source = Bs with { Version = "new catalogue", Date = "2026-10-09" } };
            fresh = fresh with { Units = [fresh.Units[0] with { Profile = new(Notes: "Updated rules"), PointsSource = fresh.Source, ProfileSource = fresh.Source }] };
            workflow.Apply(workflow.Prepare(fresh, ImportLayer.Combined));
            var saved = DataJson.Read<Dataset>(DataJson.Write(store.LoadDataset()));
            Check(saved.Units[0].Options[0].Points == 255 && saved.Units[0].Profile!.Notes == "Updated rules", "Catalogue overrode MFM or rules failed");
            Check(MfmPoints.IsMfm(saved.Units[0].PointsSource), "Provenance lost");
            var older = MfmMirrorParser.Parse(Yaml.Replace("2026-09-30", "2026-09-01").Replace("255", "230"), Mirror).Dataset;
            workflow.Apply(workflow.Prepare(older, ImportLayer.Points));
            Check(store.LoadDataset()!.Units[0].Options[0].Points == 255, "MFM regressed");
            var newer = MfmMirrorParser.Parse(Yaml.Replace("2026-09-30", "2026-10-09").Replace("255", "250"), Mirror).Dataset;
            workflow.Apply(workflow.Prepare(newer, ImportLayer.Points));
            Check(store.LoadDataset()!.Units[0].Options[0].Points == 250, "New MFM ignored");
            var profiles = workflow.Prepare(fresh, ImportLayer.Profiles).Dataset;
            Check(profiles.Units[0].Options[0].Points == 250, "Profiles import changed points");
            Reject(() => workflow.Prepare(fresh with { Source = Bs with { Edition = "10" }, Units = [fresh.Units[0] with { PointsSource = Bs with { Edition = "10" } }] }, ImportLayer.Combined));
        });
        test("MFM-only merge preserves wargear, allied costs, enhancements, upgrades and stratagems", () =>
        {
            var original = Catalogue();
            original = original with { Units = [original.Units[0] with { WargearCosts = [new("weapon", 10)], AlliedOptions = [new(5, 300)],
                DetachmentCosts = [new("Example", "Upgrade", 15, "upgrade")], MaxCopies = 3 },
                new("Dark Angels", "Unmatched squad", [new(1, 50)], Profile: new(Notes: "Keep"))],
                ReferenceCards = [new("Dark Angels", "Enhancements", "Enhancement", "Rule", "Example", "20", Bs),
                    new("Dark Angels", "Enhancements", "Upgrade", "Upgrade rule", "Example", "15", Bs, IsUpgrade: true),
                    new("Dark Angels", "Stratagems", "Stratagem", "Stratagem rule", "Example", "1 CP", Bs)],
                Detachments = [new("Dark Angels", "Example", Cost: "1 DP")] };
            using var store = WindowsServices.OpenStore(Path.Combine(root, "mfm-preservation")); store.Import(original);
            var current = store.LoadDataset()!;
            var workflow = new LibraryWorkflow(store);
            var preview = workflow.Prepare(Prices(), ImportLayer.Points, completeFactions: true);
            var next = preview.Dataset;
            Check(DataJson.Write(next.ReferenceCards) == DataJson.Write(current.ReferenceCards), "Reference cards changed");
            Check(DataJson.Write(next.Detachments) == DataJson.Write(current.Detachments), "Detachments changed");
            var old = current.Units.Single(u => u.Name == "Deathwing Knights"); var updated = next.Units.Single(u => u.Name == old.Name);
            Check(DataJson.Write(old with { Options = updated.Options, PointsSource = updated.PointsSource, Aliases = updated.Aliases }) == DataJson.Write(updated), "Non-unit-price fields changed");
            Check(next.Units.Single(u => u.Name == "Unmatched squad").Options[0].Points == 50, "Partial coverage cleared prices");
            workflow.Apply(preview);
            var persisted = store.LoadDataset()!;
            Check(DataJson.Write(persisted.Detachments) == DataJson.Write(current.Detachments)
                && DataJson.Write(persisted.ReferenceCards) == DataJson.Write(current.ReferenceCards), "Prepared points import persisted unrelated recovery");
        });
        test("MFM refuses ambiguous sizes, composition formulae, paid models and wrong editions", () =>
        {
            var data = Catalogue();
            foreach (var unit in new[] { data.Units[0] with { Options = [new(10, 240)] }, data.Units[0] with { PriceFormula = new(240, []) },
                data.Units[0] with { CopyCategory = "Terminator" } })
                Check(MfmPoints.Overlay(data with { Units = [unit] }, Prices()).Dataset.Units[0] == unit, "Unsafe prices applied");
            Check(!MfmPoints.Compatible(data.Units[0] with { ModelCosts = [new("role", 10)] }, Prices().Units[0]), "Paid model pricing accepted");
            Reject(() => MfmPoints.Overlay(data with { Source = Bs with { Edition = "10" } }, Prices()));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("[3,)", "[4,)"), Mirror));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("[1,2]", "[1,3]"), Mirror));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("points: 255", "points: ???"), Mirror));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("models: 5, points: 275", "models: 10, points: 275"), Mirror));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("255", "2147483648"), Mirror));
            Reject(() => MfmMirrorParser.Parse(Yaml.Replace("firstSeen: 2026-09-30", "firstSeen: yesterday"), Mirror));
        });
        test("MFM chapter fallback stays in faction and add-ons never become base unit prices", () =>
        {
            var marine = MfmMirrorParser.Parse(Yaml.Replace("Dark Angels", "Space Marines").Replace("dark-angels", "space-marines").Replace("Deathwing Knights", "Example squad"), Mirror).Dataset;
            var data = Catalogue() with { Units = [new("Imperial Fists", "Example squad", [new(5, 200)]), new("Orks", "Example squad", [new(5, 100)]),
                new("Black Templars", "Example squad", [new(5, 150)])] };
            var next = MfmPoints.Overlay(data, marine).Dataset;
            Check(next.Units[0].Options[0].Points == 255 && next.Units[1].Options[0].Points == 100, "Wrong faction prices");
            Check(next.Units[2].Options[0].Points == 150, "Partial Space Marines update overwrote a chapter with its own MFM page");
            var withExtra = Yaml + "\n  - name: Composite squad\n    pricing:\n      - range: \"[1,)\"\n        label: Your Unit Costs\n        costs:\n          - { models: 1, points: 50, addon: true }\n";
            var parsed = MfmMirrorParser.Parse(withExtra, Mirror);
            Check(parsed.Dataset.Units.Count == 1 && parsed.Warnings.Any(w => w.Contains("Composite squad")), "Add-on imported as base points");
        });
        test("MFM download pins archive revision, caches provenance and rejects partial corruption", () =>
        {
            var archive = Zip(("dark-angels.yaml", Yaml)); var paths = new List<string>();
            using var client = new HttpClient(new FixtureHandler(request =>
            {
                paths.Add(request.RequestUri!.AbsoluteUri);
                return new(HttpStatusCode.OK) { Content = request.RequestUri.Host == "api.github.com"
                    ? new StringContent("{\"sha\":\"" + Sha + "\"}") : new ByteArrayContent(archive) };
            }));
            var result = new MfmMirrorClient(client).Download(Path.Combine(root, "mfm-download")).GetAwaiter().GetResult();
            Check(paths.Last().EndsWith("/zip/" + Sha) && File.Exists(Path.Combine(result.CacheDirectory, "repository.zip")), "Not pinned/cached");
            Check(result.Dataset.Units[0].PointsSource!.Url.Contains(Sha) && result.Dataset.Source.ContentHash.Length == 64, "No provenance");
            Reject(() => MfmMirrorClient.ReadArchive(Zip(("dark-angels.yaml", Yaml), ("orks.yaml", "invalid")), Mirror));
            Reject(() => MfmMirrorClient.ReadArchive(Zip(("wrong-name.yaml", Yaml)), Mirror));
            var missing = Zip(("dark-angels.yaml", Yaml));
            using (var stream = new MemoryStream())
            {
                stream.Write(missing); stream.Position = 0;
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
                    zip.Entries.Single(e => e.Name == "dark-angels.yaml").Delete();
                Reject(() => MfmMirrorClient.ReadArchive(stream.ToArray(), Mirror));
            }
        });
        test("MFM separates native and allied contexts and flags duplicate native records", () =>
        {
            var first = Yaml[Yaml.IndexOf("  - name:", StringComparison.Ordinal)..];
            var allied = first.Replace("    pricing:", "    groupTitle: Every Model Has The Imperium Keyword\n    pricing:").Replace("255", "999");
            var parsed = MfmMirrorParser.Parse(Yaml + "\n" + allied, Mirror);
            Check(parsed.Dataset.Units.Single().Options[0].Points == 255, "Allied prices overwrote native");
            Check(parsed.Warnings.Any(w => w.Contains("multiple MFM contexts")), "Context warning missing");
            Reject(() => MfmMirrorParser.Parse(Yaml + "\n" + first, Mirror));
        });
        test("Shared update combines catalogue with MFM; failure is visible and cancellation propagates", () =>
        {
            var bsArchive = AllArmyChecks.Zip();
            var mfmYaml = Yaml.Replace("Dark Angels", "Space Marines").Replace("dark-angels", "space-marines").Replace("Deathwing Knights", "Captain").Replace("models: 5", "models: 1");
            foreach (var fail in new[] { false, true })
            {
                using var client = new HttpClient(new FixtureHandler(request =>
                {
                    var mirror = request.RequestUri!.AbsoluteUri.Contains("wh40k-11e-mfm");
                    if (mirror && fail) return new(HttpStatusCode.Forbidden);
                    return new(HttpStatusCode.OK) { Content = request.RequestUri.Host == "api.github.com"
                        ? new StringContent("{\"sha\":\"" + Sha + "\",\"commit\":{\"committer\":{\"date\":\"2026-10-07T00:00:00Z\"}}}")
                        : new ByteArrayContent(mirror ? Zip(("space-marines.yaml", mfmYaml)) : bsArchive) };
                }));
                var download = new LibraryDownloadClient(client).Download(true, "11", null, Path.Combine(root, "mfm-combined-" + fail)).GetAwaiter().GetResult();
                Check(download.Dataset.Units.Single(u => u.Faction == "Space Marines" && u.Name == "Captain").Options[0].Points == (fail ? 80 : 255), "Combined update missing overlay");
                Check(!fail || download.Warnings.Any(w => w.Contains("could not be refreshed")), "Silent failure");
                if (!fail)
                {
                    var cancelled = false;
                    try { new LibraryDownloadClient(client).Download(true, "11", null, root, cancellation: new CancellationToken(true)).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled, "Cancellation swallowed");
                }
            }
        });
    }
}
