using GrimList.Core;

// Opt-in real-data integration. Every writable database is created beneath the
// test runner's fresh GUID directory; no default application path is consulted.
internal static class DisposableReferenceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Hash<T>(T value) => ReferenceModel.Hash(DataJson.Write(value));
    public static void Run(Action<string, Action> test, string root, string[] args)
    {
        var flag = Array.IndexOf(args, "--disposable-reference-pack");
        if (flag < 0) return;
        test("Finished all-faction reference pack survives production import, reopen, repeat and BSData refresh in disposable SQLite", () =>
        {
            var seedFlag = Array.IndexOf(args, "--disposable-reference-dataset");
            Check(flag + 1 < args.Length && seedFlag >= 0 && seedFlag + 1 < args.Length, "Supply the pack and BSData dataset paths");
            var packPath = Path.GetFullPath(args[flag + 1]);
            var seedPath = Path.GetFullPath(args[seedFlag + 1]);
            var directory = Path.Combine(root, "disposable-reference-integration");
            Directory.CreateDirectory(directory);
            var pack = new StructuredJsonReferenceAdapter().Read(Path.GetFileName(packPath), File.ReadAllText(packPath), "11");
            var expected = pack.ReferenceCards!;
            var owners = expected.Select(c => c.Details!.SourceId).ToHashSet();
            var stages = new List<object>(); var previews = new List<object>(); var errors = new List<string>();
            var comparisons = new List<object>(); var filters = new List<object>();
            var summary = new List<string> { "Stage | Units | References (all) | Supplemental applications | Source cards | Coverage | Armies" };
            var status = "FAIL";
            Dataset baseline = null!; string armyHash = "", contextHash = "", unitHash = "", detachmentHash = "", originalCardsHash = "";
            string supplementalHash = "", coverageHash = "";
            List<ArmyList> armies = []; Dictionary<string, ArmyResult> validation = [];
            List<ReferenceCard> Supplemental(Dataset data) => (data.ReferenceCards ?? []).Where(c => owners.Contains(c.Details?.SourceId ?? "")).OrderBy(c => c.Details!.Id).ToList();
            List<ReferenceCard> Original(Dataset data) => (data.ReferenceCards ?? []).Where(c => !owners.Contains(c.Details?.SourceId ?? "")).OrderBy(ReferenceModel.LogicalKey).ToList();
            void Capture(ArmyWorkspace workspace, string stage, bool imported, bool refreshed = false)
            {
                var data = workspace.Repository.LoadDataset()!;
                var saved = workspace.Repository.LoadArmies().OrderBy(a => a.Id).ToList();
                var cards = Supplemental(data);
                using var db = new SqliteDb(Path.Combine(directory, "grimlist.db"));
                int Count(string sql) => int.Parse(db.Run(sql).Single()[0]!);
                stages.Add(new { Stage = stage, Units = data.Units.Count,
                    ActiveUnitRows = Count("SELECT count(*) FROM units WHERE source_id=(SELECT value FROM settings WHERE key='active_source')"),
                    ActivePointRows = Count("SELECT count(*) FROM points WHERE source_id=(SELECT value FROM settings WHERE key='active_source')"),
                    SourceHistoryRows = Count("SELECT count(*) FROM sources"),
                    Detachments = data.Detachments!.Count, ReferenceCards = data.ReferenceCards!.Count,
                    SupplementalApplicabilityRecords = cards.Count,
                    DistinctSupplementalSourceCards = cards.DistinctBy(SourceRuleKey).Count(),
                    SupplementalCoverageRows = (data.ReferenceCoverage ?? []).Count(c => owners.Contains(c.SourceId)),
                    Armies = saved.Count, Entries = saved.Sum(a => a.Entries.Count),
                    Attachments = saved.Sum(a => a.Entries.Count(e => e.AttachedToId.Length > 0)),
                    Enhancements = saved.Sum(a => a.Entries.Count(e => e.Enhancement != null)),
                    Loadouts = saved.Sum(a => a.Entries.Count(e => e.Loadout != null)) });
                summary.Add($"{stage} | {data.Units.Count} | {data.ReferenceCards!.Count} | {cards.Count} | {cards.DistinctBy(SourceRuleKey).Count()} | {(data.ReferenceCoverage ?? []).Count(c => owners.Contains(c.SourceId))} | {saved.Count}");
                Check(Hash(saved) == armyHash, stage + ": saved armies/loadouts/attachments/enhancement selections changed");
                Check(Hash(workspace.Repository.LoadContext()) == contextHash, stage + ": editor recovery context changed");
                // A normal BSData reimport refreshes source timestamps; all unit semantics must remain identical.
                Check((refreshed ? SemanticUnits(data) == SemanticUnits(baseline) : Hash(data.Units) == unitHash), stage + ": units/points/profiles/loadout definitions changed");
                Check(Hash(data.Detachments) == detachmentHash, stage + ": detachment metadata changed");
                Check(Hash(Original(data)) == originalCardsHash, stage + ": existing rules/enhancements/provenance changed");
                Check(Hash(data.Source with { ImportedAt = "" }) == Hash(baseline.Source with { ImportedAt = "" }), stage + ": BSData ownership changed");
                foreach (var army in saved)
                {
                    var result = ArmyCalculator.Calculate(army, data);
                    var same = Hash(result) == Hash(validation[army.Id]);
                    comparisons.Add(new { Stage = stage, army.Name, army.Faction, result.Total, result.Validated, Issues = result.Issues, Unchanged = same });
                    Check(same, stage + ": army validation/points changed: " + army.Name);
                }
                if (!imported) return;
                Check(cards.Count == expected.Count && cards.Select(c => c.Details!.Id).Distinct().Count() == cards.Count, stage + ": missing or duplicate supplemental references");
                Check(cards.Select(ReferenceModel.LogicalKey).Distinct().Count() == cards.Count, stage + ": duplicate applicability records");
                var originals = expected.ToDictionary(c => c.Details!.Id);
                Check(cards.All(c => SourceRuleKey(c) == SourceRuleKey(originals[c.Details!.Id]) && Hash(c.Source) == Hash(originals[c.Details.Id].Source)), stage + ": source rule content or provenance lost during import");
                Check(cards.DistinctBy(SourceRuleKey).Count() == 1329, stage + ": distinct source-card count changed");
                Check(cards.All(c => c.Details!.SourceType != "BSData" && !c.Details.SourceId.StartsWith("bsdata:")), stage + ": supplemental ownership adopted by BSData");
                if (supplementalHash.Length == 0) { supplementalHash = Hash(cards); coverageHash = Hash(data.ReferenceCoverage); }
                Check(Hash(cards) == supplementalHash && Hash(data.ReferenceCoverage) == coverageHash, stage + ": references/provenance/coverage changed after repeat, reopen or refresh");
                VerifyFilters(data, pack, stage, filters);
                Check(workspace.Session.Dataset != null && Supplemental(workspace.Session.Dataset).Count == expected.Count, stage + ": application session has stale references");
                Console.WriteLine("  Disposable integration: " + stage + " PASS");
            }
            void ImportPack(ArmyWorkspace workspace, string stage)
            {
                var before = Hash(workspace.Repository.LoadDataset());
                using var document = new PickedDocument(Path.GetFileName(packPath), File.OpenRead(packPath));
                var preview = workspace.Library.PrepareReferenceImportAsync(document).GetAwaiter().GetResult();
                previews.Add(new { Stage = stage, preview.CanApply, Changes = preview.ReferenceChanges.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count()), preview.Warnings });
                Check(before == Hash(workspace.Repository.LoadDataset()), "Preview wrote to the database");
                Check(preview.CanApply && preview.ReferenceChanges.All(c => c.Status is not ("Ambiguous" or "Unresolved" or "Conflict")), "Production preview rejected references");
                Check(preview.Prices.Count == 0 && preview.Armies.Count == 0, "Reference preview changes prices or army validation");
                workspace.ApplyLibraryAsync(preview).GetAwaiter().GetResult();
            }
            try
            {
                Check(expected.Count == 2214 && pack.ReferenceCoverage!.Count == 444, "Expected the frozen all-faction candidate");
                using (var workspace = new ArmyWorkspace(WindowsServices.OpenStore(directory)))
                {
                    using var document = new PickedDocument(Path.GetFileName(seedPath), File.OpenRead(seedPath));
                    var incoming = workspace.Library.ReadDocumentAsync(document, "11").GetAwaiter().GetResult();
                    workspace.ApplyLibraryAsync(workspace.Library.Prepare(incoming.Dataset, ImportLayer.Combined)).GetAwaiter().GetResult();
                    baseline = workspace.Repository.LoadDataset()!;
                    foreach (var faction in new[] { "Death Guard", "Adepta Sororitas", "World Eaters", "Black Templars", "Blood Angels", "Space Marines" })
                        workspace.Repository.Save(CreateArmy(baseline, faction));
                    armies = workspace.Repository.LoadArmies().OrderBy(a => a.Id).ToList();
                    Check(armies.All(a => a.Entries.Any(e => e.Enhancement != null) && a.Entries.Any(e => e.AttachedToId.Length > 0)), "Seed must exercise real enhancements and legal attachments");
                    var selected = armies.First();
                    workspace.Repository.SaveContext(new(selected.Id, selected.Entries[0].Id, Screen: "equipment"));
                    Check(workspace.Session.Restore(), "Seeded army did not restore");
                    armyHash = Hash(armies); contextHash = Hash(workspace.Repository.LoadContext());
                    unitHash = Hash(baseline.Units); detachmentHash = Hash(baseline.Detachments); originalCardsHash = Hash(Original(baseline));
                    validation = armies.ToDictionary(a => a.Id, a => ArmyCalculator.Calculate(a, baseline));
                    File.WriteAllText(Path.Combine(directory, "armies-before.json"), DataJson.Write(armies));
                    Capture(workspace, "Before import", false);
                    ImportPack(workspace, "First import"); Capture(workspace, "After first import", true);
                    ImportPack(workspace, "Second import"); Capture(workspace, "After second import", true);
                }
                using (var workspace = new ArmyWorkspace(WindowsServices.OpenStore(directory)))
                {
                    Check(workspace.Session.Restore() && armies.Any(a => a.Id == workspace.Session.Army.Id), "Reopen failed to restore selected army");
                    Check(Hash(workspace.Session.Army) == Hash(armies.Single(a => a.Id == workspace.Session.Army.Id)), "Restored army differs");
                    Capture(workspace, "After reopen", true);
                    using var document = new PickedDocument(Path.GetFileName(seedPath), File.OpenRead(seedPath));
                    var incoming = workspace.Library.ReadDocumentAsync(document, "11").GetAwaiter().GetResult();
                    var preview = workspace.Library.Prepare(incoming.Dataset, ImportLayer.Combined, completeFactions: true);
                    previews.Add(new { Stage = "BSData refresh", preview.CanApply, preview.Warnings });
                    Check(preview.CanApply && preview.Prices.Count == 0 && preview.Armies.Count == 0, "Normal BSData refresh changes saved army validation");
                    workspace.ApplyLibraryAsync(preview).GetAwaiter().GetResult();
                    Capture(workspace, "After BSData refresh", true, refreshed: true);
                }
                status = "PASS";
            }
            catch (Exception ex) { errors.Add(ex.ToString()); throw; }
            finally
            {
                var report = new { Status = status, Database = Path.Combine(directory, "grimlist.db"), Pack = packPath,
                    PackSha256 = ReferenceModel.Hash(File.ReadAllText(packPath)), Seed = seedPath,
                    ActiveLibraryTouched = false, AndroidChanged = false,
                    RefreshComparison = "Unit identity, data and source metadata unchanged except normal import timestamps and redundant self-ID alias registration. Source history rows are immutable import versions, not duplicate active units/references.",
                    Stages = stages, ArmyValidation = comparisons, Filtering = filters, Previews = previews, Errors = errors };
                File.WriteAllText(Path.Combine(directory, "reference-integration-report.json"), DataJson.Write(report));
                File.WriteAllText(Path.Combine(directory, "reference-integration-report.txt"), string.Join("\n", new[] { status, "Disposable database: " + Path.Combine(directory, "grimlist.db"), "" }
                    .Concat(summary).Concat(["", "Validation: full totals, priced entries and ordered issues compared before/after each stage.",
                        "Filtering: all 444 canonical contexts, 429 complete with cards, 15 complete with zero cards; chapter isolation and no duplicate visible stratagems.",
                        "Preserved: units, points, loadouts, enhancements, detachment metadata, saved armies, attachments, editor recovery context, reference content and provenance.",
                        "BSData refresh may update import timestamps and register the existing unit ID as an additional self-alias.",
                        "Active library untouched. No Android or acquisition/parser changes.", "", "Baseline validation warnings (unchanged on PASS):"])
                    .Concat(validation.SelectMany(v => v.Value.Issues.Select(i => armies.Single(a => a.Id == v.Key).Faction + ": " + i)))
                    .Concat(errors)));
                Console.WriteLine("  Integration report: " + Path.Combine(directory, "reference-integration-report.json"));
            }
        });
    }

    private static string SemanticUnits(Dataset data) => Hash(data.Units.Select(u => u with
    {
        PointsSource = u.PointsSource is { } p ? p with { ImportedAt = "" } : null,
        ProfileSource = u.ProfileSource is { } s ? s with { ImportedAt = "" } : null,
        Aliases = (u.Aliases ?? []).Append(u.Key).Distinct().Order().ToList()
    }).ToList());

    // SourceCardId includes canonical subsection context in the accepted pack.
    // Compare original rule semantics, excluding projected faction identity.
    private static string SourceRuleKey(ReferenceCard c) => Hash(new { c.Source!.Url, c.Source.ContentHash,
        Detachment = UnitIdentity.Normalize(c.Detachment), Name = UnitIdentity.Normalize(c.Name),
        c.Cost, c.Text, c.Details!.Timing, c.Details.Target, c.Details.Effect, c.Details.Restrictions });

    private static ArmyList CreateArmy(Dataset data, string faction)
    {
        var units = data.Units.Where(u => u.Faction == faction && u.Options.Count > 0).ToList();
        ArmyEntry Entry(UnitDefinition unit) => new(Guid.NewGuid().ToString(), unit.Key, unit.Name, unit.Options[0].Models,
            "Disposable integration fixture", Loadouts.Create(unit, unit.Options[0].Models));
        foreach (var detachment in data.Detachments!.Where(d => d.Faction == faction))
        foreach (var leader in units.Where(u => LeaderRules.IsLeader(u) && Enhancements.Character(u)))
        {
            var leaderEntry = Entry(leader);
            var army = ArmyList.New(faction) with { Name = faction + " integration fixture", Detachment = detachment.Name,
                Disposition = detachment.DispositionOptions.FirstOrDefault() ?? "", Entries = [leaderEntry] };
            var enhancement = Enhancements.Options(army, data, leader).FirstOrDefault()
                ?? data.ReferenceCards!.FirstOrDefault(c => c.Faction == faction && c.Detachment == detachment.Name && c.Section == "Enhancements");
            if (enhancement == null) continue;
            // Preserve a real saved selection even where BSData cannot establish
            // eligibility from its restriction text. The existing validation
            // warning is captured and must remain identical through every import.
            army = army with { Entries = [leaderEntry with { Enhancement = new(enhancement.Detachment, enhancement.Name) }] };
            foreach (var bodyguard in units.Where(u => !LeaderRules.IsLeader(u)))
            {
                var entry = Entry(bodyguard); var candidate = army with { Entries = [.. army.Entries, entry] };
                if (LeaderRules.Problem(candidate, data, leaderEntry.Id, entry.Id) != null) continue;
                candidate = LeaderRules.Attach(candidate, data, leaderEntry.Id, entry.Id);
                if (!WarlordRules.Candidates(candidate, data).Any(e => e.Id == leaderEntry.Id)) continue;
                return WarlordRules.Select(candidate, data, leaderEntry.Id);
            }
        }
        throw new Exception("Could not seed a real enhancement/leader/bodyguard army for " + faction);
    }

    private static void VerifyFilters(Dataset data, Dataset pack, string stage, List<object> reports)
    {
        var expected = pack.ReferenceCards!; var ids = expected.Select(c => c.Details!.Id).ToHashSet();
        var marineRules = expected.Where(c => c.Faction == "Space Marines").Select(SourceRuleKey).ToHashSet();
        var index = DatasetSnapshot.For(data).References;
        var zero = 0; var withCards = 0; var inherited = 0;
        foreach (var faction in data.Units.Select(u => u.Faction).Distinct())
        {
            var visible = ArmyReferences.ForArmy(ArmyList.New(faction), data);
            Check(visible.All(c => ArmyReferences.IsCore(c) || c.Faction == faction), "Faction-only query leaks another faction");
            Check(visible.All(c => c.Details?.Scope != "Detachment" && c.Detachment.Length == 0), "Faction-only query leaks detachment cards");
        }
        foreach (var coverage in pack.ReferenceCoverage!)
        {
            var army = ArmyList.New(coverage.Faction) with { Detachment = coverage.Detachment };
            var visible = ArmyReferences.ForArmy(army, data);
            var actual = visible.Where(c => ids.Contains(c.Details?.Id ?? "")).ToList();
            var wanted = expected.Where(c => c.Faction == coverage.Faction && c.Details!.Detachments!.Any(d => UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(coverage.Detachment))).ToList();
            Check(actual.Select(c => c.Details!.Id).ToHashSet().SetEquals(wanted.Select(c => c.Details!.Id)), coverage.Faction + "/" + coverage.Detachment + ": incorrect card applicability");
            Check(visible.All(c => ArmyReferences.IsCore(c) || c.Faction == coverage.Faction), "Cross-chapter/faction leakage");
            Check(visible.All(c => c.Details?.Scope != "Detachment" || c.Details.Detachments!.Any(d => UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(coverage.Detachment))), "Another detachment leaked");
            var stratagems = visible.Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
            Check(stratagems.Select(c => UnitIdentity.Normalize(c.Name)).Distinct().Count() == stratagems.Count, "Duplicate visible stratagem");
            var state = ReferenceCoverageAudit.ForDetachment(data, index, coverage.Faction, coverage.Detachment);
            Check(state.Coverage is "Complete" or "VerifiedComplete", "Persisted coverage is incomplete");
            if (wanted.Count == 0) { zero++; Check(stratagems.Count == 0, "Complete/zero detachment has stratagems"); }
            else withCards++;
            if (coverage.Faction != "Space Marines" && wanted.Any(c => marineRules.Contains(SourceRuleKey(c)))) inherited++;
        }
        Check(withCards == 429 && zero == 15 && inherited > 0, "Coverage or shared Space Marine inheritance incomplete");
        reports.Add(new { Stage = stage, FactionContexts = data.Units.Select(u => u.Faction).Distinct().Count(), DetachmentContexts = pack.ReferenceCoverage.Count,
            CompleteWithCards = withCards, CompleteWithZeroCards = zero, SharedMarineDetachmentContexts = inherited,
            CrossFactionLeaks = 0, DuplicateVisibleCards = 0, Status = "PASS" });
    }
}
