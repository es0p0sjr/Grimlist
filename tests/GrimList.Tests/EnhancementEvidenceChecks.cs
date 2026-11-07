using GrimList.Application;
using GrimList.Core;

internal static class EnhancementEvidenceChecks
{
    public static void Run(Action<string, Action> test)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var source = new SourceInfo("BSData fixture", "revision-a", "", "", Edition: "11", ContentHash: "hash-a");
        var condition = new EnhancementCondition("keyword", "Infantry", "instanceOf");
        var card = new ReferenceCard("Test", "Enhancements", "Gift", "Bearer gains an ability.", "Host", "15 pts", source,
            Details: new(SourceCardId: "entry-a"), Eligibility: condition);
        var legacy = new Dataset(source, [], ReferenceCards: [card with { Eligibility = null }], ReferenceOnly: true);
        test("Enhancement recovery ignores descriptive prose, formatting and prices but preserves installed content", () =>
        {
            foreach (var text in new[] { "Changed description", card.Text + "\n\nAssault:\nAn unrelated weapon rule.", "**Bearer** gains an ability.\r\n" })
            {
                var changed = legacy with { ReferenceCards = [legacy.ReferenceCards![0] with { Text = text, Cost = "25 pts" }] };
                var repaired = EnhancementLibraryRepair.Apply(changed, [card]);
                Check(repaired.ReferenceCards![0].Eligibility == condition, "Description or price blocked recovery");
                Check(repaired.ReferenceCards[0] with { Eligibility = null } == changed.ReferenceCards[0], "Recovery rewrote content");
                Check(ReferenceEquals(repaired, EnhancementLibraryRepair.Apply(repaired, [card])), "Recovery is not idempotent");
            }
        });
        test("Enhancement recovery pins revision, edition, faction, detachment and source entry identity", () =>
        {
            foreach (var other in new[] { card with { Source = source with { ContentHash = "hash-b" } },
                card with { Source = source with { Edition = "10" } }, card with { Faction = "Other" },
                card with { Detachment = "Other" }, card with { Details = new(SourceCardId: "entry-b") } })
                Check(ReferenceEquals(legacy, EnhancementLibraryRepair.Apply(legacy, [other])), "Different semantic identity recovered");
            Check(EnhancementLibraryRepair.Apply(legacy, [card with { Name = "Renamed presentation" }]).ReferenceCards![0].Eligibility != null,
                "Stable source entry ID was ignored");
        });
        test("Ambiguous and conflicting enhancement evidence fails closed without choosing a first record", () =>
        {
            var noId = legacy with { ReferenceCards = [legacy.ReferenceCards![0] with { Details = null }] };
            foreach (var other in new[] { card with { Eligibility = new("constant", "1") }, card with { Eligibility = null }, card with { IsUpgrade = true } })
                foreach (var records in new[] { new[] { card, other }, new[] { other, card } })
                {
                    List<string> issues = [];
                    Check(ReferenceEquals(legacy, EnhancementLibraryRepair.Apply(legacy, records, issues.Add)), "Conflict granted eligibility");
                    Check(issues.Any(s => s.Contains("Conflicting")), "Conflict diagnostic missing");
                }
            Check(ReferenceEquals(noId, EnhancementLibraryRepair.Apply(noId, [card, card with { Details = new(SourceCardId: "entry-b") }])), "Ambiguous name guessed");
            Check(EnhancementLibraryRepair.Apply(noId, [card]).ReferenceCards![0].Eligibility != null, "Unique legacy identity failed");
            Check(EnhancementLibraryRepair.Apply(legacy, [card, card]).ReferenceCards![0].Eligibility != null, "Identical evidence was treated as a conflict");
            var explicitData = legacy with { ReferenceCards = [card] };
            Check(ReferenceEquals(explicitData, EnhancementLibraryRepair.Apply(explicitData, [card with { Eligibility = new("constant", "1"), IsUpgrade = true }])),
                "Conflict overwrote explicit eligibility or classification");
            var conflictingKind = legacy with { ReferenceCards = [legacy.ReferenceCards![0] with { IsUpgrade = true }] };
            Check(ReferenceEquals(conflictingKind, EnhancementLibraryRepair.Apply(conflictingKind, [card])), "Conflicting upgrade classification granted eligibility");
        });
        test("Candidate validation rejects missing, conflicting, unknown-granted and deleted source eligibility", () =>
        {
            var pack = EnhancementLibraryRepair.Create(source, [card]);
            Check(EnhancementEvidence.ValidateCandidate(legacy, [pack], out _).Count > 0, "Missing metadata passed candidate validation");
            var repaired = EnhancementLibraryRepair.Apply(legacy, [card]);
            Check(EnhancementEvidence.ValidateCandidate(repaired, [pack], out _).Count == 0, "Valid candidate rejected");
            Check(EnhancementEvidence.ValidateCandidate(repaired with { ReferenceCards = [card with { Eligibility = new("constant", "1") }] }, [pack], out _).Count > 0,
                "Conflicting metadata passed");
            Check(EnhancementEvidence.ValidateCandidate(repaired, [EnhancementLibraryRepair.Create(source, [card with { Eligibility = null }])], out _).Count > 0,
                "Unsupported source became permission");
            Check(EnhancementEvidence.ValidateCandidate(repaired, [EnhancementLibraryRepair.Create(source, [card,
                card with { Name = "Second gift", Details = new(SourceCardId: "entry-b") }])], out _).Count > 0, "Deleted card passed");
        });
        test("Historical effective-datasheet packs cannot recover or overwrite enhancement eligibility through text hashes", () =>
        {
            var metadata = new EffectiveDatasheetRecovery.CardMetadata(card.Faction, Enhancements.Key(card),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(card.Text))),
                [], new("constant", "1"));
            var pack = new EffectiveDatasheetRecovery.Pack(source.ContentHash, [], [metadata]);
            Check(EffectiveDatasheetRecovery.Apply(legacy, [pack]).ReferenceCards![0].Eligibility == null, "Effect pack bypassed source eligibility recovery");
            var explicitData = legacy with { ReferenceCards = [card] };
            Check(EffectiveDatasheetRecovery.Apply(explicitData, [pack]).ReferenceCards![0].Eligibility == condition, "Effect pack overwrote an explicit restriction");
        });
        test("Bundled enhancement eligibility agrees with an independent reimport of the pinned source archive", () =>
        {
            var data = BootstrapRelease.ReadBundled().Dataset;
            var archive = "tests/fixtures/bsdata/11e/cc1830fbfead059f8059ed6c7f8e73cd4c390908/repository.zip";
            var bytes = File.ReadAllBytes(archive);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var provenance = data.ReferenceCards!.First(c => c.Source?.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase) == true).Source!;
            var imported = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(bytes), provenance);
            var subset = data with { ReferenceCards = data.ReferenceCards!.Where(c => c.Source?.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase) == true).ToList() };
            var errors = EnhancementEvidence.ValidateCandidate(subset, [EnhancementLibraryRepair.Create(provenance, imported)], out _);
            Check(errors.Count == 0, string.Join("\n", errors));
            Check(subset.ReferenceCards!.Where(c => c.Section == "Enhancements").All(c => c.Details?.SourceCardId.Length > 0), "Published source IDs missing");
        });
        test("Every bundled source-eligible enhancement survives shared selection on all eligible catalogue bearers", () =>
        {
            var result = EnhancementAcceptance.Run();
            Check(result.EligiblePairs > 1000 && result.SelectableCards > 500, "All-faction acceptance did not exercise the full catalogue");
            Console.WriteLine($"Enhancement acceptance: {result.SelectableCards} selectable cards, {result.EligiblePairs} eligible bearer/card pairs; {result.NoEligibleBearer.Count} cards have no eligible bearer in the tested catalogue configurations.");
        });
    }
}
