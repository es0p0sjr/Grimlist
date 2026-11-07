using System.IO.Compression;
using GrimList.Core;

namespace GrimList.Application;

// Compact proofs generated from the exact catalogue used to match the shipped
// references. No faction names, card text or observation-date guesses in recovery.
public record ReferenceCatalogueBaseline(string CoverageKey, string ReferenceHash, string CatalogueHash, string Fingerprint)
{
    private static readonly Lazy<List<ReferenceCatalogueBaseline>> bundled = new(() =>
    {
        using var input = typeof(ReferenceCatalogueBaseline).Assembly.GetManifestResourceStream("GrimList.Application.Resources.reference-baselines-11.json.gz");
        if (input == null) return [];
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return DataJson.Read<List<ReferenceCatalogueBaseline>>(reader.ReadToEnd());
    });
    public static IReadOnlyList<ReferenceCatalogueBaseline> Bundled => bundled.Value;
    public static string Key(ReferenceCoverage c) => System.Text.Json.JsonSerializer.Serialize(new[] {
        c.SourceId, c.Edition, ReferenceModel.Key(c.Category), ReferenceModel.Key(c.Faction), ReferenceModel.Key(c.Detachment) });

    public static List<ReferenceCatalogueBaseline> Create(Dataset catalogue, Dataset references)
    {
        catalogue.Validate(); references.Validate();
        if (catalogue.Source.Edition != references.Source.Edition || catalogue.Source.ContentHash.Length != 64)
            throw new InvalidDataException("A pinned matching catalogue is required for reference baselines.");
        var definitions = (references.Detachments ?? []).Where(d => d.Source?.ContentHash == catalogue.Source.ContentHash).ToList();
        var matched = catalogue with { Detachments = definitions, ReferenceCoverage = [] };
        return (references.ReferenceCoverage ?? []).Where(c => c.Source?.ContentHash is { Length: > 0 }
            && definitions.Count(d => d.Faction == c.Faction && d.Name == c.Detachment) == 1)
            .Select(c => new ReferenceCatalogueBaseline(Key(c), c.Source!.ContentHash,
                catalogue.Source.ContentHash, ReferenceCatalogueCompatibility.Fingerprint(matched, c.Faction, c.Detachment))).ToList();
    }
}
