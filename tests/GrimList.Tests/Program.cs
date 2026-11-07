using System.Text;
using GrimList.Core;

if (args is ["--audit-reference-recovery"])
{
    Console.InputEncoding = Encoding.UTF8;
    var before = DataJson.Read<Dataset>(Console.In.ReadToEnd());
    var after = GrimList.Application.ReferenceLibraryRecovery.Supplement(before, GrimList.Application.ReferenceLibraryRecovery.ReadBundled());
    Console.WriteLine(DataJson.Write(new { Changed = DataJson.Write(before) != DataJson.Write(after),
        Idempotent = ReferenceEquals(after, GrimList.Application.ReferenceLibraryRecovery.Supplement(after, GrimList.Application.ReferenceLibraryRecovery.ReadBundled())),
        Factions = (after.ReferenceCards ?? []).Where(c => c.Section == "Stratagems").GroupBy(c => c.Faction).Select(g => new { Faction = g.Key,
            Before = (before.ReferenceCards ?? []).Where(c => c.Section == "Stratagems" && c.Faction == g.Key).GroupBy(c => c.Details?.Status ?? "Legacy").ToDictionary(s => s.Key, s => s.Count()),
            After = g.GroupBy(c => c.Details?.Status ?? "Legacy").ToDictionary(s => s.Key, s => s.Count()) }).ToList() }));
    return 0;
}

