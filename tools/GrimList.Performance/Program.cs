using System.Diagnostics;
using GrimList.Core;
using GrimList.Application;
using GrimList.Persistence;
using GrimList.Platform;
using GrimList.Platform.Windows;

#if PERFORMANCE_CHECKS
if (args is ["--controlled", var controlledDirectory, var controlledMode, var controlledOutput, var controlledIterations])
{
    var path = Path.GetFullPath(controlledDirectory);
    var marker = Path.Combine(path, "controlled-performance.fixture");
    if (Directory.Exists(path) && !File.Exists(marker)) throw new InvalidOperationException("Refusing an unmarked existing library");
    Directory.CreateDirectory(path); File.WriteAllText(marker, "Disposable controlled performance fixture");
    using var controlledStore = LibraryBootstrap.Open(new WindowsPaths(path), new WindowsSqliteConnectionFactory());
    var controlledSession = new ArmySession(controlledStore, useFactionLoading: true);
    var comparison = await ControlledComparison.Run(controlledSession, _ => Task.CompletedTask,
        controlledMode, int.Parse(controlledIterations));
    File.WriteAllText(controlledOutput, DataJson.Write(comparison));
    Console.WriteLine($"Completed controlled comparison: {controlledMode}");
    return;
}
#endif

if (args is ["--catalogue-workflow", var workflowDirectory])
{
    var checks = CatalogueWorkflowWorkload.Run(workflowDirectory, new WindowsSqliteConnectionFactory());
    Console.WriteLine(DataJson.Write(new { Checks = checks })); return;
}

if (args is ["--catalogue-storage", var storageDirectory])
{
    CatalogueStorageWorkload.Run(storageDirectory, new WindowsSqliteConnectionFactory()); return;
}

if (args is ["--audit-weapon-metadata", var weaponAuditOutput])
{
    var original = BootstrapRelease.ReadBundled().Dataset;
    var repaired = WeaponMetadataRecovery.ApplyBundled(original);
    var repairedCount = 0;
    for (var i = 0; i < original.Units.Count; i++)
    {
        var oldUnit = original.Units[i]; var newUnit = repaired.Units[i];
        if (ReferenceEquals(oldUnit, newUnit)) continue;
        repairedCount++;
        var restored = newUnit with { Profile = newUnit.Profile! with { Weapons = oldUnit.Profile!.Weapons } };
        if (DataJson.Write(oldUnit) != DataJson.Write(restored)) throw new InvalidDataException("Non-metadata change: " + oldUnit.Name);
        for (var n = 0; n < oldUnit.Profile!.Weapons!.Count; n++)
        {
            var before = oldUnit.Profile.Weapons[n]; var after = newUnit.Profile!.Weapons![n];
            if (after with { SourceId = before.SourceId, SourceCategories = before.SourceCategories } != before)
                throw new InvalidDataException("Changed visible weapon: " + oldUnit.Name);
        }
    }
    if (!ReferenceEquals(repaired, WeaponMetadataRecovery.ApplyBundled(repaired))) throw new InvalidDataException("Non-idempotent metadata recovery");
    var completeRecovery = EffectiveDatasheetRecovery.ApplyBundled(original);
    if (!ReferenceEquals(completeRecovery, EffectiveDatasheetRecovery.ApplyBundled(completeRecovery))) throw new InvalidDataException("Non-idempotent composite recovery");
    File.WriteAllText(weaponAuditOutput, DataJson.Write(new { Units = original.Units.Count, Repaired = repairedCount, Idempotent = true, CompositeIdempotent = true,
        Factions = repaired.Units.Where((u, i) => !ReferenceEquals(u, original.Units[i])).GroupBy(u => u.Faction).ToDictionary(g => g.Key, g => g.Count()),
        Unresolved = repaired.Units.Where(u => u.Profile?.Weapons?.Any(w => string.IsNullOrEmpty(w.SourceId) || w.SourceCategories == null) == true)
            .Select(u => new { u.Faction, u.Name, Source = u.ProfileSource?.ContentHash }).ToList() }));
    Console.WriteLine($"Verified metadata-only recovery for {repairedCount} units and repeat recovery.");
    return;
}

if (args is ["--reference-preparation", var referenceOutput])
{
    ReferencePreparationWorkload.Run(referenceOutput);
    return;
}

