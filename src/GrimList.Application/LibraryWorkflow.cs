using System.Security.Cryptography;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public record ArmyImpact(string Name, ArmyResult Before, ArmyResult After, int Limit);
public record LibraryPreview(Dataset Dataset, string BaseSnapshot, List<PointChange> Prices,
    List<string> Changes, List<string> Warnings, List<ArmyImpact> Armies)
{
    public string? BaseRevision { get; init; }
    public StratagemImportSummary? Stratagems { get; init; }
    public List<ReferenceChange> ReferenceChanges { get; init; } = [];
    public SourceInfo? IncomingSource { get; init; }
    public bool CanApply => ReferenceChanges.All(c => c.Status != "Conflict");
}

/// <summary>One import/preview/apply pipeline for desktop, mobile and service tests.</summary>
public sealed class LibraryWorkflow(IArmyRepository repository, IPdfTextExtractor? pdfExtractor = null, string libraryVariant = "")
{
    // Complete exported libraries exceed 50 MB. Keep a bounded input allowance
    // large enough for the same all-army dataset exported by the desktop app.
    public const int MaximumDocumentBytes = 128_000_000;
    public ImportPreview Parse(string text, SourceInfo source, ImportLayer layer, string? fallbackFaction = null, bool useDocumentSource = false)
    {
        var json = text.TrimStart().StartsWith('{');
        if (!json && layer != ImportLayer.Points) throw new InvalidDataException("Datasheets use structured JSON. PDF/text extraction currently supports MFM points only.");
        var parsed = json ? new ImportPreview(DataJson.Read<Dataset>(text), []) : MfmImporter.ParseText(text, source, fallbackFaction);
        if (json && !useDocumentSource) parsed = parsed with { Dataset = parsed.Dataset with { Source = source } };
        if (json && useDocumentSource) source = parsed.Dataset.Source;
        if (json && parsed.Dataset.RulesPacks is not { Count: > 0 } && parsed.Dataset.ReferenceOnly && (parsed.Dataset.ReferenceCards ?? []).All(ReferenceModel.DisplayOnly))
            parsed = new(new StructuredJsonReferenceAdapter().Read("references.json", text, source.Edition), []);
        if (layer == ImportLayer.Profiles && parsed.Dataset.Units.Count > 0 && parsed.Dataset.Units.All(u => u.Profile == null))
            throw new InvalidDataException("This file contains only points. Download BSData or choose the original catalogue ZIP; a points-only cache cannot supply datasheets.");
        return parsed;
    }

    public Task<ImportPreview> ReadBsDataAsync(byte[] archive, string edition, IProgress<string>? progress = null,
        CancellationToken cancellation = default) => Task.Run(() => ReadBsData(BsDataDocument.ReadArchive(archive), archive, edition, progress, cancellation), cancellation);

