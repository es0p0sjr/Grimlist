using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class DetachmentLifecycleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static readonly SourceInfo OldSource = new("BSData fixture", new string('a', 40), "2026-10-01", "https://github.com/BSData/wh40k-11e", Edition: "11", ContentHash: new string('a', 64));
    private static readonly SourceInfo NewSource = OldSource with { Version = new string('b', 40), Date = "2026-10-26", ContentHash = new string('b', 64) };
    private static Dataset Library(SourceInfo source, params (string Id, string Name)[] names) => new(source,
        [new("Adeptus Custodes", "Captain", [new(1,100)], Id: "captain-type", Profile: new(new() { ["M"] = "6\"", ["OC"] = "2" }, Keywords: ["Character"],
            Semantics: new([], [new("old-buff", "Old rule", "model", "OC", "increment", "4", Condition: new("detachment", "Shield Host"))], 3)), ProfileSource: source)],
        Detachments: names.Select(n => new DetachmentDefinition("Adeptus Custodes", n.Name, Cost: "1 DP", Source: source, Id: n.Id, FactionId: "custodes")).ToList(),
        ReferenceCards: names.Select(n => new ReferenceCard("Adeptus Custodes", "Enhancements", "Test gift", "Character model only. Add 1 to the Wounds characteristic of the bearer.", n.Name, "10 pts", source)).ToList(),
        FactionContexts: [new("Adeptus Custodes", "custodes", ["Imperium"])]);
    private static Dataset Download(Dataset data) => data with { SchemaVersion = 4, IsBsDataDownload = true,
        DetachmentSources = [new("Adeptus Custodes", "custodes", data.Source, data.Detachments!.Select(d => new DetachmentIdentity(d.Id, d.Name)).ToList(), [])] };
    public static void Run(Action<string, Action> test, string root)
    {
        var old = Library(OldSource, ("old", "Shield Host"), ("kept", "Retained Host"));
        var incoming = Download(Library(NewSource, ("new", "New Host"), ("kept", "Retained Host")));
        var army = ArmyList.New("Adeptus Custodes") with { Detachment = "Shield Host", Entries = [new("captain", old.Units[0].Key, "Captain", 1, Enhancement: new("Shield Host", "Test gift"))] };
        test("Authoritative detachment replacement retires absent definitions, cards and effective benefits", () =>
        {
            foreach (var layer in new[] { ImportLayer.Combined, ImportLayer.Profiles })
            {
                var result = DatasetMerge.Prepare(old, incoming, layer).Dataset;
                Check(DetachmentLifecycle.IsObsolete(result, army.Faction, "Shield Host"), "Missing obsolete flag");
                Check(result.Detachments!.All(d => d.Name != "Shield Host") && result.ReferenceCards!.All(c => c.Detachment != "Shield Host"), "Stale content retained");
                Check(DetachmentRules.Options(army, result).All(d => d.Name != "Shield Host"), "Stale picker option");
                Check(DetachmentRules.Issues(army, result).Any(i => i.Contains("obsolete")), "No saved selection warning");
                Check(Enhancements.Options(army, result, result.Units[0]).Count == 0, "Retired enhancement available");
                Check(EffectiveDatasheets.Project(army, army.Entries[0], result)!.EffectiveCharacteristics["OC"] == "2", "Retired conditional effect active");
                Check(RosterExport.Text(army, result).Contains("obsolete"), "Export omitted obsolete warning");
                Check(DataJson.Read<Dataset>(DataJson.Write(result)).DetachmentSources!.Single().Retired.Count == 1, "Portable metadata lost");
            }
        });
        test("Partial, local, and points-only imports cannot infer retirement or reverse installed authority", () =>
        {
            var partial = incoming with { IsBsDataDownload = false, DetachmentSources = null, SchemaVersion = 2 };
            Check(DatasetMerge.Prepare(old, partial, ImportLayer.Combined, true).Dataset.Detachments!.Any(d => d.Name == "Shield Host"), "Partial retired");
            Check(DatasetMerge.Prepare(old, incoming, ImportLayer.Points).Dataset.Detachments!.Any(d => d.Name == "Shield Host"), "Points retired");
            var current = DatasetMerge.Prepare(old, incoming, ImportLayer.Combined).Dataset;
            var serializedOld = DataJson.Read<Dataset>(DataJson.Write(Download(old)));
            var replay = DatasetMerge.Prepare(current, serializedOld, ImportLayer.Combined).Dataset;
            Check(DetachmentLifecycle.IsObsolete(replay, army.Faction, "Shield Host") && replay.Detachments!.All(d => d.Name != "Shield Host"), "Old import resurrected");
            var points = DatasetMerge.Prepare(current, old, ImportLayer.Points).Dataset;
            Check(points.DetachmentSources!.Single().Source == current.DetachmentSources!.Single().Source, "Points lost authority");
        });
        test("Stable detachment identities preserve renames and authoritative reintroduction clears retirement", () =>
        {
            var renamed = Download(Library(NewSource, ("old", "Renamed Host"), ("kept", "Retained Host")));
            renamed = renamed with { ReferenceCards = renamed.ReferenceCards!.Select(c => c with { Cost = "15 pts" }).ToList() };
            var result = DatasetMerge.Prepare(old, renamed, ImportLayer.Combined).Dataset;
            Check(!DetachmentLifecycle.IsObsolete(result, army.Faction, "Shield Host") && DetachmentRules.Find(army, "Shield Host", result)?.Name == "Renamed Host", "Rename broke selection");
            Check(DetachmentRules.Contains(army, "Renamed Host", result) && Enhancements.Find(army, result, army.Entries[0].Enhancement) != null, "Rename broke enhancement");
            Check(result.ReferenceCards!.Count(c => c.Detachment == "Renamed Host" && c.Name == "Test gift") == 1
                && Enhancements.Find(army, result, army.Entries[0].Enhancement)!.Cost == "15 pts", "Renamed card duplicated or shadowed newer rules");
            var manualCard = old.ReferenceCards![0] with { Cost = "22 pts" };
            var pack = new RulesPack("rename-override", 1, NewSource with { Name = "Manual override" }, [army.Faction], [], [], [manualCard]);
            var overridden = result with { RulesPacks = [pack], ReferenceCards = [.. result.ReferenceCards!, manualCard] };
            overridden = DetachmentLifecycle.Apply(overridden);
            Check(Enhancements.Find(army, overridden, army.Entries[0].Enhancement)!.Cost == "22 pts", "Rename lost explicit override");
            Check(ReferenceEquals(overridden, DetachmentLifecycle.Apply(overridden)), "Renamed override repair churn");
            var reference = ReferenceModel.Canonical(new(army.Faction, "Stratagems", "Rename reference", "Test", "Shield Host", Source: OldSource), OldSource);
            reference = reference with { Details = reference.Details! with { Detachments = [new("Shield Host", ReferenceMatching.CanonicalDetachmentId("11", army.Faction, "Shield Host"))] } };
            Check(ReferenceMatching.Match(reference, result).Details!.Match == "ExactCanonicalMatch", "Rename lost reference canonical ID");
            Check(EffectiveDatasheets.Project(army, army.Entries[0], result)!.EffectiveCharacteristics["OC"] == "6", "Rename broke source condition");
            var priced = result with { Units = [result.Units[0] with { DetachmentCosts = [new("Renamed Host", "Mandatory", 25, "mandatory-id")] }] };
            Check(ArmyCalculator.Calculate(army, priced).Entries.Single().Points == 140, "Rename lost detachment-specific unit price or enhancement");
            var portable = DatasetMerge.Prepare(null, DataJson.Read<Dataset>(DataJson.Write(result)), ImportLayer.Combined).Dataset;
            Check(DetachmentLifecycle.ResolveName(portable, army.Faction, "Shield Host") == "Renamed Host", "Fresh portable import lost rename aliases");
            var retired = DatasetMerge.Prepare(old, incoming, ImportLayer.Combined).Dataset;
            var importedRetirement = DatasetMerge.Prepare(null, DataJson.Read<Dataset>(DataJson.Write(retired)), ImportLayer.Combined).Dataset;
            Check(importedRetirement.DetachmentSources![0].Retired.Single().RetiredBy == NewSource, "Portable import lost retirement provenance");
            var restored = DatasetMerge.Prepare(retired, Download(old with { Source = NewSource }), ImportLayer.Combined).Dataset;
            Check(!DetachmentLifecycle.IsObsolete(restored, army.Faction, "Shield Host") && restored.Detachments!.Any(d => d.Name == "Shield Host"), "Reintroduction blocked");
            var repeat = DatasetMerge.Prepare(restored, Download(old with { Source = NewSource }), ImportLayer.Combined).Dataset;
            Check(DataJson.Write(repeat.DetachmentSources) == DataJson.Write(restored.DetachmentSources), "Repeated update changed lifecycle");
        });
        test("Complete zero-detachment scope retires all; custom definitions and explicit overrides survive", () =>
        {
            var custom = new DetachmentDefinition(army.Faction, "Custom Host", Source: new("Custom", "1", "", "", Edition: "11"));
            var current = old with { Detachments = [.. old.Detachments!, custom] };
            var result = DatasetMerge.Prepare(current, Download(Library(NewSource)), ImportLayer.Combined).Dataset;
            Check(result.Detachments!.Count == 1 && result.Detachments[0] == custom && result.DetachmentSources![0].Retired.Count == 2, "Zero/custom scope");
            var pack = new RulesPack("custom-pack", 1, new("Custom codex", "1", "2026-10-26", "", Edition: "11"), [army.Faction], [], [old.Detachments![0]], []);
            var overridden = result with { RulesPacks = [pack], Detachments = [.. result.Detachments, old.Detachments[0]] };
            Check(DetachmentLifecycle.Apply(overridden).Detachments!.Any(d => d.Name == "Shield Host") && !DetachmentLifecycle.IsObsolete(overridden, army.Faction, "Shield Host"), "Explicit override retired");
        });
        test("Muster nominations follow proven renames and retain obsolete choices without granting keywords", () =>
        {
            var rule = new MusterNominationRule("nomination", "Chosen", 1, 1, ["Character"], ["Chosen"]);
            var renamed = Download(Library(NewSource, ("old", "Renamed Host"), ("kept", "Retained Host")));
            renamed = renamed with { Detachments = renamed.Detachments!.Select(d => d.Id == "old" ? d with { Nominations = [rule] } : d).ToList() };
            var result = DatasetMerge.Prepare(old, renamed, ImportLayer.Combined).Dataset;
            var nominated = army with { MusterSelections = [new("Shield Host", rule.Id, "captain")] };
            Check(MusterEffects.GrantedKeywords(nominated, nominated.Entries[0], result).Contains("Chosen"), "Rename lost nomination grant");
            Check(MusterEffects.Issues(nominated, result).Count == 0, "Rename invalidated nomination");
            Check(MusterEffects.Normalize(nominated, result).MusterSelections!.Single().Detachment == "Renamed Host", "Normalize erased nomination");
            var retired = DatasetMerge.Prepare(old, incoming, ImportLayer.Combined).Dataset;
            Check(MusterEffects.Normalize(nominated, retired).MusterSelections!.Count == 1, "Obsolete nomination erased");
            Check(MusterEffects.GrantedKeywords(nominated, nominated.Entries[0], retired).Count == 0, "Obsolete nomination granted keywords");
            Check(MusterEffects.Issues(nominated, retired).Any(i => i.Contains("inactive")), "Missing nomination warning");
            var stale = retired with { Detachments = [.. retired.Detachments!, old.Detachments![0]] };
            Check(DetachmentCatalogue.Apply(stale).Detachments!.All(d => d.Name != "Shield Host"), "Catalogue replay retained obsolete definition");
            Check(ReferenceLibraryRecovery.Supplement(stale, stale).Detachments!.All(d => d.Name != "Shield Host"), "Recovery early return retained obsolete definition");
        });
        test("Retirement persists atomically through preview/apply, reopen and bundled recovery", () =>
        {
            var path = Path.Combine(root, "detachment-lifecycle");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.ApplyImport(old); store.Save(army);
                var savedArmy = DataJson.Write(store.LoadArmies().Single());
                var flow = new LibraryWorkflow(store); var preview = flow.Prepare(incoming, ImportLayer.Combined);
                Check(preview.Changes.Any(c => c.Contains("marked obsolete")), "Preview lacks retirement");
                Check(store.LoadDataset()!.Detachments!.Any(d => d.Name == "Shield Host"), "Preview mutated live data");
                flow.Apply(preview);
                Check(DataJson.Write(store.LoadArmies().Single()) == savedArmy, "Import rewrote army");
            }
            using var reopened = WindowsServices.OpenStore(path);
            var session = new ArmySession(reopened); session.Open(army);
            Check(DetachmentLifecycle.IsObsolete(session.Dataset, army.Faction, "Shield Host"), "Startup lost flag");
            Check(session.Dataset!.Detachments!.All(d => d.Name != "Shield Host"), "Startup restored detachment");
            var supplement = ReferenceLibraryRecovery.Supplement(session.Dataset with { FactionContexts = null }, ReferenceLibraryRecovery.ReadBundled());
            Check(supplement.Detachments!.All(d => d.Name != "Shield Host"), "Bundled supplement restored detachment");
            Check(session.Army.Detachment == army.Detachment && session.Army.Entries[0].Id == "captain", "Saved selection erased");
            var scoped = new ArmySession(reopened, useFactionLoading: true); scoped.Open(army);
            Check(DetachmentLifecycle.IsObsolete(scoped.Dataset, army.Faction, "Shield Host"), "Faction cache lost authority");
        });
        test("Shared references retain surviving detachment scopes after retirement", () =>
        {
            var current = old with { ReferenceCards = [new("Adeptus Custodes", "Stratagems", "Shared rule", "Test", "", "1 CP", OldSource,
                Details: new("shared", "11", "bsdata:11", "shared", "BSData", "Detachment", "custodes",
                    [new("Shield Host", "old"), new("Retained Host", "kept")]))] };
            var result = DatasetMerge.Prepare(current, incoming, ImportLayer.Combined).Dataset;
            var shared = result.ReferenceCards!.Single(c => c.Name == "Shared rule");
            Check(shared.Details!.Detachments!.Count == 1 && shared.Detachment == "Retained Host", "Shared card lost or retained obsolete applicability");
            var conditional = new ReferenceCard(army.Faction, "Army Rules", "Conditional army rule", "Test", Source: NewSource, Applicability: new("constant", "1"));
            var withCondition = result with { ReferenceCards = [.. result.ReferenceCards!, conditional] };
            Check(ArmyReferences.ForArmy(army, withCondition).Any(c => c.Name == conditional.Name), "Authority hid conditional faction references");
        });
        test("Authority requires resolved source structure; conditional visibility differs from permanent withdrawal", () =>
        {
            var catalogue = BsDataDocument.Parse("Test.json", """
                {"catalogue":{"id":"test","name":"Test","gameSystemId":"game",
                  "selectionEntryGroups":[{"id":"detachments","name":"Detachments","selectionEntries":[
                    {"id":"visible","name":"Visible","type":"upgrade"},
                    {"id":"hidden","name":"Withdrawn","type":"upgrade","hidden":true},
                    {"id":"conditional","name":"Conditional","type":"upgrade","hidden":true,"modifiers":[
                      {"field":"hidden","type":"set","value":false,"conditions":[{"scope":"primary-catalogue","childId":"test","type":"equalTo","value":1}]}]}
                  ]}]}}
                """);
            var game = BsDataDocument.Parse("Game.json", """{"gameSystem":{"id":"game","name":"Game"}}""");
            var valid = BsDataImporter.ImportDetachmentMetadata([catalogue, game], NewSource, true);
            Check(valid.DetachmentSources!.Single().Active.Select(d => d.Name).Order().SequenceEqual(new[] { "Conditional", "Visible" }), "Hidden/conditional interpretation");
            Check(BsDataImporter.ImportDetachmentMetadata([catalogue], NewSource, true).DetachmentSources == null, "Missing game dependency accepted");
            var unsupported = catalogue with { Root = (System.Text.Json.Nodes.JsonObject)catalogue.Root.DeepClone() };
            unsupported.Root["selectionEntryGroups"]![0]!["selectionEntries"]![0]!["type"] = "future-format";
            Check(BsDataImporter.ImportDetachmentMetadata([unsupported, game], NewSource, true).DetachmentSources == null, "Unsupported entry shape authorized deletion");
            var empty = catalogue with { Root = (System.Text.Json.Nodes.JsonObject)catalogue.Root.DeepClone() };
            empty.Root["selectionEntryGroups"]![0]!["selectionEntries"] = new System.Text.Json.Nodes.JsonArray();
            Check(BsDataImporter.ImportDetachmentMetadata([empty, game], NewSource, true).DetachmentSources!.Single().Active.Count == 0, "Explicit zero scope not recognized");
            Check(BsDataImporter.ImportDetachmentMetadata([catalogue, game], NewSource).DetachmentSources == null, "Ordinary local parse acquired authority");
        });
        test("Pinned BSData detachment parser establishes Custodes authority only on the online path", () =>
        {
            var dir = Path.Combine("tests", "fixtures", "bsdata", "11e", "cc1830fbfead059f8059ed6c7f8e73cd4c390908");
            var source = DataJson.Read<SourceInfo>(File.ReadAllText(Path.Combine(dir, "source.json")));
            var docs = BsDataDocument.ReadArchive(File.ReadAllBytes(Path.Combine(dir, "repository.zip")));
            var imported = BsDataImporter.ImportDetachmentMetadata(docs, source, authoritativeDownload: true);
            var scope = imported.DetachmentSources?.SingleOrDefault(s => s.Faction == "Adeptus Custodes");
            Check(scope != null && scope.Active.Count > 0 && imported.IsBsDataDownload, "Complete Custodes scope not recognized");
            Check(!DataJson.Read<Dataset>(DataJson.Write(imported)).IsBsDataDownload, "Serialized import impersonated live download");
        });
    }
}