if (args is ["--build-reference-baselines", var baselineCatalogue, var baselineOutput])
{
    var data = DataJson.Read<Dataset>(File.ReadAllText(baselineCatalogue));
    var baselines = GrimList.Application.ReferenceCatalogueBaseline.Create(data, GrimList.Application.ReferenceLibraryRecovery.ReadBundled());
    if (baselines.Count == 0) throw new InvalidDataException("No matching source-pinned reference baselines.");
    using var baselineFile = File.Create(baselineOutput);
    using var gzip = new System.IO.Compression.GZipStream(baselineFile, System.IO.Compression.CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(gzip); writer.Write(DataJson.Write(baselines));
    Console.WriteLine($"Generated {baselines.Count} source-pinned detachment reference baselines."); return 0;
}

// Exercise Android's JSON newline convention on Windows as a compatibility regression.
if (args.Contains("--lf-json")) DataJson.Options.NewLine = "\n";

if (args is ["--audit-mfm", var mfmDirectory, var cataloguePath, var reportPath])
{
    var auditSource = new SourceInfo("Munitorum Field Manual · BSData mirror", "retrieved-fixtures", "", "https://github.com/BSData/wh40k-11e-mfm/blob/main", Edition: "11");
    var audits = Directory.GetFiles(mfmDirectory, "*.yaml").Where(p => Path.GetFileName(p) != "meta.yaml")
        .Select(p => GrimList.Application.MfmMirrorParser.Parse(File.ReadAllText(p), auditSource)).ToList();
    var prices = new Dataset(auditSource, audits.SelectMany(a => a.Dataset.Units).ToList());
    var catalogue = DataJson.Read<Dataset>(File.ReadAllText(cataloguePath));
    var overlay = MfmPoints.Overlay(catalogue, prices);
    File.WriteAllText(reportPath, DataJson.Write(new { Summary = MfmPoints.Summary(overlay.Dataset),
        Factions = audits.Count, Changes = DatasetChanges.Compare(catalogue, overlay.Dataset),
        Warnings = audits.SelectMany(a => a.Warnings).Concat(overlay.Warnings).ToList() }));
    Console.WriteLine($"Parsed {audits.Count} MFM factions. {MfmPoints.Summary(overlay.Dataset)}");
    return 0;
}

if (args is ["--build-enhancement-metadata", var enhancementArchive, var enhancementOutput])
{
    var bytes = File.ReadAllBytes(enhancementArchive);
    var sourceInfo = new SourceInfo("BSData semantic recovery", "1", "", "", Edition: "11",
        ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    var cards = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(bytes), sourceInfo);
    List<GrimList.Application.EnhancementLibraryRepair.RecoveryPack> packs = [];
    if (File.Exists(enhancementOutput))
    {
        using var input = File.OpenRead(enhancementOutput);
        using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var json = reader.ReadToEnd();
        packs = json.TrimStart().StartsWith('[')
            ? DataJson.Read<List<GrimList.Application.EnhancementLibraryRepair.RecoveryPack>>(json)
            : [DataJson.Read<GrimList.Application.EnhancementLibraryRepair.RecoveryPack>(json)];
    }
    packs.RemoveAll(p => p.Source.Edition == sourceInfo.Edition && p.Source.ContentHash.Equals(sourceInfo.ContentHash, StringComparison.OrdinalIgnoreCase));
    packs.Add(GrimList.Application.EnhancementLibraryRepair.Create(sourceInfo, cards));
    using var file = File.Create(enhancementOutput);
    using var compressed = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(compressed); writer.Write(DataJson.Write(packs));
    Console.WriteLine($"Generated {cards.Count(c => c.IsUpgrade)} upgrade classifications; retained {packs.Count} source revisions.");
    return 0;
}

if (args is ["--build-effective-metadata", var archive, var output])
{
    var bytes = File.ReadAllBytes(archive);
    var documents = BsDataDocument.ReadArchive(bytes);
    var metadataSource = new SourceInfo("BSData semantic recovery", "1", "", "", Edition: "11", ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    var imported = BsDataImporter.ImportAll(documents, metadataSource, new Progress<string>(Console.WriteLine)).Dataset;
    List<GrimList.Application.EffectiveDatasheetRecovery.Pack> packs = [];
    if (File.Exists(output))
    {
        using var input = File.OpenRead(output); using var zip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(zip); packs = DataJson.Read<List<GrimList.Application.EffectiveDatasheetRecovery.Pack>>(reader.ReadToEnd());
    }
    var generated = GrimList.Application.EffectiveDatasheetRecovery.Refresh(GrimList.Application.EffectiveDatasheetRecovery.Create(imported), packs);
    packs.RemoveAll(p => p.SourceHash.Equals(metadataSource.ContentHash, StringComparison.OrdinalIgnoreCase));
    packs.Add(generated);
    using var file = File.Create(output); using var compressed = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(compressed); writer.Write(DataJson.Write(packs));
    Console.WriteLine($"Generated metadata for {imported.Units.Count} source units; no armies or database files included.");
    return 0;
}

if (args is ["--build-detachment-metadata", var detachmentArchive, var detachmentOutput])
{
    var bytes = File.ReadAllBytes(detachmentArchive);
    var sourceInfo = new SourceInfo("BSData", "metadata", "", "", Edition: "11", ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    var data = BsDataImporter.ImportDetachmentMetadata(BsDataDocument.ReadArchive(bytes), sourceInfo);
    List<GrimList.Application.DetachmentMetadataRecovery.Pack> packs = [];
    if (File.Exists(detachmentOutput))
    {
        using var input = File.OpenRead(detachmentOutput); using var oldGzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(oldGzip); packs = DataJson.Read<List<GrimList.Application.DetachmentMetadataRecovery.Pack>>(reader.ReadToEnd());
    }
    packs.RemoveAll(p => p.SourceHash.Equals(sourceInfo.ContentHash, StringComparison.OrdinalIgnoreCase));
    packs.Add(GrimList.Application.DetachmentMetadataRecovery.Create(data));
    using var file = File.Create(detachmentOutput); using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize);
    using var writer = new StreamWriter(gzip); writer.Write(DataJson.Write(packs));
    Console.WriteLine($"Generated {data.Detachments?.Count} detachment definitions and {data.FactionContexts?.Count} catalogue contexts."); return 0;
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.local/tests", Guid.NewGuid().ToString()));
Directory.CreateDirectory(root);
var failures = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid import was accepted"); }
if (args.Contains("--import-evaluation-only"))
{
    ImportEvaluationOptimizationChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
WeaponMetadataChecks.Run(Test, root);
CatalogueOptimizationChecks.Run(Test, root);
ImportEvaluationOptimizationChecks.Run(Test, root);
if (args.Contains("--catalogue-import-regression-only"))
{
    MfmPointsChecks.Run(Test, root); ReferenceImportChecks.Run(Test, root, args);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--catalogue-optimization-only"))
{
    ImportStorageChecks.Run(Test, root); FactionLoadingChecks.Run(Test, root); StartupRecoveryChecks.Run(Test, root);
    EffectiveDatasheetChecks.Run(Test, root); AttachmentRecipientChecks.Run(Test, root, freshOnly: true);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
EnhancementPresentationChecks.Run(Test);
ReferenceIndexChecks.Run(Test);
if (args.Contains("--metadata-optimization-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--presentation-cache-only"))
{
    FactionLoadingChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--startup-revisions-only"))
{
    FactionLoadingChecks.Run(Test, root); StartupRecoveryChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
CompiledRuleChecks.Run(Test);
if (args.Contains("--compiled-rules-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
StratagemRefreshChecks.Run(Test, root);
if (args.Contains("--stratagem-refresh-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
NestedDetachmentChecks.Run(Test, root);
if (args.Contains("--nested-detachments-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
EnhancementStatChecks.Run(Test, root);
DetachmentLifecycleChecks.Run(Test, root);
if (args.Contains("--stats-retirement-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--stats-retirement-compatibility-only"))
{
    SpaceMarineStratagemChecks.Run(Test, root); SpaceMarineArmyRuleChecks.Run(Test, root);
    ConditionalRuleChecks.Run(Test, root); PantheonChecks.Run(Test, root); EffectiveDatasheetChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--historical-sample-only"))
{
    CheckHistoricalSample(); Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--text-export-only"))
{
    TextExportChecks.Run(Test); ExportChecks.Run(Test, root); ArmyValidationChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--pantheon-only"))
{
    PantheonChecks.Run(Test, root); RosterOrderChecks.Run(Test);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--detachment-budget-only"))
{
    DetachmentChecks.Run(Test, root); DetachmentApplicabilityChecks.Run(Test, root); MultiDispositionChecks.Run(Test, root);
    ReferenceRecoveryChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
EnhancementWeaponChecks.Run(Test, root);
EquipmentPriceChecks.Run(Test, root);
if (args.Contains("--equipment-prices-only"))
{
    PricingPatternChecks.RunContextual(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--enhancement-weapons-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--army-rule-coverage-only"))
{
    ArmyRuleCoverageChecks.Run(Test, root);
    UpgradeRecoveryChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--library-search-only"))
{
    LibrarySearchChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
if (args.Contains("--enhancement-evidence-only"))
{
    EnhancementEvidenceChecks.Run(Test);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
BattleTrackerChecks.Run(Test, root);
BattleDiscardChecks.Run(Test, root);
BattleActionChecks.Run(Test, root);
BattleScoreboardChecks.Run(Test, root);
BattleExchangeChecks.Run(Test, root);
if (args.Contains("--battle-tracker-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
DispositionPrimaryChecks.Run(Test, root);
PrimaryMissionCardChecks.Run(Test, args);
if (args.Contains("--primary-missions-only")) { MultiDispositionChecks.Run(Test, root); Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--reference-integration-only"))
{
    FactionLoadingChecks.Run(Test, root); RulesPackChecks.Run(Test, root); ApplicationChecks.Run(Test, root);
    ReferenceImportChecks.Run(Test, root, args); StratagemTransferChecks.Run(Test, root, args);
    GenericReferenceChecks.Run(Test, root, args); DisposableReferenceChecks.Run(Test, root, args);
    GroupedReferenceChecks.Run(Test, root, args);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
SpaceMarineStratagemChecks.Run(Test, root);
if (args.Contains("--reference-recovery-only")) { ReferenceRecoveryChecks.Run(Test, root); StartupRecoveryChecks.Run(Test, root); Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--marine-stratagems-only")) { ReferenceRecoveryChecks.Run(Test, root); Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
SpaceMarineArmyRuleChecks.Run(Test, root);
if (args.Contains("--army-rules-only")) { ReferenceRecoveryChecks.Run(Test, root); Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
DetachmentCatalogueChecks.Run(Test, root);
if (args.Contains("--detachments-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
ImportStorageChecks.Run(Test, root);
if (args.Contains("--import-storage-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
FactionLoadingChecks.Run(Test, root);
if (args.Contains("--factions-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
StartupRecoveryChecks.Run(Test, root);
if (args.Contains("--startup-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
RulesPackChecks.Run(Test, root);
if (args.Contains("--rules-packs-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
MfmPointsChecks.Run(Test, root);
if (args.Contains("--mfm-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
ReferenceRecoveryChecks.Run(Test, root);
InheritedLeaderChecks.Run(Test, root);
StatlineDisplayChecks.Run(Test);
if (args.Contains("--attachment-statlines-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--android-references-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--performance-only"))
{
    EffectiveComparisonChecks.Run(Test); PerformanceChecks.Run(Test, root); RecoveryPerformanceChecks.Run(Test, root); ArmySummaryChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1;
}
EffectiveComparisonChecks.Run(Test);
AttachmentRecipientChecks.Run(Test, root, args.Contains("--recipients-fresh-only"));
if (args.Contains("--recipients-only") || args.Contains("--recipients-fresh-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
WeaponRuleChecks.Run(Test, root);
SourceWeaponGrantChecks.Run(Test, root);
if (args.Contains("--weapon-rules-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
if (args.Contains("--warlord-only"))
{
    WarlordChecks.Run(Test, root);
    Console.WriteLine($"Artifacts: {root}");
    return failures == 0 ? 0 : 1;
}
if (args.Contains("--source-enhancements-only"))
{
    UpgradeRecoveryChecks.Run(Test, root);
    SourceEnhancementChecks.Run(Test, root, args);
    EleventhGapChecks.Run(Test, root, args);
    Console.WriteLine($"Artifacts: {root}");
    return failures == 0 ? 0 : 1;
}
ConditionalEngineChecks.Run(Test);
if (args.Contains("--conditional-engine-only")) return failures == 0 ? 0 : 1;
ConditionalRuleChecks.Run(Test, root);
PantheonChecks.Run(Test, root);
RosterOrderChecks.Run(Test);
ScrollRetentionChecks.Run(Test);
if (args.Contains("--conditional-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
DetachmentApplicabilityChecks.Run(Test, root);
BugReportChecks.Run(Test, root);
if (args.Contains("--playtester-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
EffectiveDatasheetChecks.Run(Test, root);
if (args.Contains("--effective-only")) { Console.WriteLine($"Artifacts: {root}"); return failures == 0 ? 0 : 1; }
BootstrapChecks.Run(Test, root);
EnhancementEvidenceChecks.Run(Test);
if (args.Contains("--bootstrap-only"))
{
    Console.WriteLine($"Artifacts: {root}");
    return failures == 0 ? 0 : 1;
}
if (args.Contains("--disposable-reference-only"))
{
    DisposableReferenceChecks.Run(Test, root, args);
    return failures == 0 ? 0 : 1;
}
var source = new SourceInfo("Synthetic test source", "1.0", "2026-01-01", "");
var unit = new UnitDefinition("Space Marines", "Test Squad", [new(5, 80), new(10, 150), new(5, 95, 3), new(10, 175, 3)], 3, "Battleline",
    new(new() { ["M"] = "6\"", ["T"] = "4", ["SV"] = "3+", ["W"] = "2", ["LD"] = "6+", ["OC"] = "2" }, [new("Example weapon", "Ranged", "24\"", "2", "3+", "4", "0", "1")], "Synthetic unit profile for UI tests only."));
var dataset = new Dataset(source, [unit], Detachments: [new("Space Marines", "Test detachment"), new("Orks", "Other faction detachment")],
    ReferenceCards: [new("Space Marines", "Army Rules", "Example army rule", "Synthetic reference text for UI tests."), new("Space Marines", "Stratagems", "Example stratagem", "Synthetic reference text; no rules are applied.", "Test detachment", "1 CP"), new("Orks", "Army Rules", "Other faction rule", "Must not appear for Space Marines.")]);
ArmyEntry Entry(int size = 5) => new(Guid.NewGuid().ToString(), unit.Key, unit.Name, size);
var army = new ArmyList(Guid.NewGuid().ToString(), "Test army", unit.Faction, 250, [Entry(), Entry(), Entry()]);

Test("Copy-dependent prices and over-limit checks", () =>
{
    var result = ArmyCalculator.Calculate(army, dataset);
    Equal(255, result.Total); Equal(95, result.Entries[2].Points); Equal(true, result.Issues.Any(i => i.Contains("over")));
});
Test("Removing a unit re-evaluates copy tiers", () => Equal(160, ArmyCalculator.Calculate(army with { Entries = army.Entries.Skip(1).ToList() }, dataset).Total));
Test("Missing units never appear as a complete zero-cost list", () =>
{
    var result = ArmyCalculator.Calculate(army, dataset with { Units = [] });
    Equal(false, result.Complete); Equal(null, result.Entries[0].Points); Equal(true, result.Issues.Any(i => i.Contains("missing")));
});
Test("Unsupported model counts and faction mismatch are flagged", () =>
{
    var result = ArmyCalculator.Calculate(army with { Faction = "Orks", Entries = [Entry(7)] }, dataset);
    Equal(false, result.Complete); Equal(true, result.Issues.Any(i => i.Contains("size 7"))); Equal(true, result.Issues.Any(i => i.Contains("different faction")));
});
Test("Imported copy limits are enforced", () => Equal(true, ArmyCalculator.Calculate(army with { Limit = 2000, Entries = [Entry(), Entry(), Entry(), Entry()] }, dataset).Issues.Any(i => i.Contains("limit of 3"))));
Test("Other game datasets cannot price this army", () => Equal(false, ArmyCalculator.Calculate(army, dataset with { Game = "Another game" }).Complete));
Test("JSON round trip and source metadata", () =>
{
    var actual = DataJson.Read<Dataset>(DataJson.Write(dataset)); actual.Validate(); Equal(source, actual.Source); Equal(4, actual.Units[0].Options.Count);
});
Test("Invalid source, duplicate options and missing base prices rejected", () =>
{
    Reject(() => (dataset with { Source = source with { Date = "tomorrow" } }).Validate());
    Reject(() => (dataset with { Units = [unit, unit] }).Validate());
    Reject(() => (dataset with { Units = [unit with { Options = [new(5, 80), new(5, 90)] }] }).Validate());
    Reject(() => (dataset with { Units = [unit with { Options = [new(5, 95, 3)] }] }).Validate());
});
Test("SQLite save, replace entries, close and reload", () =>
{
    var path = Path.Combine(root, "persistence");
    using (var store = WindowsServices.OpenStore(path)) { store.Import(dataset); store.Save(army); store.Save(army with { Name = "O'Brien's roster", Entries = [Entry(10)] }); }
    using (var store = WindowsServices.OpenStore(path))
    {
        Equal(1, store.LoadArmies().Count); Equal("O'Brien's roster", store.LoadArmies()[0].Name); Equal(150, ArmyCalculator.Calculate(store.LoadArmies()[0], store.LoadDataset()).Total);
        Equal(source.Version, store.LoadDataset()!.Source.Version); Equal(true, store.LoadDataset()!.Source.ImportedAt.Length > 0);
        store.Delete(army.Id); Equal(0, store.LoadArmies().Count);
    }
});
Test("Points update recalculates saved armies; invalid import retains old source", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "updates")); store.Import(dataset); store.Save(army);
    var changed = dataset with { Source = source with { Version = "2.0" }, Units = [unit with { Options = [new(5, 100), new(10, 160)] }] };
    Equal(4, ArmyStore.Compare(dataset, changed).Count);
    store.Import(changed); Equal(300, ArmyCalculator.Calculate(store.LoadArmies()[0], store.LoadDataset()).Total);
    Reject(() => store.Import(changed with { Units = [] })); Equal("2.0", store.LoadDataset()!.Source.Version);
});
Test("Existing databases migrate without losing armies or units", () =>
{
    var directory = Path.Combine(root, "legacy"); Directory.CreateDirectory(directory);
    using (var legacy = new SqliteDb(Path.Combine(directory, "grimlist.db")))
    {
        legacy.Run("CREATE TABLE armies(id TEXT PRIMARY KEY,name TEXT,faction TEXT,points_limit INTEGER,game TEXT,updated TEXT)");
        legacy.Run("CREATE TABLE entries(id TEXT PRIMARY KEY,army_id TEXT,unit_key TEXT,name TEXT,models INTEGER,position INTEGER)");
        legacy.Run("INSERT INTO armies VALUES('legacy','Old army','Space Marines',2000,'Warhammer 40,000','2025-01-01')");
        legacy.Run("INSERT INTO entries VALUES('entry','legacy',?,'Test Squad',5,0)", unit.Key);
    }
    using (var store = WindowsServices.OpenStore(directory))
    {
        var previous = store.LoadArmies().Single(); Equal("", previous.Detachment); Equal(1, previous.Entries.Count);
        store.Save(previous with { Detachment = "Chosen detachment" });
    }
    using (var store = WindowsServices.OpenStore(directory)) { Equal("Chosen detachment", store.LoadArmies().Single().Detachment); Equal(1, store.LoadArmies().Single().Entries.Count); }
});
Test("Detachment catalogue and selection persist, duplicate and export", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "detachments")); store.Import(dataset);
    var chosen = army with { Detachment = "Test detachment" }; store.Save(chosen);
    var copy = chosen with { Id = Guid.NewGuid().ToString(), Entries = [] }; store.Save(copy);
    Equal(2, store.LoadDataset()!.Detachments!.Count);
    Equal(true, store.LoadArmies().All(a => a.Detachment == "Test detachment"));
    Equal(true, ArmyCalculator.Export(chosen, dataset).Contains("Detachment: Test detachment"));
    Equal(2, DataJson.Read<Dataset>(DataJson.Write(store.LoadDataset())).Detachments!.Count);
});
Test("Imported categories, profiles, references and roster notes round trip", () =>
{
    var path = Path.Combine(root, "details");
    using (var store = WindowsServices.OpenStore(path))
    {
        store.Import(dataset);
        store.Save(army with { Entries = [Entry() with { Notes = "Personal loadout notes" }] });
    }
    using (var store = WindowsServices.OpenStore(path))
    {
        var loaded = store.LoadDataset()!;
        Equal("Battleline", loaded.Units[0].Category);
        Equal("Example weapon", loaded.Units[0].Profile!.Weapons![0].Name);
        Equal("3+", loaded.Units[0].Profile!.Characteristics!["SV"]);
        Equal(3, loaded.ReferenceCards!.Count);
        Equal("Personal loadout notes", store.LoadArmies()[0].Entries[0].Notes);
        Equal(true, ArmyCalculator.Export(store.LoadArmies()[0], loaded).Contains("Personal loadout notes"));
    }
});
Test("Traditional MFM text including wrapped unit names", () =>
{
    var text = "SPACE MARINES\nTest Squad\n5 models............80 pts\n10 models............150 pts\nCaptain in\nTest Armour\n1 model........80 pts\nENHANCEMENTS\nTest charm....15 pts";
    var parsed = MfmImporter.ParseText(text, source); Equal(2, parsed.Dataset.Units.Count); Equal("Captain in Test Armour", parsed.Dataset.Units[0].Name); Equal(2, parsed.Dataset.Units[1].Options.Count);
});
Test("Public MFM web text tiers; excludes wargear and leader relationships", () =>
{
    var text = "SPACE MARINES\nUNITS\nTEST SQUAD\nYOUR 1ST TO 2ND UNITS COST\n5 models 80 pts\nYOUR 3RD + UNIT COSTS\n5 models 95 pts\nWARGEAR OPTIONS\nper Test weapon 10 pts\nTEST LEADER\nYOUR UNIT COSTS\n1 model 50 pts\nLEADERImage\nTEST SQUAD, ANOTHER SQUAD\nTEST VEHICLE\nYOUR UNIT COSTS\n1 model 200 pts";
    var parsed = MfmImporter.ParseText(text, source); Equal(3, parsed.Dataset.Units.Count); Equal(95, parsed.Dataset.Units.Single(u => u.Name == "TEST SQUAD").Options[1].Points); Equal(3, parsed.Dataset.Units.Single(u => u.Name == "TEST SQUAD").Options[1].FromCopy);
});
Test("Duplicate and unrecognized imports fail closed", () =>
{
    Reject(() => MfmImporter.ParseText("No points here", source));
    Reject(() => MfmImporter.ParseText("ORKS\nTest Mob\n5 models 80 pts\n5 models 90 pts", source));
});
Test("Multi-column layout preserves name/price relationships", () =>
{
    var layout = "SPACE MARINES\n" + "Test Alpha".PadRight(45) + "Test Beta\n" + "5 models........80 pts".PadRight(45) + "1 model.........150 pts\n" + "10 models.......150 pts\n";
    var parsed = MfmImporter.ParseText(MfmImporter.ReadColumns(layout), source);
    Equal(2, parsed.Dataset.Units.Count); Equal(80, parsed.Dataset.Units[0].Options[0].Points); Equal(150, parsed.Dataset.Units[1].Options[0].Points);
});
Test("Readable export records source and incomplete points", () =>
{
    var text = ArmyCalculator.Export(army, dataset with { Units = [] }); Equal(true, text.Contains("UNKNOWN POINTS")); Equal(true, text.Contains(source.Name)); Equal(true, text.Contains("not validated"));
});

Test("Points merge preserves profiles, rules, limits and other factions", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "layered"));
    store.Import(dataset with { Units = [unit, new("Orks", "Other mob", [new(10, 90)])] }); store.Save(army);
    var before = store.LoadDataset()!; var id = before.Units.Single(u => u.Name == unit.Name).Id;
    var points = new Dataset(source with { Version = "2.0" }, [new(unit.Faction, unit.Name.ToUpperInvariant(), [new(5, 110)])]);
    var update = store.PreviewImport(points, ImportLayer.Points, true); store.ApplyImport(update.Dataset);
    var after = store.LoadDataset()!; var actual = after.FindUnit(id)!;
    Equal(id, actual.Id); Equal("3+", actual.Profile!.Characteristics!["SV"]); Equal(3, actual.MaxCopies);
    Equal(2, after.Units.Count); Equal(3, after.ReferenceCards!.Count); Equal(2, after.Detachments!.Count);
    Equal("2.0", actual.PointsSource!.Version); Equal("1.0", actual.ProfileSource!.Version);
    Equal(330, ArmyCalculator.Calculate(store.LoadArmies()[0], after).Total);
    Equal(true, store.LoadArmies()[0].Entries.All(e => e.UnitKey == id));
});
Test("Profiles can be imported first and updated without changing prices", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "profiles-first"));
    var profiles = dataset with { Units = [unit with { Options = [] }] };
    store.ApplyImport(store.PreviewImport(profiles, ImportLayer.Profiles).Dataset);
    Equal(false, ArmyCalculator.Calculate(army, store.LoadDataset()).Complete);
    var id = store.LoadDataset()!.Units[0].Id;
    store.ApplyImport(store.PreviewImport(new(source with { Version = "prices" }, [unit with { Profile = null }]), ImportLayer.Points).Dataset);
    var profileUpdate = profiles with { Source = source with { Version = "profile revision" }, Units = [unit with { Options = [], Profile = unit.Profile! with { Notes = "Updated only" } }] };
    store.ApplyImport(store.PreviewImport(profileUpdate, ImportLayer.Profiles).Dataset);
    var actual = store.LoadDataset()!.Units[0];
    Equal(id, actual.Id); Equal(4, actual.Options.Count); Equal("prices", actual.PointsSource!.Version); Equal("profile revision", actual.ProfileSource!.Version); Equal("Updated only", actual.Profile!.Notes);
});
Test("Rename by permanent ID preserves entries and aliases across restart", () =>
{
    var path = Path.Combine(root, "rename"); string id;
    using (var store = WindowsServices.OpenStore(path))
    {
        store.Import(dataset); store.Save(army); var data = store.LoadDataset()!; id = data.Units[0].Id;
        var renamed = data.Units[0] with { Name = "Renamed squad", Options = [new(5, 120)] };
        store.ApplyImport(store.PreviewImport(new(source, [renamed]), ImportLayer.Points).Dataset);
    }
    using (var store = WindowsServices.OpenStore(path))
    {
        Equal(id, store.LoadDataset()!.FindUnit(unit.Key)!.Id);
        Equal("Renamed squad", store.LoadArmies()[0].Entries[0].Name);
        Equal(360, ArmyCalculator.Calculate(army, store.LoadDataset()).Total);
    }
});
Test("Punctuation aliases match conservatively and cannot collide", () =>
{
    var current = DatasetMerge.Prepare(null, new(source, [unit with { Name = "O’Brien’s Squad" }]), ImportLayer.Combined).Dataset;
    var update = DatasetMerge.Prepare(current, new(source, [unit with { Name = "O'Brien's Squad" }]), ImportLayer.Points).Dataset;
    Equal(current.Units[0].Id, update.Units[0].Id); Equal(1, update.Units.Count);
    Reject(() => DatasetMerge.Prepare(current, new(source, [unit with { Name = "O'Brien's Squad" }, unit with { Name = "O’Brien’s Squad" }]), ImportLayer.Points));
});
Test("Complete faction updates clear absent prices; partial updates retain them", () =>
{
    var current = DatasetMerge.Prepare(null, dataset with { Units = [unit, unit with { Name = "Retired squad" }, new("Orks", "Other mob", [new(10, 90)])] }, ImportLayer.Combined).Dataset;
    var partial = DatasetMerge.Prepare(current, new(source, [unit]), ImportLayer.Points).Dataset;
    Equal(4, partial.Units.Single(u => u.Name == "Retired squad").Options.Count);
    var complete = DatasetMerge.Prepare(current, new(source, [unit]), ImportLayer.Points, true).Dataset;
    Equal(0, complete.Units.Single(u => u.Name == "Retired squad").Options.Count);
    Equal(true, complete.Units.Single(u => u.Name == "Retired squad").Profile != null);
    Equal(90, complete.Units.Single(u => u.Faction == "Orks").Options[0].Points);
});
Test("Edition mismatches reject; backwards dates warn; ambiguous IDs reject", () =>
{
    var current = DatasetMerge.Prepare(null, dataset with { Source = source with { Edition = "10" } }, ImportLayer.Combined).Dataset;
    Reject(() => DatasetMerge.Prepare(current, dataset with { Source = source with { Edition = "11" } }, ImportLayer.Points));
    Equal(true, DatasetMerge.Prepare(current, dataset with { Source = source with { Date = "2025-01-01", Edition = "10" } }, ImportLayer.Points).Warnings.Any(w => w.Contains("backwards")));
    Reject(() => DatasetMerge.Prepare(current, dataset with { Units = [unit with { Id = "different-id" }] }, ImportLayer.Points));
    Reject(() => DatasetMerge.Prepare(current, dataset with { Units = [] }, ImportLayer.Points, true));
});
Test("Legacy name-based dataset migrates profiles and roster keys atomically", () =>
{
    var path = Path.Combine(root, "legacy-library");
    using (var store = WindowsServices.OpenStore(path)) { store.ApplyImport(dataset); store.Save(army); }
    using (var db = new SqliteDb(Path.Combine(path, "grimlist.db")))
    {
        // Construct the actual pre-snapshot format; new imports no longer write
        // this duplicate compatibility record.
        db.Run("INSERT INTO source_details SELECT id,? FROM sources", DataJson.Write(new
        { Units = dataset.Units.Select(u => new { u.Key, u.Category, u.Profile }), Cards = dataset.ReferenceCards }));
        db.Run("DELETE FROM dataset_snapshots");
    }
    using (var store = WindowsServices.OpenStore(path))
    {
        Equal(unit.Key, store.LoadDataset()!.Units[0].Key);
        var merged = store.PreviewImport(new(source, [unit with { Profile = null }]), ImportLayer.Points);
        Equal(0, ArmyStore.Compare(store.LoadDataset(), merged.Dataset).Count);
        store.ApplyImport(merged.Dataset);
        Equal(true, store.LoadArmies()[0].Entries[0].UnitKey.StartsWith("unit-"));
        Equal("Example weapon", store.LoadDataset()!.Units[0].Profile!.Weapons![0].Name);
    }
});
Test("A stale review cannot overwrite a newer library transaction", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "stale-preview")); store.Import(dataset);
    var expected = DataJson.Write(store.LoadDataset());
    var preview = store.PreviewImport(dataset, ImportLayer.Points).Dataset;
    store.Import(dataset with { Source = source with { Version = "newer" } });
    Reject(() => store.ApplyImport(preview, expected));
    Equal("newer", store.LoadDataset()!.Source.Version);
});
void CheckHistoricalSample() => Test("Historical real Death Guard sample imports both layers and prices roster", () =>
{
    var samples = Path.GetFullPath(Path.Combine(root, "../../../samples"));
    var points = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(samples, "death-guard-historical-points.json")));
    var profiles = DataJson.Read<Dataset>(File.ReadAllText(Path.Combine(samples, "death-guard-historical-profiles.json")));
    var parsed = MfmImporter.ParseText(File.ReadAllText(Path.Combine(samples, "death-guard-historical-mfm.txt")), points.Source).Dataset;
    Equal(3, parsed.Units.Count); Equal(0, ArmyStore.Compare(points, parsed).Count);
    using var store = WindowsServices.OpenStore(Path.Combine(root, "death-guard-sample"));
    store.ApplyImport(store.PreviewImport(profiles, ImportLayer.Profiles).Dataset);
    store.ApplyImport(store.PreviewImport(points, ImportLayer.Points).Dataset);
    var data = store.LoadDataset()!;
    var list = ArmyList.New("Death Guard") with { Name = "Historical sample", Entries = data.Units.Select(u => new ArmyEntry(Guid.NewGuid().ToString(), u.Key, u.Name, u.Options[0].Models)).ToList() };
    store.Save(list); Equal(205, ArmyCalculator.Calculate(list, data).Total);
    Equal(7, data.Units.Sum(u => u.Profile!.Weapons!.Count));
    Equal("December 2024 archive", data.Units[0].ProfileSource!.Version);
    Equal(true, data.Units.Single(u => u.Name == "Death Guard Rhino").Profile!.Notes.Contains("OR"));
});
CheckHistoricalSample();
Test("MFM CODEX headings and printed point change annotations", () =>
{
    var parsed = MfmImporter.ParseText("CODEX: SPACE MARINES\nTest Squad\n5 models....(+5) 85 pts", source);
    Equal(85, parsed.Dataset.Units[0].Options[0].Points);
});

const string downloadHtml = "<h2>Munitorum Field Manual</h2><a href='https://mfm.warhammer-community.com/en'>updated 30/09/2026</a>";
const string indexHtml = "<h2>v1.0</h2><a href='/en/death-guard'>Death Guard</a><a href='/en/orks'>Orks</a>";
const string factionHtml = "<script>YOUR UNIT COSTS 1 model 999 pts</script><h2>v1.0</h2><h3>UNITS</h3><h4>TEST SQUAD</h4><p>YOUR 1ST TO 2ND UNITS COST</p><ul><li><span>5 models</span><span>80 pts</span></li></ul><p>YOUR 3RD + UNIT COSTS</p><li>5 models 95 pts</li><h4>TEST LEADER</h4><p>YOUR UNIT COSTS</p><li>1 model 50 pts</li><h5>LEADER</h5><p>TEST SQUAD</p><h3>DETACHMENTS</h3><p>TEST DETACHMENT 10 pts</p>";
Test("Automatic MFM download discovers factions, parses, caches and records provenance", () =>
{
    using var client = new HttpClient(new FixtureHandler(request => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host.StartsWith("www.") ? downloadHtml : request.RequestUri.AbsolutePath == "/en" ? indexHtml : factionHtml) }));
    var result = new OfficialMfmClient(client, new WindowsPdfTextExtractor()).Download("Death Guard", Path.Combine(root, "download")).GetAwaiter().GetResult();
    Equal(2, result.Dataset.Units.Count); Equal("2026-09-30", result.Dataset.Source.Date); Equal("1.0", result.Dataset.Source.Version);
    Equal(64, result.Dataset.Source.ContentHash.Length); Equal(true, File.Exists(Path.Combine(result.CacheDirectory, "death-guard.html")));
    Equal("https://mfm.warhammer-community.com/en/death-guard", result.Dataset.Units[0].PointsSource!.Url);
    Equal(95, result.Dataset.Units.Single(u => u.Name == "TEST SQUAD").Options[1].Points);
    var all = new OfficialMfmClient(client, new WindowsPdfTextExtractor()).Download(null, Path.Combine(root, "download-all")).GetAwaiter().GetResult(); Equal(4, all.Dataset.Units.Count);
});
Test("Changed HTML or missing unit prices cannot become a partial faction update", () =>
{
    Reject(() => OfficialMfmClient.ParseFactionHtml("<h1>Access denied</h1>", "Death Guard", source));
    Reject(() => OfficialMfmClient.ParseFactionHtml(factionHtml + "<h4>UNREADABLE UNIT</h4><p>YOUR UNIT COSTS</p><li>5 models ??? pts</li>", "Death Guard", source));
    Reject(() => OfficialMfmClient.ParseFactionHtml(factionHtml.Replace("5 models", "five models"), "Death Guard", source));
});
Test("403, cancellation, redirects and a failed second faction leave the database unchanged", () =>
{
    using var store = WindowsServices.OpenStore(Path.Combine(root, "download-failure")); store.Import(dataset); var before = DataJson.Write(store.LoadDataset());
    foreach (var scenario in new[] { "403", "cancel", "redirect", "partial" })
    {
        using var client = new HttpClient(new FixtureHandler(request =>
        {
            if (scenario == "403" || (scenario == "partial" && request.RequestUri!.AbsolutePath == "/en/orks")) return new(System.Net.HttpStatusCode.Forbidden);
            if (scenario == "redirect") { var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://example.com/private"); return redirect; }
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.Host.StartsWith("www.") ? downloadHtml : request.RequestUri.AbsolutePath == "/en" ? indexHtml : factionHtml) };
        }));
        var failed = false;
        try { new OfficialMfmClient(client, new WindowsPdfTextExtractor()).Download(null, Path.Combine(root, "failed-download"), cancellation: new CancellationToken(scenario == "cancel")).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException) { failed = true; }
        Equal(true, failed); Equal(before, DataJson.Write(store.LoadDataset()));
    }
});

BsDataChecks.Run(Test, root);
CerastusChecks.Run(Test);
PricingRepairChecks.Run(Test, root);
PricingPatternChecks.Run(Test);
PricingPatternChecks.RunContextual(Test, root);
PricingPatternChecks.RunSelectionSemantics(Test, root, args);
AllArmyChecks.Run(Test, root);
WarlordChecks.Run(Test, root);
LoadoutChecks.Run(Test, root);
LoadoutOptionChecks.Run(Test, root, args);
LeaderChecks.Run(Test, root);
MultipleLeaderChecks.Run(Test, root, args);
CrossFactionLeaderChecks.Run(Test, args);
AttachmentCombinationChecks.Run(Test, root);
CatalogueAttachmentChecks.Run(Test, args, root);
AssociationChecks.Run(Test);
AttachedEffectChecks.Run(Test, root, args);
ReferenceChecks.Run(Test, root, args);
ReferenceImportChecks.Run(Test, root, args);
StratagemTransferChecks.Run(Test, root, args);
GenericReferenceChecks.Run(Test, root, args);
DisposableReferenceChecks.Run(Test, root, args);
GroupedReferenceChecks.Run(Test, root, args);
DetachmentChecks.Run(Test, root);
MultiDispositionChecks.Run(Test, root);
UnitAvailabilityChecks.Run(Test, root);
AlliedChecks.Run(Test, root);
EnhancementChecks.Run(Test, root);
SourceEnhancementChecks.Run(Test, root, args);
UpgradePricingChecks.Run(Test, root, args);
UpgradeRecoveryChecks.Run(Test, root);
EleventhGapChecks.Run(Test, root, args);
ExportChecks.Run(Test, root);
TextExportChecks.Run(Test);
MusterChecks.Run(Test);
ArmyValidationChecks.Run(Test, root);
DatasheetReferenceChecks.Run(Test);
EnhancementDefenceChecks.Run(Test, args, root);
SelectionLimitChecks.Run(Test, root, args);

ApplicationChecks.Run(Test, root);
ArmyRuleCoverageChecks.Run(Test, root);
LibrarySearchChecks.Run(Test, root);
PerformanceChecks.Run(Test, root);
RecoveryPerformanceChecks.Run(Test, root);
ArmySummaryChecks.Run(Test, root);

// A real, small PDF fixture exercises the installed extractor, not a mocked text path.
var pdfPath = Path.Combine(root, "synthetic-mfm.pdf");
var content = "BT /F1 12 Tf 40 760 Td (SPACE MARINES) Tj 0 -30 Td (Test Alpha) Tj 0 -20 Td (5 models........80 pts) Tj 0 -20 Td (10 models.......150 pts) Tj ET\nBT /F1 12 Tf 330 730 Td (Test Beta) Tj 0 -20 Td (1 model.........150 pts) Tj ET";
var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream" };
var pdf = new StringBuilder("%PDF-1.4\n"); List<int> offsets = [0];
for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
var xref = pdf.Length; pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
foreach (var offset in offsets.Skip(1)) pdf.Append($"{offset:0000000000} 00000 n \n");
pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); File.WriteAllText(pdfPath, pdf.ToString(), Encoding.ASCII);
try
{
    var extracted = await new WindowsPdfTextExtractor().ExtractAsync(pdfPath, root);
    Test("Real PDF extraction through pdftotext", () => { var parsed = MfmImporter.ParseText(extracted, source); Equal(2, parsed.Dataset.Units.Count); Equal("Test Alpha", parsed.Dataset.Units[0].Name); Equal(2, parsed.Dataset.Units[0].Options.Count); Equal(150, parsed.Dataset.Units[1].Options[0].Points); });
}
catch (Exception ex) { failures++; Console.WriteLine("FAIL PDF extraction: " + ex.Message); }
// Separate data for the optional native UI smoke test. Never loaded into the user's app.
using (var store = WindowsServices.OpenStore(Path.Combine(root, "ui"))) { store.Import(dataset); store.Save(army); }
Console.WriteLine($"Artifacts: {root}");
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} checks failed.");
return failures == 0 ? 0 : 1;

sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = respond(request); response.RequestMessage = request; return Task.FromResult(response);
    }
}