    public Task<ImportPreview> ReadBsDataAsync(List<BsDataDocument> documents, byte[] bytes, string edition,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
        => Task.Run(() => ReadBsData(documents, bytes, edition, progress, cancellation), cancellation);

    private static ImportPreview ReadBsData(List<BsDataDocument> documents, byte[] bytes, string edition,
        IProgress<string>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (edition is not ("10" or "11")) throw new InvalidDataException("Select BSData edition 10 or 11 explicitly.");
        var catalogues = BsDataImporter.ArmyCatalogues(documents);
        var primary = catalogues.FirstOrDefault() ?? throw new InvalidDataException("Choose an army catalogue or the complete repository ZIP.");
        if (catalogues.Any(c => (Path.GetExtension(c.FileName).Equals(".cat", StringComparison.OrdinalIgnoreCase) && edition != "10")
            || (Path.GetExtension(c.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase) && edition != "11")))
            throw new InvalidDataException("The BSData 10e repository uses CAT files and 11e uses JSON. Select the matching edition above, then choose the file again.");
        var source = new SourceInfo(catalogues.Count == 1 ? $"BSData · {BsDataImporter.FactionName(primary)} (local)" : "BSData · All armies (local)",
            catalogues.Count == 1 ? "Catalogue revision " + BsDataDocument.Text(primary.Root, "revision") : "Local repository snapshot", "",
            $"https://github.com/BSData/wh40k-{edition}e", Edition: edition, ContentHash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var imported = BsDataImporter.ImportAll(documents, source, progress, cancellation);
        return imported with { Warnings = imported.Warnings.Append("Local catalogue: commit date unknown. Confirm the selected edition matches the repository you saved.").ToList() };
    }

    // Streams support Android's Storage Access Framework as well as ordinary desktop files.
    public async Task<ImportPreview> ReadDocumentAsync(PickedDocument document, string edition,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var extension = Path.GetExtension(document.Name).ToLowerInvariant();
        if (extension is not (".zip" or ".json" or ".cat" or ".gst"))
            throw new InvalidDataException("Choose GrimList JSON or a BSData ZIP/catalogue. PDF/text files use a separate import workflow.");
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024]; int count;
        while ((count = await document.Content.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumDocumentBytes) throw new InvalidDataException("Choose a source no larger than 128 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellation).ConfigureAwait(false);
        }
        var bytes = buffer.ToArray();
        if (Path.GetExtension(document.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return await ReadBsDataAsync(bytes, edition, progress, cancellation).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            if (Path.GetExtension(document.Name).Equals(".cat", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(document.Name).Equals(".gst", StringComparison.OrdinalIgnoreCase)
                || System.Text.Json.Nodes.JsonNode.Parse(text)?["catalogue"] != null)
                return ReadBsData([BsDataDocument.Parse(document.Name, text)], bytes, edition, progress, cancellation);
            var dataset = DataJson.Read<Dataset>(text);
            if (dataset.RulesPacks is not { Count: > 0 } && dataset.ReferenceOnly && (dataset.ReferenceCards ?? []).All(ReferenceModel.DisplayOnly))
                dataset = new StructuredJsonReferenceAdapter().Read(document.Name, text, edition);
            dataset.Validate();
            return new ImportPreview(dataset, []);
        }, cancellation).ConfigureAwait(false);
    }

    public async Task<MfmDownload> DownloadAsync(bool community, string edition, string? faction,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        var cache = Path.Combine(repository.DirectoryPath, "sources");
        // Also moves synchronous source-discovery/parsing work off the caller's UI thread.
        return await Task.Run(() => new LibraryDownloadClient(client, pdfExtractor)
            .Download(community, edition, faction, cache, progress, cancellation), cancellation).ConfigureAwait(false);
    }

    public LibraryPreview Prepare(Dataset incoming, ImportLayer layer, bool completeFactions = false, IEnumerable<string>? warnings = null,
        IReadOnlyDictionary<string, string>? preferredReferenceSources = null, IProgress<string>? progress = null,
        CancellationToken cancellation = default)
    {
        // Recover source-backed upgrade classification before preview/merge, so JSON
        // imports and first-run installation do not install already-disabled choices
        // and require a second import when the editing session opens.
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Preparing changes · Reading the installed library…");
        incoming = EnhancementLibraryRepair.ApplyBundled(incoming);
        incoming = ArmyRuleLibraryRecovery.ApplyBundled(incoming);
        var loaded = repository.LoadLibrary();
        var current = loaded.Dataset;
        var snapshot = loaded.Revision == null ? DataJson.Write(current) : "";
        if (current != null && layer == ImportLayer.Points && MfmPoints.IsMfm(incoming.Source))
        {
            // A prices-only source must not erase catalogue formulae, paid equipment,
            // allied costs or units absent from its coverage. Match against the library.
            var overlay = MfmPoints.Overlay(current, incoming);
            incoming = overlay.Dataset with { Source = incoming.Source };
            warnings = (warnings ?? []).Concat(overlay.Warnings).ToList();
            completeFactions = false;
        }
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Preparing changes · Merging points, datasheets and rules…");
        var merged = DatasetMerge.Prepare(current, incoming, layer, completeFactions, allowReferenceConflicts: true, preferredReferenceSources: preferredReferenceSources);
        if (layer != ImportLayer.Points && merged.Dataset.Source.Edition == "11" && merged.Dataset.ReferenceCoverage is { Count: > 0 })
        {
            var compatible = ReferenceRevisionRecovery.CheckLegacy(merged.Dataset, ReferenceLibraryRecovery.ReadBundled());
            merged = merged with { Dataset = compatible, Warnings = merged.Warnings.Concat((compatible.ReferenceCoverage ?? [])
                .Select(c => c.ReviewReason).OfType<string>()).Distinct().ToList() };
        }
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Preparing changes · Comparing units and rules…");
        var prices = DatasetChanges.Compare(current, merged.Dataset);
        var changes = prices.Select(c => c.Description).ToList();
        foreach (var pack in merged.Dataset.RulesPacks ?? [])
            if ((current?.RulesPacks ?? []).All(p => p.Id != pack.Id || p.Revision != pack.Revision))
                changes.Add($"Rules pack installed: {pack.Source.Name}, revision {pack.Revision} ({pack.Status}; {pack.Coverage} coverage). Overrides remain active on later catalogue updates.");
        foreach (var unit in merged.Dataset.Units)
        {
            cancellation.ThrowIfCancellationRequested();
            var previous = current?.FindUnit(unit.Key) ?? current?.FindUnit(unit.NameKey);
            if (DataJson.Write(previous?.Profile) != DataJson.Write(unit.Profile)) changes.Add($"{unit.Faction} / {unit.Name}: datasheet updated ({unit.Profile?.Weapons?.Count ?? 0} weapon profiles).");
            if (previous?.Category != unit.Category) changes.Add($"{unit.Name}: category → {unit.DisplayCategory}.");
            if (previous != null && previous.Name != unit.Name) changes.Add($"Unit renamed: {previous.Name} → {unit.Name}; saved roster entries retained.");
        }
        foreach (var detachment in merged.Dataset.Detachments ?? [])
        {
            var renamed = current?.Detachments?.FirstOrDefault(d => d.Faction == detachment.Faction && d.Id.Length > 0 && d.Id == detachment.Id && d.Name != detachment.Name);
            if (renamed != null) changes.Add($"{detachment.Faction}: detachment renamed {renamed.Name} → {detachment.Name}; saved selections retain their source identity.");
            var previous = current?.Detachments?.FirstOrDefault(d => UnitIdentity.Normalize(d.Faction) == UnitIdentity.Normalize(detachment.Faction) && UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(detachment.Name));
            if (previous == null || previous.Cost != detachment.Cost || !previous.DispositionOptions.SequenceEqual(detachment.DispositionOptions))
                changes.Add($"{detachment.Faction} / {detachment.Name}: {(previous == null ? "detachment added" : "detachment updated")} · Cost: {detachment.Cost} · Dispositions: {detachment.DispositionLabel}.");
        }
        static string Key(ReferenceCard c) => string.Join("|", new[] { c.Faction, c.Section, c.Detachment, c.Name }.Select(UnitIdentity.Normalize));
        var cards = (current?.ReferenceCards ?? []).GroupBy(Key).ToDictionary(g => g.Key, g => g.Last());
        foreach (var card in merged.Dataset.ReferenceCards ?? [])
        {
            cards.TryGetValue(Key(card), out var previous);
            if (previous == null || previous.Text != card.Text || previous.Cost != card.Cost || previous.IsUpgrade != card.IsUpgrade)
                changes.Add($"{card.Faction} / {card.Detachment} / {card.Section}: {card.Name} {(previous == null ? "added" : "updated")}.");
            if (previous?.Details?.Status != card.Details?.Status && card.Details?.Status == "Stale")
                changes.Add($"{card.Faction} / {card.Detachment}: {card.Name} retained as a previous reference; current rules need verification.");
        }
        var retainedCards = (merged.Dataset.ReferenceCards ?? []).Select(Key).ToHashSet();
        foreach (var card in cards.Values.Where(c => !retainedCards.Contains(Key(c))))
            changes.Add($"{card.Faction} / {card.Detachment} / {card.Section}: {card.Name} removed by this update.");
        foreach (var detachment in current?.Detachments ?? [])
            if (!(merged.Dataset.Detachments ?? []).Any(d => UnitIdentity.Normalize(d.Faction) == UnitIdentity.Normalize(detachment.Faction)
                && (detachment.Id.Length > 0 && d.Id == detachment.Id || UnitIdentity.Normalize(d.Name) == UnitIdentity.Normalize(detachment.Name))))
                changes.Add($"{detachment.Faction} / {detachment.Name}: detachment {(DetachmentLifecycle.IsObsolete(merged.Dataset, detachment.Faction, detachment.Name) ? "marked obsolete" : "removed")}; existing army selections will be reported for review.");
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Preparing changes · Checking saved armies…");
        var impacts = repository.LoadArmies().Select(a => { cancellation.ThrowIfCancellationRequested(); return new ArmyImpact(a.Name, ArmyCalculator.Calculate(a, current), ArmyCalculator.Calculate(a, merged.Dataset), a.Limit); })
            .Where(a => a.Before.Total != a.After.Total || !a.Before.Issues.SequenceEqual(a.After.Issues)).ToList();
        var unitWarnings = incoming.Units.SelectMany(u => (u.Profile?.Warnings ?? []).Select(w => u.Name + ": " + w)
            .Concat(u.PointsWarning.Length > 0 ? [u.Name + ": " + u.PointsWarning] : [])
            .Concat(u.AlliedPointsWarning.Length > 0 ? [u.Name + ": " + u.AlliedPointsWarning] : []));
        var stratagems = StratagemImportSummary.Create(incoming, merged.Dataset, layer);
        return new(merged.Dataset, snapshot, prices, changes, (warnings ?? []).Concat(merged.Warnings).Concat(unitWarnings)
            .Concat(stratagems.Warnings).Concat(merged.ReferenceChanges.Where(c => c.Status is "Conflict" or "Unresolved")
                .Select(c => $"{c.Faction} / {c.Detachment} / {c.Name}: {c.Status}. {c.Message}")).Distinct().ToList(), impacts)
            { BaseRevision = loaded.Revision, Stratagems = stratagems, ReferenceChanges = merged.ReferenceChanges, IncomingSource = incoming.Source };
    }
    public Task<LibraryPreview> PrepareAsync(Dataset incoming, ImportLayer layer, bool completeFactions = false,
        IEnumerable<string>? warnings = null, CancellationToken cancellation = default, IProgress<string>? progress = null) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested(); var preview = Prepare(incoming, layer, completeFactions, warnings, progress: progress, cancellation: cancellation);
        cancellation.ThrowIfCancellationRequested(); return preview;
    }, cancellation);

    public void Apply(LibraryPreview preview)
    {
        if (!preview.CanApply) throw new InvalidDataException("Resolve reference source conflicts and preview again before applying.");
        IDisposable? lease = null;
        try
        {
            repository.ApplyPreparedImport(preview.Dataset, preview.BaseSnapshot.Length == 0 ? null : preview.BaseSnapshot, preview.BaseRevision,
                loaded => lease = ImportProjection.Prepare(repository, loaded, libraryVariant));
        }
        finally { lease?.Dispose(); }
    }
    public async Task<LibraryPreview> PrepareReferenceImportAsync(PickedDocument document, CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? preferredSources = null)
    {
        var current = repository.LoadDataset() ?? throw new InvalidDataException("Import an army-building library first.");
        var references = await ReferenceSourceReader.ReadAsync(document, current.Source.Edition, cancellation).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var preview = Prepare(references, ImportLayer.Profiles, preferredReferenceSources: preferredSources);
            cancellation.ThrowIfCancellationRequested(); return preview;
        }, cancellation).ConfigureAwait(false);
    }
    public Task ApplyReferenceImportAsync(LibraryPreview preview, CancellationToken cancellation = default) => ApplyAsync(preview, cancellation);
    public Task ApplyAsync(LibraryPreview preview, CancellationToken cancellation = default) => Task.Run(() =>
    { cancellation.ThrowIfCancellationRequested(); Apply(preview); }, cancellation);
    public List<PricingRepairRow> ScanPrices() => repository.LoadDataset() is { } data ? PricingRepair.Scan(data) : [];
    public Task<PricingRepairPreview> PreviewRepairAsync(Dataset incoming, CancellationToken cancellation = default) => Task.Run(() =>
        PricingRepair.Preview(repository.LoadDataset() ?? throw new InvalidDataException("Import a library first."), incoming, cancellation), cancellation);
    public string ApplyRepair(PricingRepairPreview preview) => PricingRepairService.Apply(repository, preview);
}