if (args is ["--build-weapon-metadata", var weaponArchive, var weaponOutput])
{
    var bytes = File.ReadAllBytes(weaponArchive);
    var weaponSource = new SourceInfo("BSData weapon metadata", "1", "", "", Edition: "11",
        ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    var imported = BsDataImporter.ImportAll(BsDataDocument.ReadArchive(bytes), weaponSource, new Progress<string>(Console.WriteLine)).Dataset;
    var evidence = WeaponMetadataRecovery.Create(imported);
    if (File.Exists(weaponOutput))
    {
        using var previous = System.IO.Compression.ZipFile.OpenRead(weaponOutput);
        foreach (var entry in previous.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var record = DataJson.Read<WeaponMetadataRecovery.Evidence>(reader.ReadToEnd());
            if (!record.SourceHash.Equals(weaponSource.ContentHash, StringComparison.OrdinalIgnoreCase)) evidence.Add(record);
        }
    }
    var temporaryWeaponOutput = weaponOutput + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using (var weaponStream = File.Create(temporaryWeaponOutput)) WeaponMetadataRecovery.WriteArchive(evidence, weaponStream);
        File.Move(temporaryWeaponOutput, weaponOutput, overwrite: true);
    }
    finally { if (File.Exists(temporaryWeaponOutput)) File.Delete(temporaryWeaponOutput); }
    Console.WriteLine($"Generated {evidence.Count} source-pinned weapon metadata records.");
    return;
}

if (args is ["--inspect-enhancement", var inspectInput, var inspectFaction, var inspectUnit, var inspectCard, var inspectOutput])
{
    EnhancementInspection.Run(inspectInput, inspectFaction, inspectUnit, inspectCard, inspectOutput);
    return;
}

if (args is ["--compiled-rules", var compilationOutput])
{
    RuleCompilationWorkload.Run(compilationOutput);
    return;
}

if (args is ["--faction-loading", var factionDirectory])
{
    Directory.CreateDirectory(factionDirectory);
    using var factionStore = LibraryBootstrap.Open(new WindowsPaths(Path.Combine(factionDirectory, "database")), new WindowsSqliteConnectionFactory());
    var factionReport = FactionLoadingWorkload.Run(factionStore);
    File.WriteAllText(Path.Combine(factionDirectory, "faction-loading.json"), DataJson.Write(factionReport));
    Console.WriteLine($"PASS {factionReport.Checks} checks across {factionReport.Factions} factions; {factionReport.Measurements.Count} measured samples");
    return;
}

if (args.FirstOrDefault() is "--acceptance" or "--home" or "--comparison")
{
    var directory = Path.GetFullPath(args[1]); Directory.CreateDirectory(directory);
    var allocated = GC.GetTotalAllocatedBytes(true); var startup = Stopwatch.StartNew();
    using var acceptanceStore = LibraryBootstrap.Open(new WindowsPaths(Path.Combine(directory, "database")), new WindowsSqliteConnectionFactory());
    var acceptanceSession = new ArmySession(acceptanceStore);
    var ready = startup.Elapsed.TotalMilliseconds;
    var startupAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
    var managed = GC.GetTotalMemory(true);
    if (args[0] == "--comparison")
    {
        var units = acceptanceSession.Dataset!.Units.Where(u => u.Faction == "Death Guard").Take(20).ToList();
        var state = units.ToDictionary(u => u.Key, u => (EffectiveUnitState?)new EffectiveUnitState(u, u, []));
        var copied = DataJson.Read<Dictionary<string, EffectiveUnitState?>>(DataJson.Write(state));
        string Old(Dictionary<string, EffectiveUnitState?> values) => DataJson.Write(values.OrderBy(p => p.Key).Select(p => new {
            p.Key, p.Value?.Unit.Category, p.Value?.Unit.Profile?.Keywords, p.Value?.Unit.Profile?.Characteristics,
            p.Value?.Unit.Profile?.Models, p.Value?.Unit.Profile?.Weapons, p.Value?.Unit.Profile?.Abilities }));
        foreach (var structural in new[] { false, true, false, true })
        {
            var bytes = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
            for (var i = 0; i < 500; i++)
                if (structural)
                {
                    var comparer = EffectiveStateComparer.Instance;
                    if (comparer.GetHashCode(state) != comparer.GetHashCode(copied) || !comparer.Equals(state, copied)) throw new Exception("Structural mismatch");
                }
                else if (Old(state) != Old(copied)) throw new Exception("JSON mismatch");
            Console.WriteLine($"{(structural ? "Structural" : "JSON")}: {timer.Elapsed.TotalMilliseconds:F1} ms, {GC.GetTotalAllocatedBytes(true) - bytes} allocated bytes");
        }
        return;
    }
    if (args[0] == "--home") { await HomeWorkload.Run(acceptanceSession, acceptanceStore, directory); return; }
    var samples = await AcceptanceWorkload.Run(acceptanceSession, _ => Task.CompletedTask, args.Length > 2 ? int.Parse(args[2]) : 20);
    File.WriteAllText(Path.Combine(directory, "acceptance.json"), DataJson.Write(new { LibraryReadyMs = ready,
        StartupAllocatedBytes = startupAllocated, ManagedAfterRecoveryBytes = managed, WorkingSetBytes = Environment.WorkingSet, Samples = samples }));
    Console.WriteLine("Acceptance results: " + directory); return;
}

