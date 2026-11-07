using GrimList.Core;
using System.Security.Cryptography;

if (args.Length > 0 && args[0] == "references")
{
    Environment.ExitCode = await ReferenceAcquisitionCli.Run(args[1..]);
    return;
}

if (args.Length == 4 && args[0] == "export-enhancement-semantics")
{
    var source = DataJson.Read<SourceInfo>(File.ReadAllText(args[2]));
    var bytes = File.ReadAllBytes(args[1]);
    if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Archive does not match the supplied source fingerprint.");
    var cards = BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(bytes), source);
    File.WriteAllText(args[3], DataJson.Write(new Dataset(source, [], ReferenceCards: cards, ReferenceOnly: true)));
    Console.WriteLine($"Exported {cards.Count} Enhancement semantics, including {cards.Count(c => c.IsUpgrade)} Upgrades.");
}
else if (args.Length == 5 && args[0] == "build-audited-reference-pack")
{
    var current = DataJson.Read<Dataset>(File.ReadAllText(args[1]));
    var imported = DataJson.Read<Dataset>(File.ReadAllText(args[2]));
    if (current.Source.Edition != imported.Source.Edition || current.Source.ContentHash != imported.Source.ContentHash)
        throw new InvalidDataException("Audit inputs must describe the same pinned source/edition.");
    var incoming = imported with { Units = [], ReferenceOnly = true,
        ReferenceCards = imported.ReferenceCards!.Where(ReferenceModel.DisplayOnly).ToList() };
    var updated = DatasetMerge.Prepare(current, incoming, ImportLayer.Profiles);
    var existing = DataJson.Read<Dataset>(File.ReadAllText(args[3]));
    var supplements = existing with { Detachments = [], ReferenceCards = existing.ReferenceCards!
        .Where(c => ReferenceModel.Canonical(c, existing.Source).Details!.SourceType != "BSData").ToList() };
    var merged = DatasetMerge.Prepare(updated.Dataset, supplements, ImportLayer.Profiles);
    if (DataJson.Write(merged.Dataset.Units) != DataJson.Write(current.Units))
        throw new InvalidDataException("Reference build changed army-building unit data.");
    var portable = ReferenceModel.ExportDataset(merged.Dataset);
    var pack = portable with { Units = [], ReferenceOnly = true, SchemaVersion = 2, RulesPacks = null,
        ReferenceCards = portable.ReferenceCards!.Where(ReferenceModel.DisplayOnly).ToList(),
        Source = new SourceInfo("GrimList reference collection", ReferenceModel.Hash(DataJson.Write(portable.ReferenceCards))[..16],
            DateTime.UtcNow.ToString("yyyy-MM-dd"), "", Edition: current.Source.Edition) };
    pack.Validate();
    File.WriteAllText(args[4], DataJson.Write(pack));
    File.WriteAllText(args[4] + ".preview.json", DataJson.Write(updated.ReferenceChanges.Concat(merged.ReferenceChanges)));
    // Exercise the public adapter and portable merge, without modifying a database.
    var readBack = new StructuredJsonReferenceAdapter().Read(Path.GetFileName(args[4]), File.ReadAllText(args[4]), current.Source.Edition);
    var replay = DatasetMerge.Prepare(current, readBack, ImportLayer.Profiles).Dataset;
    if (DataJson.Write(replay.Units) != DataJson.Write(current.Units)) throw new InvalidDataException("Portable pack changed units.");
    Console.WriteLine($"Portable pack: {pack.ReferenceCards.Count} cards, {pack.Detachments?.Count} source detachment definitions; shared adapter/merge replay passed. No database changed.");
}
else if (args.Length == 5 && args[0] == "reference-audit-input")
{
    // Offline diagnostics only: exercise the actual shared importer without opening a user database.
    var documents = BsDataDocument.ReadArchive(File.ReadAllBytes(args[1]));
    var source = DataJson.Read<SourceInfo>(File.ReadAllText(args[2]));
    var imported = BsDataImporter.ImportAll(documents, source, new Progress<string>(Console.WriteLine));
    File.WriteAllText(args[3], DataJson.Write(imported.Dataset));
    File.WriteAllText(args[4], DataJson.Write(documents.Select(d => new {
        d.Id, d.Name, d.FileName, Faction = BsDataImporter.FactionName(d),
        ArmyCatalogue = BsDataImporter.ArmyCatalogues(documents).Contains(d),
        NativeRootIds = BsDataImporter.NativeCatalogues(documents, d).Select(n => n.Id).ToArray()
    })));
    Console.WriteLine("Audit input exported; no database changed.");
}
else if (args.Length == 3 && args[0] == "audit-references")
{
    var data = DataJson.Read<Dataset>(File.ReadAllText(args[1]));
    var report = ReferenceCoverageAudit.Build(data);
    File.WriteAllText(args[2], DataJson.Write(report));
    var detachments = report.Factions.SelectMany(f => f.Detachments).ToList();
    Console.WriteLine($"{report.Factions.Count} factions; {detachments.Count} detachments; " + string.Join("; ", detachments.GroupBy(d => d.Coverage).Select(g => $"{g.Key}: {g.Count()}")) + $"; unresolved cards: {report.Unresolved.Count}");
}
else if (args.Length == 3 && args[0] == "export-reference-pack")
{
    var data = ReferenceModel.ExportDataset(DataJson.Read<Dataset>(File.ReadAllText(args[1])));
    var pack = data with { Units = [], Detachments = [], ReferenceOnly = true, SchemaVersion = 2, RulesPacks = null,
        Source = new SourceInfo("GrimList reference collection", "1", DateTime.UtcNow.ToString("yyyy-MM-dd"), "", Edition: data.Source.Edition),
        ReferenceCards = data.ReferenceCards!.Where(ReferenceModel.DisplayOnly).ToList() };
    File.WriteAllText(args[2], DataJson.Write(pack));
    Console.WriteLine($"Exported {pack.ReferenceCards.Count} display reference cards with provenance.");
}
else if (args.Length == 4 && args[0] == "preview-reference-import")
{
    var current = DataJson.Read<Dataset>(File.ReadAllText(args[1]));
    using var input = new GrimList.Platform.PickedDocument(Path.GetFileName(args[2]), File.OpenRead(args[2]));
    var references = await ReferenceSourceReader.ReadAsync(input, current.Source.Edition);
    var preview = DatasetMerge.Prepare(current, references, ImportLayer.Profiles, allowReferenceConflicts: true);
    File.WriteAllText(args[3], DataJson.Write(preview.ReferenceChanges));
    if (preview.ReferenceChanges.Any(c => c.Status == "Conflict")) throw new InvalidDataException("Conflicts found; no merged dataset exported.");
    File.WriteAllText(args[3] + ".dataset.json", DataJson.Write(ReferenceModel.ExportDataset(preview.Dataset)));
    Console.WriteLine($"Preview only: {preview.ReferenceChanges.Count} reference decisions; no database modified.");
}
else if (args.Length is 4 or 5 && args[0] is "refresh-associations" or "refresh-muster")
{
    var muster = args[0] == "refresh-muster";
    var metadata = muster ? "muster" : "association";
    using var store = WindowsServices.OpenStore(args[1]);
    var current = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var incoming = (await BsDataClient.ReadCached(args[2])).Dataset;
    var snapshot = DataJson.Write(current); var armies = DataJson.Write(store.LoadArmies());
    var rows = new List<object>();
    var updated = current with { Units = current.Units.Select(unit =>
    {
        var provenance = unit.ProfileSource ?? current.Source;
        if (unit.Profile == null || provenance.ContentHash != incoming.Source.ContentHash || provenance.Edition != incoming.Source.Edition) return unit;
        var match = incoming.Units.SingleOrDefault(candidate => candidate.Faction == unit.Faction && (candidate.Key == unit.Key
            || unit.Aliases?.Contains(candidate.Key) == true || candidate.Aliases?.Contains(unit.Key) == true || candidate.NameKey == unit.NameKey));
        if (match?.Profile == null) return unit;
        if (muster)
        {
            var rules = match.Profile.DetachmentKeywords;
            if (DataJson.Write(unit.Profile.DetachmentKeywords) == DataJson.Write(rules)) return unit;
            rows.Add(new { unit.Faction, unit.Name, Rules = rules?.Count ?? 0, Warnings = match.Profile.Warnings?.Where(w => w.StartsWith("Conditional keyword")).ToList() });
            return unit with { Profile = unit.Profile with { DetachmentKeywords = rules,
                Warnings = (unit.Profile.Warnings ?? []).Where(w => !w.StartsWith("Conditional keyword")).Concat((match.Profile.Warnings ?? []).Where(w => w.StartsWith("Conditional keyword"))).ToList() } };
        }
        if (match.Profile.Associations is not { } associations) return unit;
        if (DataJson.Write(unit.Profile.Associations) == DataJson.Write(associations)) return unit;
        rows.Add(new { unit.Faction, unit.Name, associations.SourceEntryId, Constraints = associations.Constraints.Count, associations.Warnings });
        return unit with { Profile = LeaderRules.ImportRules(unit.Profile with { Associations = associations }) };
    }).ToList() };
    updated.Validate(); Directory.CreateDirectory(args[3]);
    File.WriteAllText(Path.Combine(args[3], metadata + "-refresh.json"), DataJson.Write(rows));
    File.WriteAllText(Path.Combine(args[3], "dataset.json"), DataJson.Write(updated));
    Console.WriteLine($"{metadata} metadata refreshed for {rows.Count} units; points and other profile fields retained.");
    if (args.Length == 5)
    {
        if (args[4] != "--apply") throw new InvalidDataException("Expected --apply.");
        var backup = Path.Combine(store.DirectoryPath, "backups", "before-" + metadata + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), armies);
        store.ApplyImport(updated, snapshot);
        if (DataJson.Write(store.LoadArmies()) != armies) throw new InvalidDataException("Saved armies changed unexpectedly; inspect " + backup);
        Console.WriteLine("Applied. Backup: " + backup);
    }
}
else if (args.Length == 6 && args[0] == "inspect-attachments")
{
    using var store = WindowsServices.OpenStore(args[1]);
    var data = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var army = store.LoadArmies().Single(a => a.Id == args[2]);
    var bodyguard = army.Entries.Single(e => e.Id == args[3]);
    var proposed = args[4].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    var problem = LeaderRules.CombinationProblem(army, data, bodyguard.Id, proposed);
    var report = AssociationRules.Describe(army, data, bodyguard, proposed) + "\nComplete attachment validation: " + (problem ?? "VALID");
    Directory.CreateDirectory(args[5]);
    File.WriteAllText(Path.Combine(args[5], "runtime-attachment-diagnostic.txt"), report);
    File.WriteAllText(Path.Combine(args[5], "runtime-attachment-rules.json"), DataJson.Write(new
    {
        data.Source, Bodyguard = bodyguard, Rules = LeaderRules.Semantics(data.FindUnit(bodyguard.UnitKey)!.Profile),
        Proposed = proposed.Select(id => { var entry = army.Entries.Single(e => e.Id == id);
            return new { Entry = entry, Rules = LeaderRules.Semantics(data.FindUnit(entry.UnitKey)!.Profile) }; }),
        Evaluation = AssociationRules.Evaluate(army, data, bodyguard, proposed), Problem = problem
    }));
    Console.WriteLine(report);
}
else if (args.Length is 4 or 5 && args[0] == "audit-pricing")
{
    using var store = WindowsServices.OpenStore(args[1]);
    var current = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var imported = await BsDataClient.ReadCached(args[2]);
    var preview = PricingRepair.Preview(current, imported.Dataset);
    Directory.CreateDirectory(args[3]);
    File.WriteAllText(Path.Combine(args[3], "incoming.json"), DataJson.Write(imported.Dataset));
    File.WriteAllText(Path.Combine(args[3], "report.json"), DataJson.Write(preview.Rows));
    var defaults = preview.Rows.Where(r => r.Status == "Recovered").SelectMany(r =>
    {
        var unit = preview.Dataset.FindUnit(r.Key)!;
        return unit.Options.Where(o => o.FromCopy == 1).Select(o =>
        {
            var entry = new ArmyEntry("audit", unit.Key, unit.Name, o.Models, Loadout: Loadouts.Create(unit, o.Models));
            var result = ArmyCalculator.Calculate(ArmyList.New(unit.Faction) with { Entries = [entry] }, preview.Dataset);
            return new { unit.Faction, unit.Name, o.Models, result.Entries[0].Points, LoadoutIssues = Loadouts.Issues(unit, entry) };
        });
    }).ToList();
    File.WriteAllText(Path.Combine(args[3], "default-loadouts.json"), DataJson.Write(defaults));
    Console.WriteLine($"Recovered default prices unavailable: {defaults.Count(r => r.Points == null)}");
    Console.WriteLine($"Recovered: {preview.Recovered}; unresolved: {preview.Unresolved}; skipped Crucible: {preview.SkippedCrucible}; battle-only references: {preview.SkippedReferences}");
    foreach (var group in preview.Rows.Where(r => r.Status != "Recovered").GroupBy(r => r.Reason).OrderByDescending(g => g.Count()))
        Console.WriteLine($"{group.Count()}: {group.Key}");
    if (args.Length == 5)
    {
        if (args[4] != "--apply") throw new InvalidDataException("Expected --apply.");
        var armies = DataJson.Write(store.LoadArmies());
        Console.WriteLine("Backup: " + PricingRepairService.Apply(store, preview));
        if (armies != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed unexpectedly.");
    }
}
else if (args.Length == 2 && args[0] == "inspect-allies")
{
    var dataset = DataJson.Read<Dataset>(File.ReadAllText(args[1])); dataset.Validate();
    foreach (var faction in dataset.Units.Select(u => u.Faction).Distinct().Order())
    {
        var army = ArmyList.New(faction);
        var library = AlliedRules.Library(army, dataset);
        var allies = library.Where(u => AlliedRules.IsAlly(army, u)).ToList();
        Console.WriteLine($"{faction}: {library.Count - allies.Count} native, {library.Count(u => AlliedRules.Category(army, u, dataset) == "Dedicated Transports")} dedicated transports; allies: "
            + string.Join(", ", allies.GroupBy(u => AlliedRules.Permission(army, u, dataset)!.Name).Select(g => $"{g.Key} {g.Count()} ({g.Count(u => u.Options.Count == 0)} prices unavailable)")));
    }
    foreach (var (faction, detachment) in new[] { ("Death Guard", "Tallyband Summoners"), ("World Eaters", "Khorne Daemonkin"),
        ("Thousand Sons", "Changehost of Deceit"), ("Emperor’s Children", "Carnival of Excess"), ("Genestealer Cults", "Brood Brother Auxilia"),
        ("Genestealer Cults", "Final Day"), ("Chaos Knights", "Iconoclast Fiefdom"), ("Imperial Knights", "Questor Forgepact"),
        ("Drukhari", "Reaper’s Wager"), ("Chaos Daemons", "Shadow Legion") })
    {
        var army = ArmyList.New(faction) with { Detachments = [detachment] };
        var allies = AlliedRules.Library(army, dataset).Where(u => AlliedRules.Permission(army, u, dataset)?.Name.Replace('’', '\'') == detachment.Replace('’', '\'')).ToList();
        Console.WriteLine($"{faction} / {detachment}: {allies.Count} unlocked allies ({allies.Count(u => u.Options.Count == 0)} prices unavailable)");
    }
}
else if (args.Length is 3 or 4 && args[0] == "import-bsdata-all")
{
    var edition = args[2];
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset(); var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    Dataset incoming; List<string> warnings;
    if (args.Length == 4)
    {
        var bytes = File.ReadAllBytes(args[3]);
        var source = new SourceInfo("BSData · All armies (local)", "Local repository snapshot", "",
            $"https://github.com/BSData/wh40k-{edition}e", Edition: edition,
            ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var parsed = BsDataImporter.ImportAll(BsDataDocument.ReadArchive(bytes), source, new Progress<string>(Console.WriteLine));
        incoming = parsed.Dataset; warnings = parsed.Warnings;
    }
    else
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var downloaded = await new BsDataClient(client).Download(edition, Path.Combine(store.DirectoryPath, "sources"), new Progress<string>(Console.WriteLine), cancellation.Token);
        incoming = downloaded.Dataset; warnings = downloaded.Warnings;
    }
    var preview = store.PreviewImport(incoming, ImportLayer.Combined);
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-all-armies-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup);
    File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot);
    File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    File.WriteAllText(Path.Combine(backup, "import-warnings.json"), DataJson.Write(warnings.Concat(preview.Warnings).ToList()));
    store.ApplyImport(preview.Dataset, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect " + backup);
    foreach (var faction in incoming.Units.GroupBy(u => u.Faction))
        Console.WriteLine($"{faction.Key}: {faction.Count()} units, {faction.Count(u => u.Profile != null)} datasheets, {faction.Count(u => u.Options.Count == 0)} prices unavailable");
    Console.WriteLine($"Imported {incoming.Units.Select(u => u.Faction).Distinct().Count()} armies; {armies.Count} saved armies retained. Backup: {backup}");
}
else if (args.Length == 5 && args[0] == "build-mfm-detachments")
{
    var source = DataJson.Read<SourceInfo>(File.ReadAllText(args[3]));
    var text = File.ReadAllText(args[2]);
    source = source with { ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant() };
    var rules = MfmDetachmentImporter.ParseText(text, args[1], source);
    File.WriteAllText(args[4], DataJson.Write(rules));
    foreach (var d in rules.Detachments!) Console.WriteLine($"{d.Name}: {d.Cost}; {d.DispositionLabel}");
}
else if (args.Length == 4 && args[0] == "build-reference-pack")
{
    using var input = new GrimList.Platform.PickedDocument(Path.GetFileName(args[1]), File.OpenRead(args[1]));
    var result = await ReferenceSourceReader.ReadAsync(input, args[2]);
    File.WriteAllText(args[3], DataJson.Write(result));
    Console.WriteLine($"{result.ReferenceCards?.Count ?? 0} generic reference cards.");
}
else if (args.Length == 3 && args[0] == "import-rules")
{
    var rules = DataJson.Read<Dataset>(File.ReadAllText(args[2]));
    if (rules.Units.Count != 0) throw new InvalidDataException("This command only imports reference rules; unit data must be empty.");
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset() ?? throw new InvalidDataException("Import a unit library first.");
    var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    var preview = store.PreviewImport(rules, ImportLayer.Profiles).Dataset;
    if (DataJson.Write(before.Units) != DataJson.Write(preview.Units)) throw new InvalidDataException("Rules import unexpectedly changes units.");
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-rules-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    store.ApplyImport(preview, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect the backup at " + backup);
    Console.WriteLine($"Imported {rules.ReferenceCards!.Count} reference cards and {rules.Detachments!.Count} detachments. Units and {armies.Count} saved armies unchanged. Backup: {backup}");
}
else if (args.Length == 2 && args[0] == "migrate-detachment-dispositions")
{
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var updated = before with { Detachments = before.Detachments?.Select(d => d with { Dispositions = d.DispositionOptions }).ToList() };
    var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    if (snapshot == DataJson.Write(updated)) { Console.WriteLine("Detachment disposition lists are already stored."); return; }
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-disposition-lists-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    store.ApplyImport(updated, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect " + backup);
    Console.WriteLine($"Stored disposition lists for {updated.Detachments?.Count ?? 0} detachments. {armies.Count} saved armies unchanged. Backup: {backup}");
    foreach (var d in updated.Detachments ?? []) Console.WriteLine($"{d.Name}: {d.DispositionLabel}");
}
else if (args.Length == 3 && args[0] == "repair-loadout-limits")
{
    var catalogue = BsDataDocument.Parse(args[2], File.ReadAllText(args[2]));
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var updated = before with { Units = before.Units.Select(u => BsDataImporter.RepairRoleLimits(u, catalogue)).ToList() };
    var changed = before.Units.Zip(updated.Units).Where(p => DataJson.Write(p.First) != DataJson.Write(p.Second)).Select(p => p.Second.Name).ToList();
    if (changed.Count == 0) { Console.WriteLine("Loadout limits are already current."); return; }
    var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-loadout-limits-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    store.ApplyImport(updated, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect " + backup);
    Console.WriteLine($"Repaired limits: {string.Join(", ", changed)}. {armies.Count} saved armies unchanged. Backup: {backup}");
}
else if (args.Length == 3 && args[0] == "refresh-catalogue-pricing")
{
    var text = File.ReadAllText(args[2]); var catalogue = BsDataDocument.Parse(args[2], text);
    var source = new SourceInfo("BSData Death Guard pricing", "Catalogue revision " + BsDataDocument.Text(catalogue.Root, "revision"), "",
        "https://github.com/BSData/wh40k-11e/blob/main/Chaos%20-%20Death%20Guard.json", Edition: "11",
        ContentHash: Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset() ?? throw new InvalidDataException("Import a library first.");
    var pricing = BsDataImporter.Reprice(before, catalogue, source);
    var byId = pricing.Dataset.Units.Select(u =>
    {
        var existing = before.FindUnit(u.Key)!;
        // This repair adds missing pricing support; retain already imported price bands and copy taxes.
        return existing.Options.Count == 0 ? u : u with { Options = existing.Options, PointsSource = existing.PointsSource, PointsWarning = existing.PointsWarning };
    }).ToDictionary(u => u.Key);
    var updated = before with { Units = before.Units.Select(u => byId.GetValueOrDefault(u.Key, u)).ToList() };
    var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-wargear-pricing-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    store.ApplyImport(updated, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect " + backup);
    Console.WriteLine($"Refreshed {byId.Count} unit prices and equipment costs; {armies.Count} saved armies unchanged. Backup: {backup}");
    foreach (var unit in pricing.Dataset.Units.Where(u => u.WargearCosts!.Any(c => c.Points != 0)))
        Console.WriteLine(unit.Name + ": " + unit.Summary + "; " + string.Join(", ", unit.WargearCosts!.Where(c => c.Points != 0).Select(c => c.OptionId + "=" + c.Points)));
    foreach (var warning in pricing.Warnings) Console.WriteLine("Unchanged: " + warning);
}
else if (args.Length == 3 && args[0] == "repair-missing-points")
{
    var incoming = DataJson.Read<Dataset>(File.ReadAllText(args[2])); incoming.Validate();
    using var store = WindowsServices.OpenStore(args[1]);
    var before = store.LoadDataset() ?? throw new InvalidDataException("Import a unit library first.");
    var replacements = new Dictionary<string, UnitDefinition>();
    foreach (var unit in incoming.Units)
    {
        var existing = before.Units.SingleOrDefault(u => u.NameKey == unit.NameKey)
            ?? throw new InvalidDataException("Unknown unit: " + unit.Name);
        if (existing.Options.Count != 0 || unit.Options.Count == 0 || unit.PointsWarning.Length > 0)
            throw new InvalidDataException("Only missing points can be repaired: " + unit.Name);
        replacements.Add(existing.Key, existing with { Options = unit.Options, PointsSource = unit.PointsSource ?? incoming.Source, PointsWarning = "" });
    }
    var updated = before with { Units = before.Units.Select(u => replacements.GetValueOrDefault(u.Key, u)).ToList() }; updated.Validate();
    var snapshot = DataJson.Write(before); var armies = store.LoadArmies();
    var backup = Path.Combine(store.DirectoryPath, "backups", "before-points-repair-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
    Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "dataset.json"), snapshot); File.WriteAllText(Path.Combine(backup, "armies.json"), DataJson.Write(armies));
    store.ApplyImport(updated, snapshot);
    if (DataJson.Write(armies) != DataJson.Write(store.LoadArmies())) throw new InvalidDataException("Saved armies changed; inspect " + backup);
    foreach (var unit in replacements.Values) Console.WriteLine(unit.Name + ": " + unit.Summary);
    Console.WriteLine($"Repaired {replacements.Count} units; {armies.Count} saved armies unchanged. Backup: {backup}");
}
else { Console.Error.WriteLine("Usage: inspect-allies <dataset.json> OR build-reference-pack <source.json> <edition> <output.json> OR build-mfm-detachments <faction> <table.txt> <source.json> <output.json> OR migrate-detachment-dispositions <data-directory> OR import-rules <data-directory> <rules.json> OR repair-missing-points <data-directory> <points.json> OR refresh-catalogue-pricing <data-directory> <catalogue.json>"); Environment.ExitCode = 1; }
