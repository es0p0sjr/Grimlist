using System.Net;
using System.Text.Json;
using GrimList.Application;
using GrimList.Core;
using GrimList.Platform.Windows;

internal static class StratagemRefreshChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe source accepted"); }
    private static readonly SourceInfo CatalogueSource = new("BSData fixture", new string('a', 40), "", "https://example.test/catalogue", Edition: "11", ContentHash: new string('a', 64));
    private static readonly SourceInfo ReferenceSource = new("wahapedia reference source", "v1", "", "https://wahapedia.ru/wh40k11ed/factions/example/", Edition: "11", ContentHash: new string('b', 64));
    private static Dataset Catalogue() => new(CatalogueSource, [new("Example", "Fixture unit", [new(1, 100)], Profile: new(), Id: "fixture-unit")],
        Detachments: [new("Example", "First", Cost: "1 DP", Source: CatalogueSource, Id: "one", FactionId: "faction"), new("Example", "Second", Cost: "1 DP", Source: CatalogueSource, Id: "two", FactionId: "faction")],
        ReferenceCards: [new("Example", "Detachments", "Fixture rule", "Original rule", "First", Source: CatalogueSource)]);
    private static string Card(string scope, string name, string effect = "Fixture effect.") => $"""
        <div class="str11Wrap"><div class="str11Name">{name}</div><div class="str11CP">1 CP</div>
        <div class="str11Type">{scope} – Strategic Ploy Stratagem</div><div class="str11Text"><p>Fixture flavour</p>
        <b>WHEN:</b> Your Command phase.<br><b>TARGET:</b> One fixture unit.<br><b>EFFECT:</b> {effect}</div></div>
        """;
    private static string Page(string first = "Updated fixture.") => "<html><head><title>Example [Warhammer 40,000 11th edition]</title></head><body><h1>Example</h1>"
        + "<h2 class='outline_header' id='one'>First<span class='dpPts'>1DP</span></h2><h2>Stratagems</h2>" + Card("First", "Shared name", first)
        + "<h2 class='outline_header' id='two'>Second<span class='dpPts'>1DP</span></h2><h2>Stratagems</h2>" + Card("Second", "New card")
        + "<h2>Boarding Actions</h2>" + Card("First", "Wrong mode") + "</body></html>";
    private static Dataset Import(Dataset catalogue, Dataset pack)
    {
        var result = ReferenceMerge.Prepare(catalogue, pack);
        Check(!result.HasConflicts, "Unexpected owner conflict");
        return catalogue with { ReferenceCards = result.Cards, ReferenceCoverage = result.Coverage };
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => send(request, cancellation); }
    public static void Run(Action<string, Action> test, string root)
    {
        var catalogue = Catalogue();
        var parsed = StratagemHtmlReader.Parse(Page(), catalogue, "Example", ReferenceSource);
        test("Generic HTML refresh binds current scopes and excludes flavour and other game modes", () =>
        {
            Check(parsed.ReferenceCards!.Count == 2 && parsed.ReferenceCoverage!.All(c => c.Authoritative && c.CatalogueFingerprint != null), "Coverage not proven");
            Check(parsed.ReferenceCards.All(c => !c.Text.Contains("Fixture flavour") && !c.Name.Contains("Wrong")), "Non-operative text leaked");
            var merged = DatasetMerge.Prepare(catalogue, parsed, ImportLayer.Profiles).Dataset;
            Check(merged.ReferenceCards!.Count(c => c.Section == "Stratagems" && c.Details!.Status == "Active") == 2, "Valid refresh was hidden");
        });
        test("Same-name revisions replace content, retire absent owned cards and fill a new detachment", () =>
        {
            var old = StratagemHtmlReader.Parse(Page("Old fixture."), catalogue, "Example", ReferenceSource);
            var retired = ReferenceModel.Canonical(old.ReferenceCards![0] with { Name = "Vanished", Details = old.ReferenceCards[0].Details! with { Id = "vanished", SourceCardId = "vanished" } }, ReferenceSource);
            var manual = ReferenceModel.Canonical(new("Example", "Stratagems", "Manual", "Retain manual", "First", Source: ReferenceSource with { Url = "https://example.test/manual" }), ReferenceSource);
            var current = Import(catalogue, old with { ReferenceCards = [old.ReferenceCards[0], retired, manual] });
            var result = DatasetMerge.Prepare(current, parsed, ImportLayer.Profiles).Dataset;
            Check(result.ReferenceCards!.Single(c => c.Name == "Shared name").Text.Contains("Updated fixture"), "Reused identity retained stale text");
            Check(result.ReferenceCards!.Single(c => c.Name == "Vanished").Details!.Status == "Removed", "Disappeared card remains active");
            Check(result.ReferenceCards!.Single(c => c.Name == "Manual").Details!.Status == "Active", "Other source removed");
            Check(new ReferenceIndex(result).ForArmy(ArmyList.New("Example") with { Detachment = "Second", Detachments = ["Second"] }, "11").Any(c => c.Name == "New card"), "New scope not visible");
            var repeated = DatasetMerge.Prepare(result, parsed, ImportLayer.Profiles).Dataset;
            Check(DataJson.Write(result.ReferenceCards!.OrderBy(c => c.Details!.Id)) == DataJson.Write(repeated.ReferenceCards!.OrderBy(c => c.Details!.Id))
                && DataJson.Write(result.ReferenceCoverage!.OrderBy(ReferenceMerge.CoverageKey)) == DataJson.Write(repeated.ReferenceCoverage!.OrderBy(ReferenceMerge.CoverageKey)), "Repeated refresh changed rules or completeness");
        });
        test("Catalogue fingerprints detect changed rules with unchanged detachment ID but ignore price-only changes", () =>
        {
            var verified = Import(catalogue, parsed);
            var changed = verified with { ReferenceCards = verified.ReferenceCards!.Select(c => c.Section == "Detachments" ? c with { Text = "Replaced rule" } : c).ToList() };
            changed = ReferenceData.Normalize(changed);
            Check(changed.ReferenceCards!.Single(c => c.Name == "Shared name").Details!.Status == "Stale", "Same-ID rule change accepted");
            Check(changed.ReferenceCoverage!.First(c => c.Detachment == "First").ReviewReason != null, "Missing review reason");
            var report = ReferenceCoverageAudit.Build(changed).Factions.Single().Detachments;
            Check(report.Single(d => d.Detachment == "First").Coverage == "Partial" && report.Single(d => d.Detachment == "Second").Coverage == "Complete", "Unrelated scope completeness contaminated");
            Check(ReferenceEquals(changed, ReferenceCatalogueCompatibility.Apply(changed)), "Invalidation not idempotent");
            var priced = verified with { Units = verified.Units.Select(u => u with { Options = [new(1, 900)] }).ToList() };
            Check(ReferenceCatalogueCompatibility.Apply(priced).ReferenceCards!.Where(c => c.Section == "Stratagems").All(c => c.Details!.Status == "Active"), "Price update invalidated rules");
            var scoped = verified with { IsScoped = true, ReferenceCards = verified.ReferenceCards!.Where(c => c.Section == "Stratagems").ToList() };
            Check(ReferenceEquals(scoped, ReferenceCatalogueCompatibility.Apply(scoped)), "Partial view treated missing rules as a catalogue change");
        });
        test("Malformed, missing, duplicate, mixed-edition and truncated HTML never authorizes deletion", () =>
        {
            foreach (var bad in new[] { Page().Replace("11th", "10th"), Page().Replace("</html>", ""), Page().Replace("<h1>Example</h1>", "<h1>Other</h1>"),
                Page().Replace(Card("Second", "New card"), ""), Page().Replace("class=\"str11CP\"", "class=\"unknown\""),
                Page().Replace("Updated fixture.", ""), Page().Replace(Card("First", "Shared name", "Updated fixture."), Card("First", "Shared name") + Card("First", "Shared name")),
                Page().Replace("First – Strategic", "First Extra – Strategic"), Page().Replace("<h2>Stratagems</h2>", "<h2>Unknown section</h2>") })
                Reject(() => StratagemHtmlReader.Parse(bad, catalogue, "Example", ReferenceSource));
        });
        test("Repeated effect blocks, lists and tooltip labels survive without FAQ payloads", () =>
        {
            var page = Page("<ol start='2'><li>First item.</li><li value='5'>Second item.</li></ol><span class='tooltip'>TERM<span class='tooltip-content'>Hidden payload</span></span><div class='faq'>Hidden FAQ</div><br>WHEN: Later.<br>EFFECT: Next effect.");
            var card = StratagemHtmlReader.Parse(page, catalogue, "Example", ReferenceSource).ReferenceCards!.First();
            Check(card.Text.Contains("2. First item.") && card.Text.Contains("5. Second item.") && card.Text.Contains("TERM") && !card.Text.Contains("Hidden"), "Operative formatting corrupted");
            Check(card.Text.IndexOf("First item.", StringComparison.Ordinal) < card.Text.IndexOf("WHEN: Later", StringComparison.Ordinal), "Repeated block order lost");
        });
        test("Downloaded source metadata, stable semantic IDs and HTTP acquisition work for an arbitrary faction", () =>
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page()) })));
            var result = new StratagemReferenceClient(http).Refresh(new(catalogue, [], ""), Path.Combine(root, "refresh"), sourceManifest: parsed).GetAwaiter().GetResult();
            Check(result.Warnings.Count == 0 && result.Dataset.ReferenceCards!.Count(c => c.Section == "Stratagems") == 2, "Acquisition failed");
            var source = result.Dataset.ReferenceCards!.First(c => c.Section == "Stratagems").Source!;
            Check(source.ContentHash.Length == 64 && source.ImportedAt.Length > 0 && source.Date == "", "False freshness or missing provenance");
            Check(File.Exists(Path.Combine(root, "refresh", "stratagem-references", ReferenceModel.Owner(source), "raw.html")), "Validated raw source not cached");
            var pack = ReferenceLibraryRecovery.ReadBundled();
            foreach (var card in pack.ReferenceCards!.Where(c => c.Faction == "Adeptus Custodes"))
                Check(StratagemHtmlReader.SemanticId(card.Faction, card.Detachment, card.Name) == card.Details!.SourceCardId, "Existing semantic identity changed");
        });
        test("HTTP failures and invalid pages retain cards, downgrade completeness and do not erase the last compatibility proof", () =>
        {
            var verified = Import(catalogue, parsed);
            foreach (var response in new[] { new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Unavailable</html>") } })
            {
                using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
                var result = new StratagemReferenceClient(http).Refresh(new(catalogue, [], ""), root, sourceManifest: parsed).GetAwaiter().GetResult();
                Check(result.Warnings.Count == 1, "Failure not reported");
                var merged = DatasetMerge.Prepare(verified, result.Dataset, ImportLayer.Combined).Dataset;
                Check(merged.ReferenceCards!.Count(c => c.Section == "Stratagems" && c.Details!.Status == "Active") == 2, "Failure removed valid history");
                Check(merged.ReferenceCoverage!.All(c => !c.Authoritative && c.State == "Partial" && c.CatalogueFingerprint != null), "Failure kept complete claim or lost compatibility proof");
            }
        });
        test("Cancelled refresh propagates cancellation rather than publishing a partial success", () =>
        {
            using var cts = new CancellationTokenSource();
            using var http = new HttpClient(new Handler((_, token) => { cts.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token); }));
            try { new StratagemReferenceClient(http).Refresh(new(catalogue, [], ""), root, cancellation: cts.Token, sourceManifest: parsed).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Cancellation swallowed");
        });
        test("Shared source pages download once and never adopt another faction's cards", () =>
        {
            var requests = 0;
            using var http = new HttpClient(new Handler((_, _) => { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page()) }); }));
            var shared = catalogue with { Detachments = catalogue.Detachments!.Append(new("Other", "First", Id: "other-one", FactionId: "other")).ToList() };
            var manifest = parsed with { ReferenceCards = parsed.ReferenceCards!.Concat(parsed.ReferenceCards!.Select(c => c with { Faction = "Other", Details = c.Details! with { FactionId = "other" } })).ToList() };
            var result = new StratagemReferenceClient(http).Refresh(new(shared, [], ""), Path.Combine(root, "shared-refresh"), sourceManifest: manifest).GetAwaiter().GetResult();
            Check(requests == 1 && result.Warnings.Count == 1, "Shared page downloaded repeatedly or mismatched scope accepted");
            Check(!result.Dataset.ReferenceCards!.Any(c => c.Faction == "Other"), "Shared page misattributed");
        });
        test("Redirects, oversized pages and custom catalogue identities cannot certify reference coverage", () =>
        {
            var oversized = new StringContent("short"); oversized.Headers.ContentLength = StratagemHtmlReader.MaximumBytes + 1L;
            foreach (var response in new[] { new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page()), RequestMessage = new(HttpMethod.Get, "https://wahapedia.ru/wh40k10ed/factions/example/") },
                new HttpResponseMessage(HttpStatusCode.OK) { Content = oversized } })
            {
                using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
                var result = new StratagemReferenceClient(http).Refresh(new(catalogue, [], ""), root, sourceManifest: parsed).GetAwaiter().GetResult();
                Check(result.Warnings.Count == 1 && !result.Dataset.ReferenceCards!.Any(c => c.Section == "Stratagems"), "Unsafe response accepted");
            }
            using var unused = new HttpClient(new Handler((_, _) => throw new Exception("Unmapped custom source requested")));
            var custom = catalogue with { Detachments = catalogue.Detachments!.Select(d => d with { FactionId = "custom" }).ToList() };
            Check(new StratagemReferenceClient(unused).Refresh(new(custom, [], ""), root, sourceManifest: parsed).GetAwaiter().GetResult().Warnings.Count == 1, "Custom identity not reported");
        });
        test("A verified refresh retires previously unresolved cards and explicit zero scopes remain separate from failed downloads", () =>
        {
            var current = Import(catalogue, parsed);
            var vanished = current.ReferenceCards!.First(c => c.Section == "Stratagems") with { Name = "Old card", Details = new(Id: "old", Edition: "11", SourceId: ReferenceModel.Owner(ReferenceSource), SourceCardId: "old", Scope: "Detachment", Detachments: [new("First", "one")], Status: "Unresolved") };
            current = current with { ReferenceCards = current.ReferenceCards!.Append(vanished).ToList() };
            var merged = ReferenceMerge.Prepare(current, parsed);
            Check(merged.Cards.Single(c => c.Name == "Old card").Details!.Status == "Removed", "Unresolved card left reusable after verified replacement");
            var zero = parsed with { ReferenceCards = [], ReferenceCoverage = [parsed.ReferenceCoverage!.First()] };
            Check(ReferenceMerge.Prepare(current, zero).Cards.Where(c => c.Section == "Stratagems" && c.Detachment == "First").All(c => c.Details!.Status == "Removed"), "Explicit zero coverage lost");
            var inconsistent = zero with { ReferenceCoverage = [zero.ReferenceCoverage![0] with { State = "Partial" }] };
            Check(ReferenceMerge.Prepare(current, inconsistent).Cards.Single(c => c.Name == "Shared name").Details!.Status == "Active", "Partial state authorized removal");
        });
        test("Explicit rules-pack overrides keep their cards and completeness when catalogue fingerprints change", () =>
        {
            var current = Import(catalogue, parsed);
            var owned = current.Detachments!.First();
            var pack = new RulesPack("fixture-override", 1, ReferenceSource, ["Example"], [], [owned], [], ReplaceCardScopes: [new("Example", "Stratagems", "First")]);
            current = current with { RulesPacks = [pack], ReferenceCards = current.ReferenceCards!.Select(c => c.Section == "Detachments" ? c with { Text = "Changed catalogue" } : c).ToList() };
            var normalized = ReferenceCatalogueCompatibility.Apply(current);
            Check(normalized.ReferenceCards!.Single(c => c.Name == "Shared name").Details!.Status == "Active"
                && normalized.ReferenceCoverage!.First(c => c.Detachment == "First").Authoritative, "Explicit authority invalidated");
        });
        test("Legacy source-pinned compatibility recovery rejects stale Custodes shape and preserves manual edits and tombstones", () =>
        {
            var bundled = ReferenceLibraryRecovery.ReadBundled();
            var definitions = bundled.Detachments!.Where(d => d.Faction == "Adeptus Custodes").ToList();
            definitions.Add(new("Adeptus Custodes", "Fixture newly discovered scope", Source: CatalogueSource, Id: "new-scope", FactionId: definitions[0].FactionId));
            var current = new Dataset(CatalogueSource, [new("Adeptus Custodes", "Fixture", [new(1, 1)])], Detachments: definitions,
                ReferenceCards: bundled.ReferenceCards!.Where(c => c.Faction == "Adeptus Custodes").ToList(), SchemaVersion: 4,
                ReferenceCoverage: bundled.ReferenceCoverage!.Where(c => c.Faction == "Adeptus Custodes").ToList(),
                DetachmentSources: [new("Adeptus Custodes", definitions[0].FactionId, CatalogueSource, definitions.Select(d => new DetachmentIdentity(d.Id, d.Name)).ToList(), [])]);
            var recovered = ReferenceRevisionRecovery.CheckLegacy(current, bundled);
            Check(recovered.ReferenceCards!.Where(c => c.Detachment == "Lions of the Emperor").All(c => c.Details!.Status == "Stale"), "Old Lions still active");
            Check(recovered.ReferenceCoverage!.All(c => !c.Authoritative && c.ReviewReason != null), "Old complete claims retained");
            Check(ReferenceEquals(recovered, ReferenceRevisionRecovery.CheckLegacy(recovered, bundled)), "Legacy migration repeats");
            var first = current.ReferenceCards![0];
            var manual = current with { ReferenceCards = current.ReferenceCards.Select(c => c == first ? c with { Text = "Manual edit" } : c).ToList() };
            Check(ReferenceRevisionRecovery.CheckLegacy(manual, bundled).ReferenceCards!.First().Text == "Manual edit"
                && ReferenceRevisionRecovery.CheckLegacy(manual, bundled).ReferenceCards!.First().Details!.Status == "Active", "Manual content invalidated");
            var tombstone = current with { ReferenceCards = current.ReferenceCards.Select(c => c == first ? c with { Details = c.Details! with { Status = "Removed" } } : c).ToList() };
            Check(ReferenceRevisionRecovery.CheckLegacy(tombstone, bundled).ReferenceCards!.First().Details!.Status == "Removed", "Tombstone resurrected");
            using var store = LibraryBootstrap.Open(new WindowsPaths(Path.Combine(root, "legacy-preview")), new WindowsSqliteConnectionFactory());
            store.ApplyImport(current, DataJson.Write(store.LoadLibrary().Dataset!));
            var before = DataJson.Write(store.LoadLibrary().Dataset);
            var preview = new LibraryWorkflow(store).Prepare(current, ImportLayer.Combined);
            Check(preview.Dataset.ReferenceCards!.Where(c => c.Detachment == "Lions of the Emperor").All(c => c.Details!.Status == "Stale")
                && preview.Warnings.Any(w => w.Contains("Previous references")), "Preview hid legacy compatibility warning");
            Check(DataJson.Write(store.LoadLibrary().Dataset) == before, "Preview changed the library");
        });
        test("Real cached source HTML parses its historical catalogue and cannot certify a new catalogue scope", () =>
        {
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            using var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tools/reference_acquisition/fixtures/adeptus-custodes.snapshot.json")));
            var raw = File.ReadAllText(Path.Combine(repo, "tests/fixtures/wahapedia/snapshots", pointer.RootElement.GetProperty("Snapshot").GetString()!, "raw"));
            var pack = ReferenceLibraryRecovery.ReadBundled();
            var old = catalogue with { Detachments = pack.Detachments!.Where(d => d.Faction == "Adeptus Custodes").ToList() };
            var historical = StratagemHtmlReader.Parse(raw, old, "Adeptus Custodes", pack.ReferenceCards!.First(c => c.Faction == "Adeptus Custodes").Source!);
            Check(historical.ReferenceCards!.Count == 45, "Real source parser lost cards");
            Reject(() => StratagemHtmlReader.Parse(raw, old with { Detachments = old.Detachments!.Append(new("Adeptus Custodes", "Solar Watch", Id: "solar", FactionId: old.Detachments[0].FactionId)).ToList() }, "Adeptus Custodes", ReferenceSource));
        });
        test("Refreshed references survive SQLite reopen without an older bundle restoring retired cards", () =>
        {
            var data = Import(catalogue, parsed);
            using var store = LibraryBootstrap.Open(new WindowsPaths(Path.Combine(root, "refresh-reopen")), new WindowsSqliteConnectionFactory());
            store.ApplyImport(data, DataJson.Write(store.LoadLibrary().Dataset!));
            var loaded = store.LoadLibrary().Dataset!;
            Check(DataJson.Write(loaded.ReferenceCoverage) == DataJson.Write(data.ReferenceCoverage), "Coverage proof lost in storage");
            var old = parsed with { ReferenceCards = parsed.ReferenceCards!.Select(c => c with { Text = "Old bundle" }).ToList() };
            var supplemented = ReferenceLibraryRecovery.Supplement(loaded, old);
            Check(!supplemented.ReferenceCards!.Any(c => c.Text == "Old bundle"), "Startup restored older content");
        });
        test("Adding a detachment never invalidates proven unchanged scopes; legacy quarantine repairs per detachment", () =>
        {
            var baselines = ReferenceCatalogueBaseline.Create(catalogue, parsed);
            var old = Import(catalogue, parsed) with { SchemaVersion = 4, DetachmentSources = [new("Example", "faction", CatalogueSource,
                catalogue.Detachments!.Select(d => new DetachmentIdentity(d.Id, d.Name)).ToList(), [])] };
            var extra = old with { Detachments = old.Detachments!.Append(new("Example", "Third", Source: CatalogueSource, Id: "three", FactionId: "faction")).ToList(),
                ReferenceCoverage = old.ReferenceCoverage!.Select(c => c with { CatalogueFingerprint = null }).ToList() };
            var added = ReferenceRevisionRecovery.CheckLegacy(extra, parsed, baselines);
            Check(added.ReferenceCards!.Where(c => c.Section == "Stratagems").All(c => c.Details!.Status == "Active"), "Unrelated addition hid existing scopes");
            var quarantined = extra with {
                ReferenceCoverage = extra.ReferenceCoverage!.Select(c => c with { CatalogueFingerprint = "shape:old-faction-proof", State = "Partial", Mode = "Partial", Authoritative = false, ReviewReason = ReferenceCatalogueCompatibility.LegacyReviewReason }).ToList(),
                ReferenceCards = extra.ReferenceCards!.Select(c => c.Section == "Stratagems" ? c with { Details = c.Details! with { Status = "Unresolved" } }
                    : c with { Text = "Changed codex rule" }).ToList()
            };
            var repaired = ReferenceRevisionRecovery.CheckLegacy(quarantined, parsed, baselines);
            var first = ArmyList.New("Example") with { Detachment = "First", Detachments = ["First"] };
            var second = first with { Detachment = "Second", Detachments = ["Second"] };
            Check(ArmyReferences.PreviousForArmy(first, repaired).Single().Name == "Shared name", "Previous card disappeared");
            Check(!ArmyReferences.ForArmy(first, repaired).Any(c => c.Name == "Shared name"), "Stale card presented as current");
            Check(ArmyReferences.ForArmy(second, repaired).Any(c => c.Name == "New card") && ArmyReferences.PreviousForArmy(second, repaired).Count == 0, "Unchanged scope was not restored");
            Check(ReferenceEquals(repaired, ReferenceRevisionRecovery.CheckLegacy(repaired, parsed, baselines)), "Recovery churn");
            var ambiguous = quarantined with { ReferenceCards = quarantined.ReferenceCards!.Select(c => c.Section == "Stratagems" ? c with { Details = c.Details! with { Match = "Ambiguous" } } : c).ToList() };
            Check(ArmyReferences.PreviousForArmy(first, ReferenceRevisionRecovery.CheckLegacy(ambiguous, parsed, baselines)).Count == 0, "Matching failure resurrected");
            var report = ReferenceCoverageAudit.Build(repaired).Factions.Single().Detachments.Single(d => d.Detachment == "First");
            Check(report.PreviousCount == 1 && report.StratagemCount == 0 && report.Missing.Any(m => m.Contains("previous reference")), "Previous/current distinction missing from both frontends' report");
        });
        test("Reference proofs are stable across platform JSON newline conventions", () =>
        {
            var windows = DataJson.Read<Dataset>(System.Text.Json.JsonSerializer.Serialize(catalogue, new JsonSerializerOptions(DataJson.Options) { NewLine = "\r\n" }));
            var android = DataJson.Read<Dataset>(System.Text.Json.JsonSerializer.Serialize(catalogue, new JsonSerializerOptions(DataJson.Options) { NewLine = "\n" }));
            Check(ReferenceCatalogueCompatibility.Fingerprint(windows, "Example", "First") == ReferenceCatalogueCompatibility.Fingerprint(android, "Example", "First"), "Platform-specific compatibility hash");
            Check(!ReferenceCatalogueBaseline.Key(parsed.ReferenceCoverage!.First()).Contains('\n'), "Multiline baseline identity");
            var enriched = catalogue with { Detachments = catalogue.Detachments!.Select(d => d with {
                Tags = ["Imported category"], CannotCombineWithTags = [], GroupLimits = [], Applicability = new("constant", "1") }).ToList() };
            Check(ReferenceCatalogueCompatibility.Fingerprint(enriched, "Example", "First") == ReferenceCatalogueCompatibility.Fingerprint(catalogue, "Example", "First"), "Importer metadata enrichment masquerades as a codex change");
        });
        test("Incomplete pages preserve good scopes; invalid cards cannot authorize deleting their scope", () =>
        {
            var missing = Page().Replace(Card("Second", "New card"), "");
            var partial = StratagemHtmlReader.Parse(missing, catalogue, "Example", ReferenceSource, allowPartial: true);
            Check(partial.ReferenceCards!.Single().Detachment == "First" && partial.ReferenceCoverage!.Single(c => c.Detachment == "First").Authoritative
                && !partial.ReferenceCoverage!.Single(c => c.Detachment == "Second").Authoritative, "Independent verified scope discarded");
            var bad = Page().Replace(Card("Second", "New card"), Card("Second", "New card").Replace("str11CP", "unknown"));
            var invalid = StratagemHtmlReader.Parse(bad, catalogue, "Example", ReferenceSource, allowPartial: true);
            Check(invalid.ReferenceCards!.Count == 1 && invalid.ReferenceCoverage!.Single(c => c.Detachment == "Second").ReviewReason!.Contains("Incomplete stratagem"), "Bad scope falsely complete");
            var old = Import(catalogue, parsed);
            var merged = DatasetMerge.Prepare(old, partial, ImportLayer.Profiles).Dataset;
            Check(merged.ReferenceCards!.Any(c => c.Name == "New card" && c.Details!.Status == "Active"), "Missing section removed previous data");
        });
        test("A codex refresh replaces stale same-ID rules, fills new scopes and survives reopen with no stale resurrection", () =>
        {
            var current = Import(catalogue, parsed);
            current = current with { ReferenceCards = current.ReferenceCards!.Select(c => c.Section == "Detachments" ? c with { Text = "New codex rule" } : c).ToList() };
            current = ReferenceData.Normalize(current);
            var retired = current.ReferenceCards!.First(c => c.Section == "Stratagems") with { Name = "Absent old card", Details = current.ReferenceCards!.First(c => c.Section == "Stratagems").Details! with { Id = "absent", SourceCardId = "absent" } };
            current = current with { ReferenceCards = current.ReferenceCards!.Append(retired).Where(c => c.Name != "New card").ToList() };
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page("Current codex text.")) })));
            var incomingCatalogue = current with { ReferenceCards = current.ReferenceCards.Where(c => c.Section != "Stratagems").ToList(), ReferenceCoverage = [] };
            var downloaded = new StratagemReferenceClient(http).Refresh(new(incomingCatalogue, [], ""), Path.Combine(root, "codex-http"), sourceManifest: parsed).GetAwaiter().GetResult();
            Check(downloaded.Warnings.Count == 0, "Verified acquisition failed");
            using var store = WindowsServices.OpenStore(Path.Combine(root, "codex-reopen"));
            store.ApplyImport(current);
            var workflow = new LibraryWorkflow(store); var preview = workflow.Prepare(downloaded.Dataset, ImportLayer.Combined);
            Check(preview.CanApply, "Generic source-owned update conflicts"); workflow.Apply(preview);
            var loaded = ReferenceLibraryRecovery.Supplement(store.LoadLibrary().Dataset!, parsed);
            var army = ArmyList.New("Example") with { Detachments = ["First", "Second"] };
            var visible = ArmyReferences.ForArmy(army, loaded).Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
            Check(visible.Count == 2 && visible.Single(c => c.Name == "Shared name").Text.Contains("Current codex text"), "Current replacement content missing");
            Check(ArmyReferences.PreviousForArmy(army, loaded).Count == 0 && loaded.ReferenceCards!.Single(c => c.Name == "Absent old card").Details!.Status == "Removed", "Stale history resurrected");
        });
        test("Shipped reference baselines reproduce the exact matched historical catalogue without packing user data", () =>
        {
            var path = Path.Combine("tests", "fixtures", "bsdata", "11e", "da7248777da54218034f09b6271ad89ff6b68a77", "reimported.json");
            var expected = ReferenceCatalogueBaseline.Create(DataJson.Read<Dataset>(File.ReadAllText(path)), ReferenceLibraryRecovery.ReadBundled());
            Check(expected.Count > 0 && expected.SequenceEqual(ReferenceCatalogueBaseline.Bundled), "Missing or mismatched pinned baseline resource");
        });
        test("Current Custodes catalogue rejects a successful HTTP response containing the known old codex page", () =>
        {
            var folder = Path.Combine("tests", "fixtures", "bsdata", "11e", "79c2aff2dcf7663b53e6b1c52fb36a564d5ab054");
            var source = DataJson.Read<SourceInfo>(File.ReadAllText(Path.Combine(folder, "source.json")));
            var archive = File.ReadAllBytes(Path.Combine(folder, "repository.zip"));
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase), "Pinned catalogue hash");
            var documents = BsDataDocument.ReadArchive(archive);
            var primary = documents.Single(d => BsDataImporter.FactionName(d) == "Adeptus Custodes");
            var dependencies = new HashSet<string>();
            void Include(string id) { if (!dependencies.Add(id)) return; foreach (var link in BsDataDocument.Children(documents.Single(d => d.Id == id).Root, "catalogueLinks")) Include(BsDataDocument.Text(link, "targetId")); }
            Include(primary.Id); Include(BsDataDocument.Text(primary.Root, "gameSystemId"));
            var imported = BsDataImporter.ImportAll(documents.Where(d => dependencies.Contains(d.Id)).ToList(), source, authoritativeDownload: true).Dataset;
            imported = imported with { Units = imported.Units.Where(u => u.Faction == "Adeptus Custodes").ToList(),
                Detachments = imported.Detachments!.Where(d => d.Faction == "Adeptus Custodes").ToList(),
                DetachmentSources = imported.DetachmentSources!.Where(s => s.Faction == "Adeptus Custodes").ToList(),
                ReferenceCards = imported.ReferenceCards!.Where(c => c.Faction == "Adeptus Custodes").ToList() };
            var bundle = ReferenceLibraryRecovery.ReadBundled();
            var existing = ReferenceLibraryRecovery.Supplement(imported, bundle);
            var lions = ArmyList.New("Adeptus Custodes") with { Detachment = "Lions of the Emperor", Detachments = ["Lions of the Emperor"] };
            Check(ArmyReferences.PreviousForArmy(lions, existing).Count == 6, "Known older Lions references inaccessible");
            using var pointer = JsonDocument.Parse(File.ReadAllText("tools/reference_acquisition/fixtures/adeptus-custodes.snapshot.json"));
            var bytes = File.ReadAllBytes(Path.Combine("tests/fixtures/wahapedia/snapshots", pointer.RootElement.GetProperty("Snapshot").GetString()!, "raw"));
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            var result = new StratagemReferenceClient(http).Refresh(new(imported, [], ""), Path.Combine(root, "known-old-page"), sourceManifest: bundle).GetAwaiter().GetResult();
            Check(result.Warnings.Count > 0 && !result.Dataset.ReferenceCards!.Any(c => c.Section == "Stratagems"), "Known outdated page re-certified as current");
            Check(result.Dataset.ReferenceCoverage!.Count == 13 && result.Dataset.ReferenceCoverage.All(c => !c.Authoritative), "Missing scopes silently certified");
            var merged = DatasetMerge.Prepare(existing, result.Dataset, ImportLayer.Combined).Dataset;
            Check(ArmyReferences.PreviousForArmy(lions, merged).Count == 6 && !ArmyReferences.ForArmy(lions, merged).Any(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)), "Failed refresh lost history or reactivated old Lions");
            var solar = lions with { Detachment = "Solar Watch", Detachments = ["Solar Watch"] };
            Check(ArmyReferences.MissingStratagemDetachments(solar, merged).SequenceEqual(["Solar Watch"]), "Missing Solar Watch claimed complete-zero");
        });
    }
}