// Run from the repository root. Only writes a unique disposable output directory.
var input = args.FirstOrDefault() ?? "tests/fixtures/libraries/GrimList-Library.json";
var output = Path.GetFullPath(args.Skip(1).FirstOrDefault() ?? ".local/performance-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
var json = File.ReadAllText(input); var source = DataJson.Read<Dataset>(json);
var factory = new TraceFactory(); using var store = new ArmyStore(Path.Combine(output, "database"), factory);
var connection = factory.Connection!;
connection.Run("INSERT INTO sources VALUES(?,?,?,?,?,?,?)", "performance", source.Source.Name, source.Source.Version, source.Source.Date, source.Source.Url, "", source.Game);
connection.Run("INSERT INTO dataset_snapshots VALUES(?,?)", "performance", json);
connection.Run("INSERT INTO settings VALUES('active_source',?)", "performance");
var repository = new TimedRepository(store);
double calculationMs = 0; var calculationCalls = 0;
var session = new ArmySession(repository, (army, data) =>
{
    var timer = Stopwatch.StartNew(); var result = ArmyCalculator.Calculate(army, data);
    calculationMs += timer.Elapsed.TotalMilliseconds; calculationCalls++; return result;
});
var data = session.Dataset!;
UnitDefinition Unit(string name) => data.Units.Single(u => u.Faction == "Death Guard" && u.Name == name);
ArmyEntry Entry(UnitDefinition unit, int size, string id) => new(id, unit.Key, unit.Name, size, Loadout: Loadouts.Create(unit, size));
var marine = Unit("Plague Marines");
var army = ArmyList.New("Death Guard") with { Name = "Performance fixture", Limit = 2000, Detachments = ["Virulent Vectorium"],
    Entries = [Entry(marine, 10, "marines"), Entry(Unit("Malignant Plaguecaster"), 1, "caster"), Entry(Unit("Biologus Putrifier"), 1, "biologus")] };
session.Open(army); session.SelectEntry("marines");
var role = marine.Profile!.Loadout!.Roles.Single(r => r.Name.Contains("Blight launcher", StringComparison.OrdinalIgnoreCase));
var section = Loadouts.Sections(marine).Single(s => s.RoleIds.Contains(role.Id));
List<object> results = [];
void Record(object value) { results.Add(value); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value)); }
void Cache(string name)
{
    var builds = session.EligibilityBuildCount; var rows = session.LibraryBuildCount;
    var allocation = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew(); var result = session.BrowseLibrary(new("", false, false, true));
    Record(new { Scenario = name, Ms = timer.Elapsed.TotalMilliseconds, AllocatedMiB = (GC.GetTotalAllocatedBytes(true) - allocation) / 1048576.0,
        Rows = result.Count, EligibilityBuilds = session.EligibilityBuildCount - builds, RowBuilds = session.LibraryBuildCount - rows });
}
void Edit(string name)
{
    session.SelectEntry("marines"); session.BrowseLibrary(new("", false, false, true));
    List<object> samples = [];
    for (var i = 0; i < 5; i++)
    {
        var allocation = GC.GetTotalAllocatedBytes(true); var reads = connection.DatasetReads;
        var eligibility = session.EligibilityBuildCount; var rowBuilds = session.LibraryBuildCount;
        calculationMs = 0; calculationCalls = 0;
        var timer = Stopwatch.StartNew();
        var delta = Loadouts.RoleCount(session.SelectedEntry!.Loadout!, role.Id) == 0 ? 1 : -1;
        session.AdjustEquipment("marines", new(section.Id, role.Id, "", "", delta));
        var command = timer.Elapsed.TotalMilliseconds; var commandAllocation = (GC.GetTotalAllocatedBytes(true) - allocation) / 1048576.0;
        var available = Stopwatch.StartNew(); session.BrowseLibrary(new("", false, false, true)); available.Stop();
        timer.Stop();
        var totalAllocation = (GC.GetTotalAllocatedBytes(true) - allocation) / 1048576.0;
        var loads = connection.DatasetReads - reads;
        if (loads != 0 || calculationCalls != 1 || session.EligibilityBuildCount != eligibility || session.LibraryBuildCount != rowBuilds)
            throw new Exception("Architectural performance contract failed.");
        samples.Add(new { CommandMs = command, repository.SaveMs, CalculateMs = calculationMs, AvailabilityMs = available.Elapsed.TotalMilliseconds,
            TotalMs = timer.Elapsed.TotalMilliseconds, CommandAllocatedMiB = commandAllocation,
            TotalAllocatedMiB = totalAllocation, DatasetLoads = loads,
            CalculationCalls = calculationCalls, EligibilityBuilds = session.EligibilityBuildCount - eligibility, RowBuilds = session.LibraryBuildCount - rowBuilds });
    }
    Record(new { Scenario = name, Samples = samples });
}
Cache("cold"); Cache("warm"); Edit("three-entry equipment edit"); Cache("after equipment");
session.AddUnit(marine.Key, 5); Cache("after add"); session.RemoveUnit(session.SelectedEntry!.Id); Cache("after remove");
session.Commit(DetachmentRules.WithSelection(session.Army, [])); Cache("after detachment");
session.Commit(session.Army with { Faction = "World Eaters" }); Cache("after faction");
session.Open(army);
var extra = data.Units.Where(u => u.Faction == "Death Guard" && !u.Name.Contains("variant") && !PricingRepair.IsCrucible(u)
    && u.Options.Count > 0 && army.Entries.All(e => e.UnitKey != u.Key)).Take(17).ToList();
