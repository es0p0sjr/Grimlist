using System.Security.Cryptography;
using System.IO.Compression;
using GrimList.Core;
using GrimList.Platform;

namespace GrimList.Application;

public static class EnhancementLibraryRepair
{
    // Compatibility boundary for existing imports: restore missing Eligibility and
    // IsUpgrade metadata only from matching source evidence, retaining card content.
    // Preserve for attachment work; new attachment semantics do not require rebuilding
    // selections, reference cards or recovery packs. Keep older source revisions.
    // Regression coverage: SourceEnhancementChecks and UpgradeRecoveryChecks.
    // TextHash/Cost remain readable for older packs, but are never identity or eligibility evidence.
    public record RecoveryCard(string Faction, string Detachment, string Name, string TextHash, string Cost,
        EnhancementCondition? Eligibility, bool IsUpgrade = false, string SourceEntryId = "");
    public record RecoveryPack(SourceInfo Source, List<RecoveryCard> Cards);
    public static RecoveryPack Create(SourceInfo source, IEnumerable<ReferenceCard> cards) => new(source,
        cards.Where(c => c.Section == "Enhancements").Select(c => new RecoveryCard(
            c.Faction, c.Detachment, c.Name, "", "", c.Eligibility, c.IsUpgrade, c.Details?.SourceCardId ?? "")).ToList());
    private static readonly Lazy<List<RecoveryPack>> bundled = new(() =>
    {
        using var resource = typeof(EnhancementLibraryRepair).Assembly.GetManifestResourceStream("GrimList.Application.Resources.enhancement-recovery.json.gz")
            ?? throw new InvalidDataException("Missing enhancement recovery metadata.");
        using var zip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(zip);
        return DataJson.Read<List<RecoveryPack>>(reader.ReadToEnd());
    });

    // Keep every shipped source revision: transferred libraries have no raw archive.
    public static IReadOnlyList<RecoveryPack> BundledEvidence => bundled.Value;
    public static Dataset ApplyBundled(Dataset current, Action<string>? diagnostic = null)
        => ApplyEvidence(current, bundled.Value, diagnostic);

    public static Dataset Restore(IArmyRepository repository, Dataset current)
        => RestoreLibrary(repository, new LoadedLibrary(current)).Dataset!;

    public static LoadedLibrary RestoreLibrary(IArmyRepository repository, LoadedLibrary loaded)
    {
        var current = loaded.Dataset!;
        var cache = Path.Combine(repository.DirectoryPath, "sources");
        // Source archives can provide new evidence independently of a library import.
        // Keep the full path whenever any such archives may be present.
        var canCache = !Directory.Exists(cache) || !Directory.EnumerateDirectories(cache, "bsdata-*").Any();
        if (canCache && repository.HasLibraryCheck("enhancement-recovery", LibraryRecoveryVersion.Enhancements, loaded.Revision)) return loaded;
        var pending = (current.ReferenceCards ?? []).Where(c => c.Section == "Enhancements" && (c.Eligibility == null || !c.IsUpgrade)
            && c.Source?.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) == true
            && c.Source.ContentHash.Length > 0).ToList();
        if (pending.Count == 0)
        {
            if (canCache) repository.RecordLibraryCheck("enhancement-recovery", LibraryRecoveryVersion.Enhancements, loaded.Revision);
            return loaded;
        }
        var evidence = bundled.Value.ToList();
        foreach (var directory in Directory.Exists(cache) ? Directory.EnumerateDirectories(cache, "bsdata-" + current.Source.Edition + "-*") : [])
        {
            var sourcePath = Path.Combine(directory, "source.json"); var archive = Path.Combine(directory, "repository.zip");
            if (!File.Exists(sourcePath) || !File.Exists(archive)) continue;
            try
            {
                var source = DataJson.Read<SourceInfo>(File.ReadAllText(sourcePath));
                if (source.Edition != current.Source.Edition || !pending.Any(c => Matches(c.Source!, source))) continue;
                var preparedPath = Path.Combine(directory, "enhancement-semantics-v3.json");
                Dataset? prepared = null;
                if (File.Exists(preparedPath))
                {
                    try { prepared = DataJson.Read<Dataset>(File.ReadAllText(preparedPath)); }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
                }
                if (prepared == null || !Matches(prepared.Source, source))
                {
                    var bytes = File.ReadAllBytes(archive);
                    if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
                    prepared = new(source, [], ReferenceCards: BsDataImporter.ImportEnhancementReferences(BsDataDocument.ReadArchive(bytes), source), ReferenceOnly: true);
                    File.WriteAllText(preparedPath, DataJson.Write(prepared));
                }
                evidence.Add(Create(source, prepared.ReferenceCards ?? []));
            }
            // A missing/corrupt cache must not prevent the saved library from opening.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        var result = ApplyEvidence(current, evidence);
        if (!ReferenceEquals(result, current))
        {
            repository.ApplyImport(result, DataJson.Write(current));
            loaded = repository.LoadLibrary();
        }
        if (canCache) repository.RecordLibraryCheck("enhancement-recovery", LibraryRecoveryVersion.Enhancements, loaded.Revision);
        return loaded;
    }

    private static bool Matches(SourceInfo first, SourceInfo second) => first.Edition == second.Edition
        && first.ContentHash.Length > 0 && first.ContentHash.Equals(second.ContentHash, StringComparison.OrdinalIgnoreCase);

    public static Dataset Apply(Dataset current, IEnumerable<ReferenceCard> recovered, Action<string>? diagnostic = null)
        => ApplyEvidence(current, recovered.Where(c => c.Source != null).GroupBy(c => c.Source!)
            .Select(g => Create(g.Key, g)), diagnostic);

    public static Dataset ApplyEvidence(Dataset current, IEnumerable<RecoveryPack> evidence, Action<string>? diagnostic = null)
    {
        var index = new EnhancementEvidence(evidence);
        var changed = false;
        var cards = (current.ReferenceCards ?? []).Select(card =>
        {
            if (card.Section != "Enhancements") return card;
            var match = index.Match(card);
            if (match.Problem != null)
            {
                var message = $"Enhancement recovery: {card.Faction}/{card.Detachment}/{card.Name}: {match.Problem}";
                if (diagnostic != null) diagnostic(message); else System.Diagnostics.Trace.TraceWarning(message);
            }
            var source = match.Card;
            if (source?.Eligibility == null || !EnhancementEvidence.Known(source.Eligibility)) return card;
            // Explicit eligibility belongs to the installed card, including overrides.
            // Conflicting evidence must not change its classification either.
            if (card.IsUpgrade && !source.IsUpgrade || card.Eligibility != null && !EnhancementEvidence.Same(card.Eligibility, source.Eligibility))
            {
                var message = $"Enhancement recovery: {card.Faction}/{card.Detachment}/{card.Name}: installed metadata conflicts with source evidence; preserved without repair.";
                if (diagnostic != null) diagnostic(message); else System.Diagnostics.Trace.TraceWarning(message);
                return card;
            }
            if (card.Eligibility != null && (card.IsUpgrade || !source.IsUpgrade)) return card;
            changed = true;
            return card with { Eligibility = card.Eligibility ?? source.Eligibility, IsUpgrade = card.IsUpgrade || source.IsUpgrade };
        }).ToList();
        return changed ? current with { ReferenceCards = cards } : current;
    }
}
