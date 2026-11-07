using GrimList.Core;
using GrimList.Application;
using GrimList.Platform;
using System.Text;

internal static class GenericReferenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe reference accepted"); }
    private static readonly SourceInfo Source = new("Structured fixture", "1", "2026-01-01", "", Edition: "11");
    private static ReferenceCard Card(string name = "Tactic", string owner = "fixture", string id = "tactic") =>
        new("Faction A", "Stratagems", name, "Fixture reference text.", "Alpha", "1 CP", Source,
            Details: new(Id: id, Edition: "11", SourceId: owner, SourceCardId: id, Scope: "Detachment", Detachments: [new("Alpha", "alpha-id")], Effect: "Fixture effect."));
    private static Dataset Library() => new(Source, [new("Faction A", "Unit", [new(1, 50)])],
        Detachments: [new("Faction A", "Alpha", Id: "alpha-id", FactionId: "faction-a"), new("Faction A", "Beta", Id: "beta-id", FactionId: "faction-a")]);
    private static Dataset Pack(params ReferenceCard[] cards) => new(Source, [], ReferenceCards: cards.ToList(), ReferenceOnly: true, SchemaVersion: 2);
    private static Dataset With(Dataset data, ReferenceMergeResult result) => data with { ReferenceCards = result.Cards, ReferenceCoverage = result.Coverage, SchemaVersion = 2 };
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        test("One supplemental JSON adapter handles detachment, faction, multi-faction and edition collections", () =>
        {
            var data = Library() with { Units = [.. Library().Units, new("Faction B", "Second unit", [new(1, 60)])],
                Detachments = [.. Library().Detachments!, new("Faction B", "Gamma", Id: "gamma-id")] };
            var other = Card("Other faction tactic", id: "other") with { Faction = "Faction B", Detachment = "Gamma",
                Details = Card(id: "other").Details! with { Detachments = [new("Gamma", "gamma-id")] } };
            foreach (var mode in new[] { "Partial", "CompleteForDetachment", "CompleteForFaction", "CompleteForEdition" })
            {
                var pack = Pack(Card(), other) with { ReferenceCoverage = [new("fixture", "11",
                    Faction: mode is "CompleteForDetachment" or "CompleteForFaction" ? "Faction A" : "",
                    Detachment: mode == "CompleteForDetachment" ? "Alpha" : "",
                    State: mode == "Partial" ? "Partial" : "Complete", Mode: mode, Authoritative: mode != "Partial")] };
                var parsed = new StructuredJsonReferenceAdapter().Read("synthetic-multi-faction.json", DataJson.Write(pack), "11");
                var merged = With(data, ReferenceMerge.Prepare(data, parsed));
                Check(merged.ReferenceCards!.Count == 2, "Adapter lost one faction");
                Check(ArmyReferences.ForArmy(ArmyList.New("Faction B") with { Detachment = "Gamma" }, merged)
                    .Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 1, "Cross-faction filtering failed");
                var roundTrip = DataJson.Read<Dataset>(DataJson.Write(ReferenceModel.ExportDataset(merged)));
                Check(roundTrip.ReferenceCoverage!.Single().Mode == mode, "Coverage declaration changed on transfer");
                if (mode == "Partial") Check(ReferenceCoverageAudit.Build(merged).Factions.SelectMany(f => f.Detachments)
                    .All(d => d.Coverage is "Partial" or "Unknown"), "Partial input falsely certified completeness");
            }
        });
        test("Singular and plural BSData detachment containers use the same reference extraction", () =>
        {
            foreach (var groupName in new[] { "Detachment", "Detachments" })
            {
                var json = "{\"catalogue\":{\"id\":\"fixture\",\"name\":\"Fixture\",\"selectionEntryGroups\":[{\"id\":\"group\",\"name\":\"" + groupName +
                    "\",\"selectionEntries\":[{\"id\":\"det\",\"name\":\"Alpha\",\"type\":\"upgrade\",\"rules\":[{\"id\":\"rule\",\"name\":\"Rule\",\"description\":\"Synthetic test text.\"}]}]}]}}";
                var imported = new BsDataReferenceAdapter().Read("fixture.json", json, "11");
                Check(imported.Detachments!.Single().Id == "det" && imported.ReferenceCards!.Single().Detachment == "Alpha", "Container alias discarded source rules");
            }
        });
        test("A later declared catalogue edition does not adopt legacy references with an unknown edition", () =>
        {
            var legacy = Library() with { Source = Source with { Edition = "" }, ReferenceCards =
                [new("Faction A", "Army Rules", "Legacy rule", "Old reference with unspecified edition.")] };
            legacy = ReferenceModel.ExportDataset(legacy);
            var merged = DatasetMerge.Prepare(legacy, Library(), ImportLayer.Points).Dataset;
            merged.Validate();
            var exported = ReferenceModel.ExportDataset(merged);
            Check(exported.ReferenceCards!.Single().Details!.Edition == "", "Unknown edition was guessed");
            Check(!ArmyReferences.ForArmy(ArmyList.New("Faction A"), exported).Any(c => c.Name == "Legacy rule"), "Unknown edition displayed as current");
            Check(exported.ReferenceCards!.Single().Details!.Id == legacy.ReferenceCards!.Single().Details!.Id, "Legacy identity changed");
        });
        test("Empty authoritative reference collection removes owned scope while malformed input and stale reviews remain atomic", () =>
        {
            using var store = WindowsServices.OpenStore(Path.Combine(root, "reference-empty-update"));
            store.Import(Library()); var workflow = new LibraryWorkflow(store);
            workflow.Apply(workflow.Prepare(Pack(Card()), ImportLayer.Profiles));
            var coverage = new ReferenceCoverage("fixture", "11", Faction: "Faction A", Detachment: "Alpha", State: "Complete", Mode: "CompleteForDetachment", Authoritative: true);
            var empty = Pack() with { ReferenceCoverage = [coverage] };
            empty.Validate();
            var preview = workflow.Prepare(empty, ImportLayer.Profiles);
            Check(preview.ReferenceChanges.Any(c => c.Status == "Removed"), "Explicit complete empty scope did not remove");
            var saved = DataJson.Write(store.LoadDataset());
            using var malformed = new PickedDocument("bad.json", new MemoryStream(Encoding.UTF8.GetBytes("{not JSON")));
            try { workflow.PrepareReferenceImportAsync(malformed).GetAwaiter().GetResult(); throw new Exception("Malformed source accepted"); }
            catch (System.Text.Json.JsonException) { }
            Check(saved == DataJson.Write(store.LoadDataset()), "Malformed read changed storage");
            workflow.Apply(workflow.Prepare(Pack(Card() with { Cost = "3 CP" }), ImportLayer.Profiles));
            Reject(() => workflow.Apply(preview));
            Check(store.LoadDataset()!.ReferenceCards!.Single().Cost == "3 CP", "Stale review overwrote update");
        });
        test("Portable canonical dataset transfers in both directions and retains source ownership and coverage", () =>
        {
            var data = With(Library(), ReferenceMerge.Prepare(Library(), Pack(Card()) with {
                ReferenceCoverage = [new("fixture", "11", Faction: "Faction A", Detachment: "Alpha", State: "VerifiedComplete", Mode: "CompleteForDetachment", Authoritative: true)] }));
            var exported = ReferenceModel.ExportDataset(data);
            var phone = DataJson.Read<Dataset>(DataJson.Write(exported));
            var imported = DatasetMerge.Prepare(null, phone, ImportLayer.Combined).Dataset;
            var back = DataJson.Read<Dataset>(DataJson.Write(ReferenceModel.ExportDataset(imported)));
            Check(DataJson.Write(exported.ReferenceCards) == DataJson.Write(back.ReferenceCards), "Reference round-trip changed canonical data");
            Check(DataJson.Write(exported.ReferenceCoverage) == DataJson.Write(back.ReferenceCoverage), "Coverage round-trip changed");
            var next = ReferenceMerge.Prepare(imported, Pack(Card() with { Cost = "2 CP" }));
            Check(next.Coverage.Single().State == "Partial", "Partial update retained an outdated completeness assertion");
        });
        var phoneIndex = Array.IndexOf(args, "--reference-phone-library");
        var archiveIndex = Array.IndexOf(args, "--reference-import-archive");
        if (archiveIndex >= 0)
            test("Real Mechanicus and Grey Knights plural detachment containers retain scoped source rules", () =>
            {
                var documents = BsDataDocument.ReadArchive(File.ReadAllBytes(args[archiveIndex + 1]));
                foreach (var faction in new[] { "Adeptus Mechanicus", "Grey Knights" })
                {
                    var document = documents.Single(d => BsDataImporter.FactionName(d) == faction);
                    var imported = BsDataImporter.ImportReferences(document, Source with { Name = "BSData fixture archive" });
                    Check(imported.Detachments!.Count > 1, faction + ": plural detachment group was omitted");
                    Check(imported.ReferenceCards!.Any(c => c.Section == "Detachments" && c.Detachment.Length > 0), faction + ": scoped rules missing");
                    Check(imported.ReferenceCards!.All(c => c.Section != "Stratagems"), "Non-stratagem text misclassified");
                }
            });
        if (phoneIndex >= 0 && archiveIndex >= 0)
            test("Real Adepta Sororitas, Orks and Death Guard references share adapters, matching, persistence and portable datasets", () =>
            {
                var phone = DataJson.Read<Dataset>(File.ReadAllText(args[phoneIndex + 1]));
                var documents = BsDataDocument.ReadArchive(File.ReadAllBytes(args[archiveIndex + 1]));
                var records = new List<object>();
                using var store = WindowsServices.OpenStore(Path.Combine(root, "real-generic-references"));
                // Real unit data, empty references: no downloaded/synthetic rules enter user data.
                store.ApplyImport(phone with { ReferenceCards = [], ReferenceCoverage = [] });
                var initialUnits = DataJson.Write(store.LoadDataset()!.Units);
                var workflow = new LibraryWorkflow(store);
                foreach (var faction in new[] { "Adepta Sororitas", "Orks" })
                {
                    var document = documents.First(d => BsDataImporter.FactionName(d) == faction);
                    var text = new System.Text.Json.Nodes.JsonObject { ["catalogue"] = document.Root.DeepClone() }.ToJsonString();
                    var incoming = new BsDataReferenceAdapter().Read(document.FileName, text, "11");
                    // Import the catalogue's native detachment IDs alongside its real unit subset.
                    store.ApplyImport(store.LoadDataset()! with { Detachments = store.LoadDataset()!.Detachments!.Where(d => d.Faction != faction).Concat(incoming.Detachments!).ToList() });
                    var preview = workflow.Prepare(incoming, ImportLayer.Profiles);
                    Check(preview.CanApply && preview.ReferenceChanges.All(c => c.Status != "Unresolved"), "Real BSData references failed matching");
                    workflow.Apply(preview);
                    Check(incoming.Detachments!.Count > 1 && incoming.ReferenceCards!.Count > 0, "Real multi-detachment source absent");
                    records.Add(new { Faction = faction, SourceType = "BSData", Cards = incoming.ReferenceCards!.Count, Detachments = incoming.Detachments.Count });
                }
                var supplementIndex = Array.IndexOf(args, "--stratagem-pack");
                using var supplement = new PickedDocument("reference-pack.json", File.OpenRead(args[supplementIndex + 1]));
                var dg = workflow.PrepareReferenceImportAsync(supplement).GetAwaiter().GetResult();
                Check(dg.CanApply && dg.ReferenceChanges.All(c => c.Status != "Unresolved"), "Supplement failed generic matching");
                workflow.Apply(dg);
                var saved = store.LoadDataset()!;
                var dgArmy = ArmyList.New("Death Guard") with { Detachment = "Virulent Vectorium" };
                Check(ArmyReferences.ForArmy(dgArmy, saved).Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)) == 6, "Real supplemental cards missing");
                Check(DataJson.Write(saved.Units) == initialUnits, "Reference imports changed real army-building data");
                records.Add(new { Faction = "Death Guard", SourceType = "StructuredJson", Cards = 45, Detachments = 9 });
                File.WriteAllText(Path.Combine(root, "generic-reference-factions.json"), DataJson.Write(records));
                var exported = DataJson.Write(saved); var imported = DataJson.Read<Dataset>(exported);
                Check(DataJson.Write(imported) == exported, "Portable dataset changed in reverse round-trip");
            });
        test("Indexed reference filtering handles global, faction, multiple detachments, edition and obsolete scope", () =>
        {
            var global = Card("Global", id: "global") with { Faction = "Core", Detachment = "", Details = Card(id: "global").Details! with { Scope = "Global", Detachments = [] } };
            var faction = Card("Faction", id: "faction") with { Detachment = "", Details = Card(id: "faction").Details! with { Scope = "Faction", Detachments = [] } };
            var multi = Card("Multi", id: "multi") with { Details = Card(id: "multi").Details! with { Detachments = [new("Alpha", "alpha-id"), new("Beta", "beta-id")] } };
            var data = With(Library(), ReferenceMerge.Prepare(Library(), Pack(Card(), global, faction, multi)));
            data = data with { ReferenceCards = [.. data.ReferenceCards!, Card("Old edition", id: "old") with { Details = Card(id: "old").Details! with { Edition = "10" } },
                Card("Obsolete", id: "obsolete") with { Details = Card(id: "obsolete").Details! with { Status = "Obsolete" } }] };
            var index = DatasetSnapshot.For(data).References;
            var alpha = index.ForArmy(ArmyList.New("Faction A") with { Detachment = "Alpha" }, "11");
            var beta = index.ForArmy(ArmyList.New("Faction A") with { Detachment = "Beta" }, "11");
            Check(alpha.Count == 4 && beta.Count == 3 && beta.All(c => c.Name != "Tactic"), "Wrong scope filtered");
            Check(index.ForArmy(ArmyList.New("Faction B"), "11").Single().Name == "Global", "Foreign faction leaked");
            Check(ReferenceEquals(index, DatasetSnapshot.For(data).References), "Query rebuilt indexes");
            var report = ReferenceCoverageAudit.Build(data);
            Check(report.Factions.Single().Detachments.All(d => d.Coverage == "Partial"), "Card count falsely proved completeness");
        });
        test("Reference workflow previews atomically, blocks conflicts, preserves catalogue provenance and round-trips SQLite coverage", () =>
        {
            var path = Path.Combine(root, "generic-reference-workflow");
            using (var store = WindowsServices.OpenStore(path))
            {
                store.Import(Library()); var workflow = new LibraryWorkflow(store);
                var before = DataJson.Write(store.LoadDataset());
                var pack = Pack(Card()) with { Source = Source with { Name = "Supplement", Version = "7" },
                    ReferenceCoverage = [new("fixture", "11", Faction: "Faction A", Detachment: "Alpha", State: "Complete", Mode: "CompleteForDetachment", Authoritative: true)] };
                using var stream = new PickedDocument("references.json", new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(pack))));
                var preview = workflow.PrepareReferenceImportAsync(stream).GetAwaiter().GetResult();
                Check(before == DataJson.Write(store.LoadDataset()), "Preview mutated storage");
                workflow.ApplyReferenceImportAsync(preview).GetAwaiter().GetResult();
                Check(store.LoadDataset()!.Source.Name == Source.Name, "Reference import replaced catalogue provenance");
                var saved = DataJson.Write(store.LoadDataset());
                var conflict = workflow.Prepare(Pack(Card(owner: "other", id: "other") with { Cost = "2 CP" }), ImportLayer.Profiles);
                Check(!conflict.CanApply, "Conflict allowed apply"); Reject(() => workflow.Apply(conflict));
                Check(saved == DataJson.Write(store.LoadDataset()), "Conflict corrupted storage");
                using var cancelled = new PickedDocument("cancel.json", new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(pack))));
                try { workflow.PrepareReferenceImportAsync(cancelled, new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
                Check(saved == DataJson.Write(store.LoadDataset()), "Cancellation applied changes");
            }
            using var reopened = WindowsServices.OpenStore(path);
            var data = reopened.LoadDataset()!;
            Check(data.ReferenceCoverage!.Single().State == "Complete" && data.ReferenceCards!.Single().Details!.SourceId == "fixture", "SQLite lost provenance/coverage");
            var roundTrip = DataJson.Read<Dataset>(DataJson.Write(data));
            Check(DataJson.Write(roundTrip) == DataJson.Write(data), "Portable dataset lost fields");
        });
        test("BSData stratagem profiles follow hierarchy and shared links for multiple detachments without faction-specific rules", () =>
        {
            var text = """
            {"catalogue":{"id":"faction-a","name":"Faction A","revision":"1","sharedProfiles":[
             {"id":"tactic","name":"Example tactic","typeName":"Stratagem","characteristics":[
              {"name":"CP","$text":"1 CP"},{"name":"WHEN","$text":"Your turn."},{"name":"TARGET","$text":"A unit."},{"name":"EFFECT","$text":"Fixture."}]}],
             "selectionEntryGroups":[{"id":"group","name":"Detachment","selectionEntries":[
              {"id":"alpha-id","type":"upgrade","name":"Alpha","infoLinks":[{"id":"a-link","type":"profile","targetId":"tactic"}]},
              {"id":"beta-id","type":"upgrade","name":"Beta","infoLinks":[{"id":"b-link","type":"profile","targetId":"tactic"}]}]}]}}
            """;
            var data = new BsDataReferenceAdapter().Read("catalogue.json", text, "11");
            var card = data.ReferenceCards!.Single(c => c.Section == "Stratagems");
            Check(card.Details!.Detachments!.Count == 2 && card.Details.SourceCardId == "tactic" && card.Details.Effect == "Fixture.", "Linked scope/text lost");
            Check(ReferenceMerge.Prepare(Library(), data).Cards.Single(c => c.Section == "Stratagems").Details!.Match == "ExactId", "Native IDs failed to match");
        });
        test("Canonical references round-trip structured text, identities, scope and provenance; legacy JSON remains readable", () =>
        {
            var card = ReferenceModel.Canonical(Card(), Source);
            var read = DataJson.Read<Dataset>(DataJson.Write(Pack(card)));
            Check(DataJson.Write(card) == DataJson.Write(read.ReferenceCards!.Single()), "Canonical metadata lost");
            Check(ReferenceModel.Description(card).Contains("EFFECT:"), "Structured field omitted");
            var legacy = DataJson.Read<Dataset>("""{"Source":{"Name":"Legacy","Version":"1","Date":"","Url":""},"Units":[]} """);
            Check(legacy.ReferenceCards == null && legacy.SchemaVersion == 1, "Legacy JSON rejected");
        });
        test("Reference semantic hashes ignore formatting but detect text, price, scope and timing changes", () =>
        {
            var card = ReferenceModel.Canonical(Card(), Source);
            Check(ReferenceModel.ContentHash(card) == ReferenceModel.ContentHash(card with { Text = "  Fixture   reference\ntext. " }), "Whitespace produces false update");
            foreach (var changed in new[] { card with { Cost = "2 CP" }, card with { Name = "Other" }, card with { Details = card.Details! with { Timing = "Now" } },
                card with { Details = card.Details! with { Detachments = [new("Beta", "beta-id")] } } })
                Check(ReferenceModel.ContentHash(card) != ReferenceModel.ContentHash(changed), "Semantic change ignored");
        });
        test("Reference merge classifies additions, unchanged records and renamed stable-ID updates", () =>
        {
            var data = Library(); var added = ReferenceMerge.Prepare(data, Pack(Card()));
            Check(added.Changes.Single().Status == "Added", "Not added");
            data = With(data, added);
            Check(ReferenceMerge.Prepare(data, Pack(Card())).Changes.Single().Status == "Unchanged", "Not unchanged");
            var updated = ReferenceMerge.Prepare(data, Pack(Card("Renamed") with { Cost = "2 CP" }));
            Check(updated.Cards.Count == 1 && updated.Changes.Single().Status == "Updated", "Rename became a duplicate");
        });
        test("Different reference owners cannot silently overwrite; explicit preferred source resolves conflict", () =>
        {
            var data = With(Library(), ReferenceMerge.Prepare(Library(), Pack(Card())));
            var other = Card(owner: "other", id: "other-id") with { Text = "Different effect." };
            var conflict = ReferenceMerge.Prepare(data, Pack(other));
            Check(conflict.HasConflicts && conflict.Cards.Single().Details!.SourceId == "fixture", "Conflict silently replaced data");
            Check(conflict.Changes.Any(c => c.DifferingFields?.Contains("Text") == true), "Conflict fields omitted");
            var key = ReferenceModel.LogicalKey(ReferenceModel.Canonical(other, Source));
            var resolved = ReferenceMerge.Prepare(data, Pack(other), new Dictionary<string, string> { [key] = "other" });
            Check(!resolved.HasConflicts && resolved.Cards.Single(c => c.Details!.Status == "Active").Details!.SourceId == "other", "Explicit resolution failed");
        });
        test("Partial reference updates retain absent records; complete declarations remove only owned scope", () =>
        {
            var data = Library(); data = With(data, ReferenceMerge.Prepare(data, Pack(Card(), Card("Other", id: "other"), Card("Supplement", owner: "supplement", id: "supplement"))));
            var partial = ReferenceMerge.Prepare(data, Pack(Card()));
            Check(partial.Cards.Count(c => c.Details!.Status == "Active") == 3, "Partial import removed data");
            var complete = Pack(Card()) with { ReferenceCoverage = [new("fixture", "11", Faction: "Faction A", Detachment: "Alpha", State: "Complete", Mode: "CompleteForDetachment", Authoritative: true)] };
            var result = ReferenceMerge.Prepare(data, complete);
            Check(result.Cards.Single(c => c.Details!.Id == "other").Details!.Status == "Removed", "Complete owner did not remove missing card");
            Check(result.Cards.Single(c => c.Details!.SourceId == "supplement").Details!.Status == "Active", "Complete source deleted another owner's card");
        });
        test("Explicit reference replacements retain obsolete history without cross-owner removals", () =>
        {
            var data = With(Library(), ReferenceMerge.Prepare(Library(), Pack(Card())));
            var next = Card("New", id: "new") with { Details = Card(id: "new").Details! with { Replaces = "tactic" } };
            var result = ReferenceMerge.Prepare(data, Pack(next));
            Check(result.Cards.Single(c => c.Details!.Id == "tactic").Details!.Status == "Obsolete", "Replacement failed");
            Reject(() => ReferenceMerge.Prepare(data, Pack(next with { Details = next.Details! with { Edition = "10" } })));
        });
        test("Reference matching uses exact IDs, audits name fallbacks and retains ambiguous or unresolved cards hidden", () =>
        {
            var exact = ReferenceMerge.Prepare(Library(), Pack(Card())).Cards.Single();
            Check(exact.Details!.Match == "ExactId", "Stable ID not used");
            var fallback = Card() with { Details = Card().Details! with { Detachments = [new(" a-lpha ")] } };
            Check(ReferenceMerge.Prepare(Library(), Pack(fallback)).Cards.Single().Details!.Match == "NameFallback", "Normalized fallback failed");
            var ambiguous = Library() with { Detachments = [new("Faction A", "Alpha"), new("Faction A", "Al-pha")] };
            var result = ReferenceMerge.Prepare(ambiguous, Pack(fallback));
            Check(result.Changes.Single().Status == "Unresolved" && result.Cards.Single().Details!.Match == "Ambiguous", "Ambiguous scope guessed");
            var missing = Card() with { Details = Card().Details! with { Detachments = [new("Alpha", "missing-id")] } };
            Check(ReferenceMerge.Prepare(Library(), Pack(missing)).Cards.Single().Details!.Status == "Unresolved", "Bad ID silently fell back to name");
            var canonicalId = ReferenceMatching.CanonicalDetachmentId("11", "Faction A", "Alpha");
            var canonical = Card() with { Details = Card().Details! with { Detachments = [new("Alpha", canonicalId)] } };
            var legacy = Library() with { Detachments = [new("Faction A", "Alpha")] };
            var matched = ReferenceMerge.Prepare(legacy, Pack(canonical)).Cards.Single();
            Check(matched.Details!.Match == "ExactCanonicalMatch" && matched.Details.Detachments!.Single().Id == canonicalId, "Canonical detachment ID was lost");
        });
        test("Structured reference adapter accepts portable streams and rejects malformed, empty and wrong-edition sources", () =>
        {
            using var input = new PickedDocument("pack.json", new MemoryStream(Encoding.UTF8.GetBytes(DataJson.Write(Pack(Card())))));
            var parsed = ReferenceSourceReader.ReadAsync(input, "11").GetAwaiter().GetResult();
            Check(parsed.ReferenceCards!.Single().Details!.SourceId == "fixture", "Adapter discarded identity");
            var adapter = new StructuredJsonReferenceAdapter();
            Reject(() => adapter.Read("x.json", DataJson.Write(Pack()), "11"));
            Reject(() => adapter.Read("x.json", DataJson.Write(Pack(Card())), "10"));
            Reject(() => adapter.Read("x.json", DataJson.Write(Pack(Card() with { Section = "Enhancements" })), "11"));
            Reject(() => adapter.Read("x.json", DataJson.Write(Pack(Card() with { Section = "enhancements" })), "11"));
            Reject(() => adapter.Read("x.json", DataJson.Write(Pack(Card() with { Section = "Other", IsUpgrade = true })), "11"));
        });
    }
}