session.Open(army with { Entries = [..army.Entries, ..extra.Select((u, i) => Entry(u, u.Options[0].Models, "extra" + i))] });
Edit("twenty-entry equipment edit");
var beforeSave = connection.DatasetReads; connection.Sql.Clear(); store.Save(session.Army, session.Context);
if (connection.DatasetReads != beforeSave) throw new Exception("Save read library data.");
Record(new { Scenario = "save SQL", DatasetLoads = connection.DatasetReads - beforeSave, Statements = connection.Sql });
var report = new { Environment = new { OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Configuration = "Release", Provider = "Windows SQLite; shared commands and library lookup only; no UI preparation or rendering measured" },
    Source = data.Source, Units = data.Units.Count, ReferenceCards = data.ReferenceCards?.Count, InputBytes = new FileInfo(input).Length, Results = results };
File.WriteAllText(Path.Combine(output, "results.json"), DataJson.Write(report));
Console.WriteLine("Results: " + Path.Combine(output, "results.json"));

sealed class TimedRepository(IArmyRepository inner) : IArmyRepository
{
    public double SaveMs;
    public string DirectoryPath => inner.DirectoryPath;
    public Dataset? LoadDataset() => inner.LoadDataset();
    public List<ArmyList> LoadArmies() => inner.LoadArmies();
    public void Save(ArmyList army, EditorContext? context = null) { var w = Stopwatch.StartNew(); inner.Save(army, context); SaveMs = w.Elapsed.TotalMilliseconds; }
    public void SaveContext(EditorContext c) => inner.SaveContext(c);
    public EditorContext LoadContext() => inner.LoadContext();
    public void Delete(string id) => inner.Delete(id);
    public ImportPreview PreviewImport(Dataset d, ImportLayer l, bool f = false) => inner.PreviewImport(d, l, f);
    public void ApplyImport(Dataset d, string? expected = null) => inner.ApplyImport(d, expected);
    public void Dispose() { }
}
sealed class TraceFactory : ISqliteConnectionFactory
{
    public TraceConnection? Connection;
    public ISqliteConnection Open(string path) => Connection = new(new WindowsSqliteConnectionFactory().Open(path));
}
sealed class TraceConnection(ISqliteConnection inner) : ISqliteConnection
{
    public long DatasetReads;
    public List<string> Sql = [];
    public List<string?[]> Run(string sql, params object?[] args)
    {
        if (sql.StartsWith("SELECT substr(content,1,256) FROM dataset_snapshots", StringComparison.OrdinalIgnoreCase)) DatasetReads++;
        Sql.Add(sql); return inner.Run(sql, args);
    }
    public void Transaction(Action action) => inner.Transaction(action);
    public void Dispose() => inner.Dispose();
}
