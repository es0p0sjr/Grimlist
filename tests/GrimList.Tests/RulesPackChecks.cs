using System.Text;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform;
using GrimList.Platform.Windows;

internal static class RulesPackChecks
{
    private static readonly SourceInfo Bs = new("BSData fixture", "old", "2026-10-07", "https://example.test/catalogue", Edition: "11");
    private static readonly SourceInfo Book = new("Codex fixture", "new", "2026-10-03", "https://example.test/codex", Edition: "11");
    private static readonly SourceInfo Mfm = new("Munitorum Field Manual", "1.5", "2026-10-09", "https://example.test/mfm", Edition: "11");
    private static Dictionary<string, string> Stats(string t = "9", string w = "10") => new() { ["M"] = "8\"", ["T"] = t, ["W"] = w, ["Sv"] = "2+" };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid pack accepted"); }
    private static Dataset Catalogue() => new(Bs, [new("Test Marines", "Commander", [new(1, 300)], Id: "commander", Category: "Characters",
        Profile: new(Stats(), Weapons: [new("Sword", "Melee", "Melee", "5", "2+", "8", "-3", "3")],
            Abilities: [new("Old ability", "Old rules")], Models: [new("Commander", Stats())], Keywords: ["Character"],
            Semantics: new([], [new("old-effect", "Old ability", "model", "T", "increment", "1")], 3)), PointsSource: Mfm, ProfileSource: Bs)],
        Detachments: [new("Test Marines", "Old Force", Cost: "1DP"), new("Test Marines", "Retained Force", Cost: "1DP")],
        ReferenceCards: [new("Test Marines", "Army Rules", "Old army rule", "Old army text", Source: Bs),
            new("Test Marines", "Stratagems", "Old tactic", "Old tactic text", "Old Force", "1CP", Bs),
            new("Test Marines", "Stratagems", "Retained tactic", "Retained text", "Retained Force", "1CP", Bs),
            new("Test Marines", "Enhancements", "Retained enhancement", "CHARACTER model only. Retained effect.", "Retained Force", "10", Bs),
            new("Test Marines", "Enhancements", "Retained upgrade", "CHARACTER model only. Retained upgrade effect.", "Retained Force", "15", Bs, IsUpgrade: true),
            new("Other faction", "Army Rules", "Other army rule", "Other text", Source: Bs)]);
    private static RulesPack Pack() => new("test-codex", 1, Book, ["Test Marines"],
        [new("Test Marines", "Commander", new(Stats("10", "16"), Models: [new("Commander", Stats("10", "16"))]), ["Characteristics", "Models"])],
        [], [], Notes: "Test fixture, not game rules.");
    private static Dataset Input(RulesPack pack) => new(pack.Source, [], SchemaVersion: 3, RulesPacks: [pack]);
    private static RulesPack Codex() => Pack() with {
        Units = [Pack().Units[0] with { Profile = Pack().Units[0].Profile with {
            Abilities = [new("New ability", "Replacement text")], Semantics = new([], [], 3) },
            Fields = ["Characteristics", "Models", "Abilities", "Semantics"] }],
        Detachments = [new("Test Marines", "New Force", Cost: "2DP", Dispositions: ["Take and Hold"], Tags: ["Unique: Doctrines"], CannotCombineWithTags: ["Unique: Doctrines"])],
        RemoveDetachments = [new("Test Marines", "Old Force")],
        ReplaceCardScopes = [new("Test Marines", "Army Rules")],
        Cards = [new("Test Marines", "Army Rules", "New army rule", "Replacement army rule"),
            new("Test Marines", "Stratagems", "New tactic", "New tactic text", "New Force", "1CP"),
            new("Test Marines", "Enhancements", "New enhancement", "CHARACTER model only. Add 1 to the Toughness characteristic of the bearer.",
                "New Force", "20", Eligibility: new("keyword", "Character"), Modifiers: [new("new-effect", "New enhancement", "model", "T", "increment", "1")])]
    };
    public static void Run(Action<string, Action> test, string root)
    {
        test("Published partial Lion pack updates the cached real datasheet without claiming a complete codex", () => {
            var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var input = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(repository, "data/rules-packs/dark-angels-2026-partial.json")));
            input.Validate(); Check(input.RulesPacks!.Single().Coverage == "Partial", "Incomplete source marked complete");
            var path = Path.Combine(repository, ".local/sources/bsdata-11-6166445ea55740a9b4d0d2aedc1234c727a6d513/dataset.json");
            var fixture = new UnitDefinition("Dark Angels", "Lion El'Jonson", [new(1, 310)], Profile: new(Stats(), Models: [new("Lion El'Jonson", Stats())]));
            var data = File.Exists(path) ? DataJson.Read<Dataset>(File.ReadAllText(path)) : new Dataset(Bs, [fixture]);
            var lion = data.Units.Single(u => u.Faction == "Dark Angels" && UnitIdentity.Normalize(u.Name) == "LIONELJONSON");
            var current = data with { Units = [lion], ReferenceCards = [], ReferenceCoverage = [], Detachments = [] };
            var updated = DatasetMerge.Prepare(current, input, ImportLayer.Profiles).Dataset.Units.Single();
            Check(updated.Profile!.Characteristics!["T"] == "10" && updated.Profile.Models![0].Characteristics["W"] == "16", "Real profile remained stale");
            Check(DataJson.Write(updated.Profile.Abilities) == DataJson.Write(lion.Profile!.Abilities)
                && DataJson.Write(updated.Profile.Loadout) == DataJson.Write(lion.Profile.Loadout)
                && DataJson.Write(updated.Options) == DataJson.Write(lion.Options), "Partial correction altered uncovered rules/prices");
        });
        test("Rules packs update both statlines and preserve weapons, prices and identities", () => {
            var current = Catalogue(); var before = DataJson.Write(current);
            var updated = DatasetMerge.Prepare(current, Input(Pack()), ImportLayer.Profiles).Dataset;
            var unit = updated.Units[0];
            Check(unit.Profile!.Characteristics!["T"] == "10" && unit.Profile.Models![0].Characteristics["W"] == "16", "Stats not updated");
            Check(unit.Id == "commander" && unit.Options[0].Points == 300 && unit.PointsSource == Mfm, "Points or identity altered");
            Check(DataJson.Write(unit.Profile.Weapons) == DataJson.Write(current.Units[0].Profile!.Weapons), "Uncovered weapons altered");
            Check(DataJson.Write(current) == before && updated.SchemaVersion == 3, "Input mutated or unsafe schema");
        });
        test("Codex replaces army rules, removes old detachments, keeps unrelated stratagems and upgrades", () => {
            var updated = DatasetMerge.Prepare(Catalogue(), Input(Codex()), ImportLayer.Profiles).Dataset;
            var names = updated.ReferenceCards!.Select(c => c.Name).ToHashSet();
            Check(!names.Contains("Old army rule") && !names.Contains("Old tactic") && names.Contains("New army rule"), "Obsolete rules remain");
            Check(names.IsSupersetOf(["Retained tactic", "Retained enhancement", "Retained upgrade", "Other army rule"]), "Unrelated rules lost");
            Check(updated.ReferenceCards!.Single(c => c.Name == "Retained upgrade").IsUpgrade, "Upgrade classification lost");
            Check(updated.Detachments!.All(d => d.Name != "Old Force") && updated.Detachments!.Any(d => d.Name == "New Force"), "Detachment replacement failed");
            var army = ArmyList.New("Test Marines") with { Detachment = "New Force", Entries = [new("entry", "commander", "Commander", 1)] };
            Check(DatasetSnapshot.For(updated).References.ForArmy(army, "11").Any(c => c.Name == "New tactic"), "New stratagem hidden");
            var enhancement = updated.ReferenceCards!.Single(c => c.Name == "New enhancement");
            Check(Enhancements.Eligible(army, updated.Units[0], enhancement, updated), "Typed enhancement eligibility lost");
            var entry = army.Entries[0] with { Enhancement = new("New Force", "New enhancement") };
            army = army with { Entries = [entry] };
            Check(EffectiveDatasheets.Project(army, entry, updated)!.EffectiveCharacteristics["T"] == "11", "Stale ability effect or enhancement missing");
        });
        test("Rules packs survive catalogue refresh, points updates, export and database restart", () => {
            var path = Path.Combine(root, "rules-persistence");
            using (var store = WindowsServices.OpenStore(path)) {
                store.Import(Catalogue()); var flow = new LibraryWorkflow(store);
                flow.Apply(flow.Prepare(Input(Codex()), ImportLayer.Profiles));
                foreach (var layer in new[] { ImportLayer.Profiles, ImportLayer.Combined, ImportLayer.Points })
                    flow.Apply(flow.Prepare(Catalogue(), layer));
                Check(store.LoadDataset()!.Units[0].Profile!.Characteristics!["T"] == "10", "BSData undid codex");
                Check(store.LoadDataset()!.ReferenceCards!.All(c => c.Name != "Old tactic"), "BSData resurrected removed rule");
                var prices = new Dataset(Mfm with { Version = "1.6", Date = "2026-10-10" }, [new("Test Marines", "Commander", [new(1, 310)])]);
                flow.Apply(flow.Prepare(prices, ImportLayer.Points));
            }
            using var reopened = WindowsServices.OpenStore(path);
            var restored = DataJson.Read<Dataset>(DataJson.Write(ReferenceModel.ExportDataset(reopened.LoadDataset()!)));
            restored.Validate();
            Check(restored.RulesPacks!.Single().Id == "test-codex" && restored.Units[0].Options[0].Points == 310, "Persistence or MFM broken");
            Check(restored.Units[0].Profile!.Models![0].Characteristics["W"] == "16", "Model data reverted");
        });
        test("Rules preview reports removals and saved army impact without changing the live army", () => {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "rules-preview")); store.Import(Catalogue());
            var army = ArmyList.New("Test Marines") with { Detachment = "Old Force", Entries = [new("entry", "commander", "Commander", 1, Notes: "Keep notes")] };
            store.Save(army); var before = DataJson.Write(store.LoadArmies()); var library = DataJson.Write(store.LoadDataset());
            var flow = new LibraryWorkflow(store); var preview = flow.Prepare(Input(Codex()), ImportLayer.Profiles);
            Check(preview.Changes.Any(c => c.Contains("Old army rule removed")) && preview.Changes.Any(c => c.Contains("Old Force: detachment removed")), "Removal preview missing");
            Check(preview.Armies.Count == 1 && preview.CanApply, "Saved army not reviewed");
            Check(DataJson.Write(store.LoadArmies()) == before && DataJson.Write(store.LoadDataset()) == library, "Preview changed data");
            flow.Apply(preview);
            Check(DataJson.Write(store.LoadArmies()) == before, "Import rewrote saved choices");
        });
        test("Rules packs reject missing targets, wrong editions, older revisions and silent revision changes", () => {
            var updated = DatasetMerge.Prepare(Catalogue(), Input(Pack()), ImportLayer.Profiles).Dataset;
            Reject(() => DatasetMerge.Prepare(updated, Input(Pack() with { Notes = "Changed without revision" }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(updated, Input(Pack() with { Source = Book with { Edition = "10" } }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack() with { Units = [Pack().Units[0] with { Name = "Absent" }] }), ImportLayer.Profiles));
            var newer = Pack() with { Revision = 2, Notes = "Revision two" };
            updated = DatasetMerge.Prepare(updated, Input(newer), ImportLayer.Profiles).Dataset;
            Reject(() => DatasetMerge.Prepare(updated, Input(Pack()), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(updated, Input(newer with { Revision = 3, Units = [] }), ImportLayer.Profiles));
        });
        test("Rules packs require model consistency and explicit replacement of stale ability semantics", () => {
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack() with { Units = [Pack().Units[0] with { Fields = ["Characteristics"] }] }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack() with { Units = [Pack().Units[0] with { Fields = ["Abilities"] }] }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack() with { Units = [Pack().Units[0] with { Fields = ["Points"] }] }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack() with { Factions = ["Other"] }), ImportLayer.Profiles));
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(Pack()), ImportLayer.Points));
        });
        test("Android bundled recovery cannot resurrect codex removals or downgrade schema", () => {
            var current = DatasetMerge.Prepare(Catalogue(), Input(Codex()), ImportLayer.Profiles).Dataset;
            var old = Catalogue() with { Units = [], ReferenceOnly = true, ReferenceCards = Catalogue().ReferenceCards!.Where(c => c.Section == "Stratagems").ToList() };
            var recovered = ReferenceLibraryRecovery.Supplement(current, old);
            recovered.Validate();
            Check(recovered.Detachments!.All(d => d.Name != "Old Force") && recovered.ReferenceCards!.All(c => c.Name != "Old tactic"), "Bundled rules resurrected");
            Check(recovered.SchemaVersion == 3, "Schema downgraded");
        });
        test("New rules-pack units require full profiles and retain missing-point diagnostics", () => {
            var pack = Pack() with { Status = "Provisional", Units = [new("Test Marines", "New squad", new(Stats("5", "3")), ["*"], AddIfMissing: true)] };
            var preview = DatasetMerge.Prepare(Catalogue(), Input(pack), ImportLayer.Profiles);
            var unit = preview.Dataset.Units.Single(u => u.Name == "New squad");
            Check(unit.Options.Count == 0 && unit.PointsWarning.Length > 0, "New unit received invented points");
            Check(preview.Warnings.Any(w => w.Contains("Provisional")), "Provisional provenance hidden");
            Reject(() => DatasetMerge.Prepare(Catalogue(), Input(pack with { Units = [pack.Units[0] with { Fields = ["Characteristics"] }] }), ImportLayer.Profiles));
        });
        test("Both platform import paths read rules JSON and block display-only reference imports", () => {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "rules-read")); store.Import(Catalogue());
            var flow = new LibraryWorkflow(store); var json = DataJson.Write(Input(Pack()));
            var desktop = flow.Parse(json, Book, ImportLayer.Profiles);
            using var document = new PickedDocument("rules.json", new MemoryStream(Encoding.UTF8.GetBytes(json)));
            var mobile = flow.ReadDocumentAsync(document, "11").GetAwaiter().GetResult();
            Check(DataJson.Write(desktop.Dataset) == DataJson.Write(mobile.Dataset), "Platform reads differ");
            Reject(() => new StructuredJsonReferenceAdapter().Read("rules.json", json, "11"));
        });
        test("Rules packs retain explicit removals across revisions and reject overlapping ownership", () => {
            var current = DatasetMerge.Prepare(Catalogue(), Input(Codex()), ImportLayer.Profiles).Dataset;
            var next = Codex() with { Revision = 2, Cards = Codex().Cards.Where(c => c.Name != "New tactic").ToList(),
                RemoveCards = [new("Test Marines", "Stratagems", "New tactic", "New Force")] };
            current = DatasetMerge.Prepare(current, Input(next), ImportLayer.Profiles).Dataset;
            Check(current.ReferenceCards!.All(c => c.Name != "New tactic"), "Explicit removal failed");
            current = DatasetMerge.Prepare(current, Catalogue(), ImportLayer.Combined).Dataset;
            Check(current.ReferenceCards!.All(c => c.Name != "New tactic"), "Tombstone forgotten");
            Reject(() => DatasetMerge.Prepare(current, Input(Pack() with { Id = "another-pack" }), ImportLayer.Profiles));
        });
        test("Stat-only packs retain reference coverage; explicit complete rule scopes replace it", () => {
            var current = Catalogue() with { ReferenceCoverage = [new(ReferenceModel.Owner(Bs), "11", "Stratagems", "Test Marines", "Retained Force", "VerifiedComplete", "CompleteForDetachment", true, Bs)] };
            var updated = DatasetMerge.Prepare(current, Input(Pack()), ImportLayer.Profiles).Dataset;
            Check(updated.ReferenceCoverage!.Single().State == "VerifiedComplete", "Stats changed unrelated rule coverage");
            var fullScope = Pack() with { Revision = 2, ReplaceCardScopes = [new("Test Marines", "Stratagems", "Retained Force")],
                Cards = [new("Test Marines", "Stratagems", "Replacement tactic", "New text", "Retained Force", "2CP")] };
            updated = DatasetMerge.Prepare(updated, Input(fullScope), ImportLayer.Profiles).Dataset;
            Check(updated.ReferenceCoverage!.Single(c => c.SourceId == ReferenceModel.Owner(Bs)).State == "Partial", "Old source certifies new rules");
            Check(updated.ReferenceCoverage!.Single(c => c.SourceId.StartsWith("rules-pack:")).State == "VerifiedComplete", "Explicit full scope unrecognized");
            Check(updated.ReferenceCards!.All(c => c.Name != "Retained tactic"), "Replaced scope retained old cards");
        });
        test("Replacing one detachment preserves the other targets of a shared source stratagem", () => {
            var shared = ReferenceModel.Canonical(new ReferenceCard("Test Marines", "Stratagems", "Shared tactic", "Old text", Source: Bs,
                Details: new(Scope: "Detachment", Detachments: [new("Old Force"), new("Retained Force")])), Bs);
            var current = Catalogue() with { ReferenceCards = [shared] };
            var pack = Pack() with { ReplaceCardScopes = [new("Test Marines", "Stratagems", "Old Force")],
                Cards = [new("Test Marines", "Stratagems", "Shared tactic", "New text", "Old Force")] };
            var updated = DatasetMerge.Prepare(current, Input(pack), ImportLayer.Profiles).Dataset;
            foreach (var next in new[] { updated, DatasetMerge.Prepare(updated, current, ImportLayer.Combined).Dataset }) {
                var references = DatasetSnapshot.For(next).References;
                Check(references.ForArmy(ArmyList.New("Test Marines") with { Detachment = "Old Force" }, "11").Single().Text == "New text", "Old scope retained old text");
                Check(references.ForArmy(ArmyList.New("Test Marines") with { Detachment = "Retained Force" }, "11").Single().Text == "Old text", "Uncovered scope lost shared rule");
            }
        });
    }
}
