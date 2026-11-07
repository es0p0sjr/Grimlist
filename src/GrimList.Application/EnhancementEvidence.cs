using GrimList.Core;
using static GrimList.Application.EnhancementLibraryRepair;

namespace GrimList.Application;

// A source content hash pins the exact revision, including legacy packs whose Version
// is only a metadata format label. Never match across hashes, editions or owners.
public sealed class EnhancementEvidence
{
    private readonly Dictionary<string, List<RecoveryCard>> scopes;
    public EnhancementEvidence(IEnumerable<RecoveryPack> packs) => scopes = packs
        .Where(p => p.Source.ContentHash.Length > 0)
        .SelectMany(p => p.Cards.Select(c => (Key: Scope(p.Source, c.Faction, c.Detachment), Card: c)))
        .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Card).ToList());

    private static string Scope(SourceInfo source, string faction, string detachment) => DataJson.Write(new[] {
        source.Edition, source.ContentHash.ToUpperInvariant(), UnitIdentity.Normalize(faction), UnitIdentity.Normalize(detachment) });
    private static string Name(string name) => Enhancements.Key(new EnhancementSelection("", name));

    public (RecoveryCard? Card, string? Problem) Match(ReferenceCard card)
    {
        if (card.Source == null || card.Source.ContentHash.Length == 0
            || !scopes.TryGetValue(Scope(card.Source, card.Faction, card.Detachment), out var scope)) return (null, null);
        var id = card.Details?.SourceCardId ?? "";
        // An explicit source ID must never fall back to a different entry with the same name.
        var candidates = scope.Where(c => id.Length > 0 ? c.SourceEntryId == id : Name(c.Name) == Name(card.Name)).ToList();
        if (candidates.Count == 0) return (null, "No matching source identity in the pinned evidence.");
        if (id.Length == 0 && candidates.Select(c => c.SourceEntryId).Distinct().Count() > 1)
            return (null, "Ambiguous legacy name: multiple source identities.");
        if (candidates.Select(c => (Eligibility: Signature(c.Eligibility), c.IsUpgrade)).Distinct().Count() != 1)
            return (null, "Conflicting source eligibility or classification.");
        return (candidates[0], null);
    }

    public static bool Known(EnhancementCondition? c)
    {
        if (c == null) return false;
        if (c.Kind is "and" or "or") return (c.Children ?? []).All(Known);
        if (c.Kind == "not") return c.Children is { Count: 1 } && Known(c.Children[0]);
        if ((c.Children?.Count ?? 0) != 0 || c.Comparison is not ("equalTo" or "notEqualTo" or "lessThan" or "greaterThan"
            or "atLeast" or "atMost" or "instanceOf" or "notInstanceOf")) return false;
        return c.Kind == "constant" ? int.TryParse(c.Value, out _) : c.Kind is "keyword" or "unit" or "detachment" && c.Value.Length > 0;
    }
    public static bool Same(EnhancementCondition? a, EnhancementCondition? b) => Signature(a) == Signature(b);
    private static string Signature(EnhancementCondition? c)
    {
        if (c == null) return "unknown";
        var children = (c.Children ?? []).Select(Signature);
        if (c.Kind is "and" or "or") children = children.Order(StringComparer.Ordinal);
        // Encode scalar fields once. Serializing already-serialized child strings at
        // every depth would repeatedly escape them and grow exponentially.
        return DataJson.Write(new[] { c.Kind, c.Kind is "keyword" or "detachment" ? UnitIdentity.Normalize(c.Value) : c.Value,
            c.Comparison, c.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            + "(" + string.Join(",", children) + ")";
    }

    // Build-time audit, independent of runtime repair. Unknown source conditions are
    // reported separately; they cannot be converted into permissive eligibility.
    public static List<string> ValidateCandidate(Dataset data, IEnumerable<RecoveryPack> evidence, out int unsupported)
    {
        var packs = evidence.ToList(); var index = new EnhancementEvidence(packs);
        var errors = new List<string>(); unsupported = 0;
        var cards = (data.ReferenceCards ?? []).Where(c => c.Section == "Enhancements").ToList();
        foreach (var duplicate in cards.GroupBy(c => (Faction: UnitIdentity.Normalize(c.Faction), Key: Enhancements.Key(c))).Where(g => g.Count() > 1))
            errors.Add($"{duplicate.First().Faction}/{duplicate.First().Detachment}/{duplicate.First().Name}: Duplicate selectable enhancement identity.");
        foreach (var card in cards)
        {
            var match = index.Match(card);
            var label = $"{card.Faction}/{card.Detachment}/{card.Name}";
            if (match.Problem != null) { errors.Add(label + ": " + match.Problem); continue; }
            if (match.Card == null)
            {
                if (card.Source?.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) == true)
                    errors.Add(label + ": Missing pinned source evidence.");
                else unsupported++; // Text-only reference sources have no structured source evidence.
                continue;
            }
            if (!Known(match.Card.Eligibility))
            {
                if (Known(card.Eligibility)) errors.Add(label + ": Candidate grants eligibility unsupported by its source.");
                unsupported++; continue;
            }
            if (!Known(card.Eligibility)) errors.Add(label + ": Recoverable source eligibility is missing from the candidate.");
            else if (!Same(card.Eligibility, match.Card.Eligibility)) errors.Add(label + ": Candidate eligibility conflicts with its source.");
            if (card.IsUpgrade != match.Card.IsUpgrade) errors.Add(label + ": Candidate classification conflicts with its source.");
        }
        // Also catch a deleted card in a published source/faction/detachment scope.
        var published = cards.Where(c => c.Source != null).GroupBy(c => Scope(c.Source!, c.Faction, c.Detachment))
            .ToDictionary(g => g.Key, g => g.ToList());
        foreach (var pack in packs)
            foreach (var expected in pack.Cards.Where(c => Known(c.Eligibility)))
                if (published.TryGetValue(Scope(pack.Source, expected.Faction, expected.Detachment), out var scope)
                    && !scope.Any(c => (c.Details?.SourceCardId ?? "").Length > 0 ? c.Details!.SourceCardId == expected.SourceEntryId : Name(c.Name) == Name(expected.Name)))
                    errors.Add($"{expected.Faction}/{expected.Detachment}/{expected.Name}: Source enhancement is missing from the candidate.");
        return errors.Distinct().ToList();
    }
}
