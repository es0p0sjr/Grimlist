namespace GrimList.Core;

// Reference completeness proves a parsed source scope, not compatibility with
// every later catalogue that happens to reuse the same detachment name or ID.
public static class ReferenceCatalogueCompatibility
{
    public const string LegacyReviewReason = "Catalogue rules changed since these references were verified. Refresh stratagem references.";
    public const string ReviewReason = "Previous references are not verified for this detachment's current rules. Use Update data to check for replacements.";
    private static readonly System.Text.Json.JsonSerializerOptions StableJson = new();
    public static string Shape(Dataset data, string faction) => "shape:" + ReferenceModel.Hash(DataJson.Write(
        (data.Detachments ?? []).Where(d => ReferenceModel.Key(d.Faction) == ReferenceModel.Key(faction))
            .Select(d => new[] { d.Id, ReferenceModel.Key(d.Name) }).OrderBy(d => d[0], StringComparer.Ordinal).ThenBy(d => d[1], StringComparer.Ordinal).ToList()));

    private static object Contents(Dataset data, string faction, string detachment, bool legacy = false) => new {
        Definitions = (data.Detachments ?? []).Where(d => ReferenceModel.Key(d.Faction) == ReferenceModel.Key(faction)
            && ReferenceModel.Key(d.Name) == ReferenceModel.Key(detachment))
            .OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => legacy
                ? (object)new { d.Id, d.FactionId, d.Name, d.Cost, d.Dispositions, d.Disposition, d.Tags, d.CannotCombineWithTags, d.Applicability, d.GroupLimits }
                : new { d.Id, d.FactionId, Name = ReferenceModel.Key(d.Name), Cost = ReferenceModel.Key(d.Cost),
                    Dispositions = d.DispositionOptions.Select(ReferenceModel.Key).Order(StringComparer.Ordinal).ToList() }).ToList(),
        Rules = (data.ReferenceCards ?? []).Where(c => c.Faction == faction && ReferenceModel.Key(c.Detachment) == ReferenceModel.Key(detachment)
            && c.Section is "Detachments" or "Detachment Rules"
            && (c.Source == null || (data.Detachments ?? []).Any(d => d.Faction == faction && ReferenceModel.Key(d.Name) == ReferenceModel.Key(detachment)
                && d.Source?.Version == c.Source.Version && d.Source.ContentHash == c.Source.ContentHash)))
            .Select(c => new { c.Section, c.Name, Text = ReferenceModel.Clean(c.Text) })
            .OrderBy(c => c.Section, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList()
    };

    public static string Fingerprint(Dataset data, string faction, string detachment) => "rules3:" + ReferenceModel.Hash(
        System.Text.Json.JsonSerializer.Serialize(Contents(data, faction, detachment), StableJson));

    public static bool Changed(ReferenceCoverage coverage, Dataset data) => coverage.CatalogueFingerprint is { Length: > 0 } fingerprint
        && (fingerprint.StartsWith("rules3:", StringComparison.Ordinal)
            ? fingerprint != Fingerprint(data, coverage.Faction, coverage.Detachment)
            : fingerprint.StartsWith("rules2:", StringComparison.Ordinal)
                ? fingerprint != "rules2:" + ReferenceModel.Hash(System.Text.Json.JsonSerializer.Serialize(Contents(data, coverage.Faction, coverage.Detachment, legacy: true), StableJson))
            : fingerprint.StartsWith("rules:", StringComparison.Ordinal)
                ? !new[] { "\n", "\r\n" }.Any(newline => fingerprint == "rules:" + ReferenceModel.Hash(System.Text.Json.JsonSerializer.Serialize(
                    Contents(data, coverage.Faction, coverage.Detachment, legacy: true), new System.Text.Json.JsonSerializerOptions(DataJson.Options) { NewLine = newline })))
                : false); // An old faction-shape stamp cannot prove which detachment changed.

    public static Dataset Apply(Dataset data)
    {
        // A faction view may omit rule cards from other scopes. Only the complete
        // library can establish that a previously verified rule disappeared.
        if (data.ReferenceOnly || data.IsScoped) return data;
        var stale = (data.ReferenceCoverage ?? []).Where(c => (Changed(c, data)
                || c.CatalogueFingerprint?.StartsWith("shape:", StringComparison.Ordinal) == true && c.ReviewReason == LegacyReviewReason)
            && !(data.RulesPacks ?? []).Any(p => RulesPacks.OwnsDetachment(p, new(c.Faction, c.Detachment)))).ToList();
        if (stale.Count == 0) return data;
        var coverage = data.ReferenceCoverage!.Select(c => stale.Contains(c)
            ? c with { State = "Partial", Mode = "Partial", Authoritative = false,
                ReviewReason = ReviewReason } : c).ToList();
        var cards = (data.ReferenceCards ?? []).Select(c => c.Details != null
            && stale.Any(s => ReferenceMerge.OwnedScope(c, s) && (c.Details.Status == "Active"
                || c.Details.Status == "Unresolved" && s.ReviewReason == LegacyReviewReason
                    && c.Details.Match is not ("Unresolved" or "Ambiguous") && c.Source?.ContentHash is { Length: > 0 } hash && hash == s.Source?.ContentHash))
            ? c with { Details = c.Details with { Status = "Stale" } } : c).ToList();
        return coverage.SequenceEqual(data.ReferenceCoverage ?? []) && cards.SequenceEqual(data.ReferenceCards ?? []) ? data
            : data with { ReferenceCoverage = coverage, ReferenceCards = cards };
    }
}
