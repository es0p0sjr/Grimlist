using System.Text.Json.Nodes;
using GrimList.Application;
using GrimList.Core;

internal static class ArmyRuleCoverageChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static readonly SourceInfo source = new("BSData fixture", "1", "2026-10-16", "", Edition: "11", ContentHash: new string('A', 64));
    private static List<BsDataDocument> Documents() => new[]
    {
        """
        {"catalogue":{"id":"faction","name":"Fixture","gameSystemId":"game",
          "catalogueLinks":[{"targetId":"library"},{"targetId":"shared"},{"targetId":"foreign"}],
          "sharedRules":[{"id":"inline","name":"Inline rule","description":"Inline text"}],
          "infoLinks":[{"targetId":"linked","type":"rule"}],
          "selectionEntryGroups":[{"id":"detachments","name":"Detachments","selectionEntries":[
            {"id":"host","name":"Host","type":"upgrade","infoLinks":[{"targetId":"detachment-rule","type":"rule"}]}]}]}}
        """,
        """
        {"catalogue":{"id":"library","name":"Fixture - Library","library":true,"gameSystemId":"game",
          "sharedRules":[{"id":"shared-rule","name":"Shared rule","description":"Shared text"},
            {"id":"detachment-rule","name":"Host rule","description":"Detachment only"},
            {"id":"hidden","name":"Hidden rule","description":"Hidden","hidden":true},
            {"id":"uncertain","name":"Uncertain","description":"Unknown scope","modifiers":[
              {"type":"set","field":"hidden","value":true,"conditions":[{"scope":"roster","field":"selections","childId":"unknown","type":"atLeast","value":1}]}]}]}}
        """,
        """
        {"catalogue":{"id":"shared","name":"Several factions","library":true,"gameSystemId":"game",
          "sharedRules":[{"id":"linked","name":"Linked rule","description":"Root-linked text"},
            {"id":"explicit","name":"Explicit faction rule","description":"Explicit text","modifiers":[
              {"type":"set","field":"hidden","value":true,"conditions":[{"scope":"primary-catalogue","childId":"faction","type":"notInstanceOf"}]}]},
            {"id":"other","name":"Other faction rule","description":"Other text","modifiers":[
              {"type":"set","field":"hidden","value":true,"conditions":[{"scope":"primary-catalogue","childId":"other-faction","type":"notInstanceOf"}]}]}]}}
        """,
        """
        {"catalogue":{"id":"foreign","name":"Foreign - Library","library":true,"gameSystemId":"game",
          "sharedRules":[{"id":"ally","name":"Allied rule","description":"Allied text"}],
          "sharedSelectionEntries":[{"id":"unit","name":"Ally","type":"unit","rules":[{"id":"ability","name":"Unit ability","description":"Unit-only text"}]}]}}
        """
    }.Select((text, i) => BsDataDocument.Parse(i + ".json", text)).ToList();
    private static Dataset Data(ArmyRuleScope scope) => new(source, [new(scope.Faction, "Unit", [new(1, 50)])],
        ReferenceCards: scope.Cards, FactionContexts: [new(scope.Faction, scope.CatalogueId, [])]);

    public static void Run(Action<string, Action> test, string root)
    {
        test("Army-rule discovery follows rule provenance independently of native roster roots", () =>
        {
            var docs = Documents();
            Check(BsDataImporter.NativeCatalogues(docs, docs[0]).Count() == 1, "Fixture imports library units");
            var scope = BsDataImporter.ImportArmyRules(docs, source).Single();
            Check(scope.Cards.Select(c => c.Details!.SourceCardId).Order().SequenceEqual(new[] { "explicit", "inline", "linked", "shared-rule" }), "Wrong source rules discovered");
            Check(new[] { "ally", "other", "hidden", "detachment-rule" }.All(scope.Excluded.Contains), "Missing exclusion evidence");
            Check(scope.Unresolved.Contains("uncertain"), "Unknown visibility guessed");
            Check(scope.Cards.All(c => c.Source == source && c.Details!.FactionId == "faction"), "Source/faction identity lost");
            var data = Data(scope);
            Check(ArmyReferences.ForArmy(ArmyList.New("Fixture"), data).Count(c => c.Section == "Army Rules") == 4, "Source-to-display lost rules");
            docs[2].Root["sharedRules"]!.AsArray().Add(JsonNode.Parse("""
                {"id":"effect-only","name":"Foreign rule with a faction-specific effect","description":"Foreign text","modifiers":[
                  {"field":"attacks","type":"set","value":"1","conditions":[{"scope":"primary-catalogue","childId":"faction","type":"instanceOf"}]}]}
                """));
            Check(!BsDataImporter.ImportArmyRules(docs, source).Single().Cards.Any(c => c.Details!.SourceCardId == "effect-only"), "An effect condition was mistaken for faction applicability");
            var duplicate = docs[1].Root["sharedRules"]![0]!.DeepClone(); duplicate["description"] = "Conflicting definition";
            docs[0].Root["sharedRules"]!.AsArray().Add(duplicate);
            var conflicted = BsDataImporter.ImportArmyRules(docs, source).Single();
            Check(conflicted.Unresolved.Contains("shared-rule") && conflicted.Cards.All(c => c.Details!.SourceCardId != "shared-rule"), "Conflicting source definitions chose the first record");
        });
        test("Faction reference matching works without detachments and rejects conflicting catalogue identities", () =>
        {
            var scope = BsDataImporter.ImportArmyRules(Documents(), source).Single(); var data = Data(scope);
            Check(ReferenceMatching.Match(scope.Cards[0], data).Details!.Match == "ExactId", "FactionContexts not used");
            var ambiguous = data with { FactionContexts = [new("Fixture", "faction", []), new("Other", "faction", [])] };
            Check(ReferenceMatching.Match(scope.Cards[0], ambiguous).Details!.Match == "Ambiguous", "Ambiguous catalogue guessed");
            Check(!ArmyReferences.ForArmy(ArmyList.New("Fixture"), ambiguous).Any(c => c.Section == "Army Rules"), "Ambiguous rules displayed");
        });
        test("Army-rule visibility retains conditional detachment exclusions through serialization", () =>
        {
            var docs = Documents();
            docs[0].Root["catalogueLinks"]![0]!["importRootEntries"] = true;
            docs[0].Root["sharedRules"]![0]!["modifiers"] = JsonNode.Parse("""
                [{"field":"hidden","type":"set","value":true,"conditionGroups":[{"type":"and","conditions":[
                  {"scope":"primary-catalogue","childId":"library","type":"instanceOf"},
                  {"scope":"roster","field":"selections","childId":"host","type":"atLeast","value":1}]}]}]
                """);
            var scope = BsDataImporter.ImportArmyRules(docs, source).Single();
            var data = DataJson.Read<Dataset>(DataJson.Write(Data(scope)));
            var normal = ArmyList.New("Fixture");
            Check(ArmyReferences.ForArmy(normal, data).Any(c => c.Name == "Inline rule"), "Unconditional availability lost");
            Check(!ArmyReferences.ForArmy(normal with { Detachment = "Host" }, data).Any(c => c.Name == "Inline rule"), "Detachment visibility ignored");
            var card = scope.Cards.Single(c => c.Name == "Inline rule");
            Check(ReferenceModel.ContentHash(card) != ReferenceModel.ContentHash(card with { Applicability = null }), "Visibility omitted from conflict identity");
        });
        test("Army-rule candidate validation rejects partial, misattributed and undisplayable collections", () =>
        {
            var scope = BsDataImporter.ImportArmyRules(Documents(), source).Single(); var data = Data(scope);
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(data, [scope]).Count == 0, "Valid source candidate rejected");
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(data with { ReferenceCards = scope.Cards.Skip(1).ToList() }, [scope]).Count > 0, "Nonempty partial scope accepted");
            var foreign = scope.Cards[0] with { Name = "Wrong rule", Details = scope.Cards[0].Details! with { SourceCardId = "other" } };
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(data with { ReferenceCards = [..scope.Cards, foreign] }, [scope]).Count > 0, "Wrongly attributed rule accepted");
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(data with { ReferenceCards = [..scope.Cards, foreign with { Details = foreign.Details! with { SourceCardId = "unverified" } }] }, [scope]).Count > 0,
                "Unverified source-owned rule accepted");
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(data with { ReferenceCards = scope.Cards.Select(c => c with { Details = c.Details! with { Status = "Unresolved" } }).ToList() }, [scope]).Count > 0, "Hidden source rules accepted");
        });
        test("Army-rule recovery preserves text, manual ownership, tombstones, source revisions and ambiguity", () =>
        {
            var scope = BsDataImporter.ImportArmyRules(Documents(), source).Single(); var data = Data(scope);
            var old = scope.Cards[0] with { Text = "Edited descriptive prose", Details = scope.Cards[0].Details! with { Status = "Unresolved" } };
            var recovered = ArmyRuleLibraryRecovery.Apply(data with { ReferenceCards = [old] }, [scope]);
            Check(recovered.ReferenceCards!.Count == 4 && recovered.ReferenceCards[0].Text == old.Text
                && recovered.ReferenceCards[0].Details!.Status == "Active", "Recovery lost prose or failed to restore references");
            Check(ReferenceEquals(recovered, ArmyRuleLibraryRecovery.Apply(recovered, [scope])), "Recovery not idempotent");
            foreach (var guarded in new[] { old with { Details = old.Details! with { Status = "Removed" } },
                old with { Details = old.Details! with { Status = "Obsolete" } }, old with { Source = source with { Name = "Manual" } },
                old with { Source = source with { ContentHash = new string('B', 64) } } })
                Check(ArmyRuleLibraryRecovery.Apply(data with { ReferenceCards = [guarded] }, [scope]).ReferenceCards!.Contains(guarded), "Owned/tombstoned reference changed");
            var empty = data with { ReferenceCards = [] };
            Check(ReferenceEquals(empty, ArmyRuleLibraryRecovery.Apply(empty, [scope with { Source = source with { Edition = "10" } }])), "Cross-edition recovery");
            Check(ReferenceEquals(empty, ArmyRuleLibraryRecovery.Apply(empty, [scope, scope with { Cards = [] }])), "Conflicting evidence guessed");
            var excluded = old with { Details = old.Details! with { SourceCardId = "other", Status = "Active" }, Name = "Misattributed" };
            Check(ArmyRuleLibraryRecovery.Apply(data with { ReferenceCards = [excluded] }, [scope]).ReferenceCards![0].Details!.Status == "Obsolete", "Known wrong attribution survived");
            var legacyExcluded = excluded with { Name = "Other faction rule", Details = null };
            Check(ArmyRuleLibraryRecovery.Apply(data with { ReferenceCards = [legacyExcluded] }, [scope]).ReferenceCards![0].Details!.Status == "Obsolete", "Legacy exclusion without source ID survived");
            Check(ArmyRuleLibraryRecovery.Apply(data with { ReferenceCards = [legacyExcluded with { Source = source with { Name = "Manual" } }] }, [scope])
                .ReferenceCards![0].Details == null, "Legacy exclusion removed manual data");
            var pack = new RulesPack("owned", 1, source, ["Fixture"], [], [], [], RemoveCards: [new("Fixture", "Army Rules", old.Name)]);
            Check(!ArmyRuleLibraryRecovery.Apply(empty with { RulesPacks = [pack] }, [scope]).ReferenceCards!.Any(c => c.Name == old.Name), "Rules-pack removal resurrected");
        });
        test("Pinned bundled army-rule coverage survives recovery and all-faction display", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            var issues = ArmyRuleLibraryRecovery.ValidateCandidate(data, ArmyRuleLibraryRecovery.BundledEvidence);
            Check(issues.Count == 0, string.Join("\n", issues));
            foreach (var (faction, name) in new[] { ("Tyranids", "Synapse"), ("Tyranids", "Shadow in the Warp"),
                ("Astra Militarum", "Voice Of Command"), ("Drukhari", "Power from Pain"),
                ("Titan Legions", "Towering Example"), ("Chaos Titan Legions", "Towering Example") })
                Check(ArmyReferences.ForArmy(ArmyList.New(faction), data).Any(c => c.Section == "Army Rules" && c.Name == name), faction + ": missing " + name);
            Check(!ArmyReferences.ForArmy(ArmyList.New("Aeldari"), data).Any(c => c.Section == "Army Rules" && c.Name == "Power from Pain"), "Drukhari rule still attributed to Aeldari");
            var legacy = data with { ReferenceCards = data.ReferenceCards!.Where(c => c.Section != "Army Rules").ToList() };
            var restored = ArmyRuleLibraryRecovery.ApplyBundled(legacy);
            Check(ArmyRuleLibraryRecovery.ValidateCandidate(restored, ArmyRuleLibraryRecovery.BundledEvidence).Count == 0, "Legacy recovery incomplete");
            Check(ReferenceEquals(data.Units, restored.Units), "Recovery changed unit data");
        });
        test("Existing compatibility normalization preserves older army-rule tombstones", () =>
        {
            var oldSource = source with { Date = "2026-10-02" };
            var removed = ReferenceModel.Canonical(new("Space Marines", "Army Rules", "Combat Doctrines", "Removed", Source: oldSource,
                Details: new(Status: "Removed")), oldSource);
            var obsolete = ReferenceModel.Canonical(new("Space Marines", "Army Rules", "Oath of Moment", "Retired", Source: oldSource,
                Details: new(Status: "Obsolete")), oldSource);
            var data = new Dataset(oldSource, [new("Space Marines", "Unit", [new(1, 50)])], ReferenceCards: [removed, obsolete],
                FactionContexts: [new("Space Marines", "e0af-67df-9d63-8fb7", [])]);
            var normalized = ReferenceData.Normalize(data);
            Check(normalized.ReferenceCards!.Contains(removed) && normalized.ReferenceCards.Contains(obsolete), "Compatibility cleanup erased a tombstone");
            Check(!ArmyReferences.ForArmy(ArmyList.New("Space Marines"), normalized).Any(c => c.Section == "Army Rules"), "Compatibility fallback resurrected a removed rule");
        });
        test("Independent pinned archive confirms published army-rule identity and scope coverage", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            var archive = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var evidence = ArmyRuleLibraryRecovery.ReadArchive(archive, data.Source);
            Check(evidence.Count == 36 && evidence.All(s => s.Unresolved.Count == 0), "Source scope coverage changed");
            var issues = ArmyRuleLibraryRecovery.ValidateCandidate(data, evidence);
            Check(issues.Count == 0, string.Join("\n", issues));
            var missingContexts = data with { FactionContexts = null, Detachments = [], ReferenceCards = [] };
            var recovered = ArmyRuleLibraryRecovery.Apply(missingContexts, evidence);
            Check(recovered.FactionContexts?.Count == 36 && ArmyRuleLibraryRecovery.ValidateCandidate(recovered, evidence).Count == 0,
                "Old bundle without detachment/faction metadata could not recover safely");
        });
    }
}
