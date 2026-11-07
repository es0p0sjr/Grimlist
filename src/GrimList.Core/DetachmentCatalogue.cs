using System.IO.Compression;
using System.Text.Json;

namespace GrimList.Core;

// A dated, explicit retirement list, not deletion inferred from an incomplete import.
// The supplement only fills missing definitions for known catalogue contexts.
public static class DetachmentCatalogue
{
    private sealed record Repair(string Game, string Edition, List<string> Factions, List<string> Retired,
        List<DetachmentDefinition> Detachments, List<ReferenceCard> Cards, List<FactionContext> Contexts, List<string> LegacySourceHashes);
    private static readonly Lazy<Repair> bundled = new(() =>
    {
        using var input = typeof(DetachmentCatalogue).Assembly.GetManifestResourceStream("GrimList.Core.Resources.detachment-catalogue-11.json.gz")!;
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Repair>(gzip, DataJson.Options)!;
    });
    private static string Key(string value) => UnitIdentity.Normalize(value);
    private static readonly Lazy<HashSet<string>> factions = new(() => bundled.Value.Factions.Select(Key).ToHashSet());
    private static readonly Lazy<HashSet<string>> retired = new(() => bundled.Value.Retired.Select(Key).ToHashSet());
    public static bool IsRetired(Dataset? data, string faction, string name) => data?.Game == "Warhammer 40,000"
        && !(data.DetachmentSources ?? []).Any(s => Key(s.Faction) == Key(faction))
        && data.Source.Edition == "11" && factions.Value.Contains(Key(faction)) && retired.Value.Contains(Key(name));

    public static Dataset Apply(Dataset data)
    {
        data = DetachmentLifecycle.Apply(data);
        if (data.Game != "Warhammer 40,000" || data.Source.Edition != "11") return data;
        bool Removed(string faction, string name) => !(data.DetachmentSources ?? []).Any(s => Key(s.Faction) == Key(faction))
            && factions.Value.Contains(Key(faction)) && retired.Value.Contains(Key(name));
        var definitions = (data.Detachments ?? []).Where(d => !Removed(d.Faction, d.Name)).ToList();
        var cards = new List<ReferenceCard>();
        foreach (var card in data.ReferenceCards ?? [])
        {
            // Split shared applicability without deleting the surviving scopes.
            if (card.Details?.Detachments is { Count: > 0 } targets)
            {
                var kept = targets.Where(t => !Removed(card.Faction, t.Name)).ToList();
                if (kept.Count == 0) continue;
                if (kept.Count != targets.Count)
                {
                    cards.Add(ReferenceModel.Canonical(card with { Detachment = kept.Count == 1 ? kept[0].Name : "",
                        Details = card.Details with { Detachments = kept } }, data.Source));
                    continue;
                }
            }
            else if (Removed(card.Faction, card.Detachment)) continue;
            cards.Add(card);
        }
        var coverage = data.ReferenceCoverage?.Where(c => !Removed(c.Faction, c.Detachment)).ToList();
        var recoveredContexts = (data.FactionContexts ?? []).ToList();
        // Do not inject a faction library into partial/reference-only documents.
        if (data.Units.Count > 0)
        {
            // The original bootstrap predates serialized catalogue contexts. Recover
            // these only for the exact known source hashes, never by faction name alone.
            foreach (var context in bundled.Value.Contexts)
                if (!recoveredContexts.Any(c => c.Faction == context.Faction) && data.Units.Any(u => u.Faction == context.Faction)
                    && (bundled.Value.LegacySourceHashes.Contains(data.Source.ContentHash)
                        || (data.Detachments ?? []).Any(d => d.Faction == context.Faction && bundled.Value.LegacySourceHashes.Contains(d.Source?.ContentHash ?? ""))))
                    recoveredContexts.Add(context);
            var contexts = recoveredContexts.Where(c => bundled.Value.Contexts.Any(b => b.Faction == c.Faction && b.CatalogueId == c.CatalogueId))
                .Select(c => c.Faction).ToHashSet();
            var known = definitions.Select(d => (d.Faction, Key(d.Name))).ToHashSet();
            foreach (var definition in bundled.Value.Detachments.Where(d => contexts.Contains(d.Faction)))
            {
                if (!DetachmentLifecycle.Allows(data, definition)) continue;
                if ((data.RulesPacks ?? []).Any(p => RulesPacks.OwnsDetachment(p, definition))) continue;
                if (!known.Add((definition.Faction, Key(definition.Name))))
                {
                    var index = definitions.FindIndex(d => d.Faction == definition.Faction && Key(d.Name) == Key(definition.Name));
                    var previous = definitions[index];
                    // Refresh dated catalogue metadata, but never pin a newer import
                    // or a deliberate rules-pack override to this bundled snapshot.
                    if (previous.Source?.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) == true
                        && previous.Source.Date.Length > 0 && string.CompareOrdinal(previous.Source.Date, definition.Source!.Date) < 0)
                        definitions[index] = definition;
                    continue;
                }
                definitions.Add(definition);
                foreach (var card in bundled.Value.Cards.Where(c => c.Faction == definition.Faction && Key(c.Detachment) == Key(definition.Name)))
                    if (!cards.Any(c => c.Faction == card.Faction && Key(c.Detachment) == Key(card.Detachment) && c.Section == card.Section && Key(c.Name) == Key(card.Name))
                        && !(data.RulesPacks ?? []).Any(p => RulesPacks.OwnsCard(p, card))) cards.Add(card);
            }
        }
        return definitions.SequenceEqual(data.Detachments ?? []) && cards.SequenceEqual(data.ReferenceCards ?? [])
            && (coverage ?? []).SequenceEqual(data.ReferenceCoverage ?? []) && recoveredContexts.SequenceEqual(data.FactionContexts ?? []) ? data
            : data with { Detachments = definitions, ReferenceCards = cards, ReferenceCoverage = coverage, FactionContexts = recoveredContexts };
    }
}
