using System.Text.Json;

namespace GrimList.Core;

// A reviewed reference snapshot, independent of BSData's army-building catalogue.
// Only known legacy source hashes are retired. Explicit overrides and later
// imports keep their authority; the snapshot never changes roster selections.
public static class SpaceMarineStratagems
{
    private sealed record Entry(string Name, string Detachment, string Cost, string Timing, string Target, string Effect, string Restrictions);
    private sealed record Supplement(SourceInfo Source, List<FactionContext> Contexts, List<string> LegacySourceHashes, List<Entry> Cards);
    private static readonly Lazy<Supplement> bundled = new(() =>
    {
        using var stream = typeof(SpaceMarineStratagems).Assembly.GetManifestResourceStream("GrimList.Core.Resources.space-marine-stratagems-11.json")!;
        return JsonSerializer.Deserialize<Supplement>(stream, DataJson.Options)!;
    });
    private static string Key(string value) => ReferenceModel.Key(value);

    // Incoming BSData normalization also supplies this fallback. That automatic
    // addition must not become an explicit import over a newer/manual reference
    // already installed in the destination library.
    public static Dataset PreserveOverrides(Dataset incoming, Dataset current)
    {
        if (incoming.Game != "Warhammer 40,000" || incoming.Source.Edition != "11") return incoming;
        var pack = bundled.Value;
        bool Snapshot(SourceInfo? s) => s?.ContentHash == pack.Source.ContentHash && s.Date == pack.Source.Date && s.Version == pack.Source.Version;
        var existing = (current.ReferenceCards ?? []).Where(c => ReferenceModel.Category(c.Section) == "Stratagems")
            .Select(c => ReferenceModel.Canonical(c, current.Source)).GroupBy(ReferenceModel.LogicalKey)
            .ToDictionary(g => g.Key, g => g.ToList());
        var skippedScopes = new HashSet<(string, string)>();
        var preserved = new HashSet<ReferenceCoverage>();
        bool Keep(ReferenceCard c)
        {
            if (!Snapshot(c.Source) || c.Details?.Status != "Active") return true;
            var overridden = existing.GetValueOrDefault(ReferenceModel.LogicalKey(c))?.Any(p =>
                !Snapshot(p.Source) || p.Details?.Status != "Active") == true;
            var complete = (current.ReferenceCoverage ?? []).Where(s => s.SourceId == c.Details.SourceId
                && s.Authoritative && s.State is "Complete" or "VerifiedComplete" && ReferenceMerge.OwnedScope(c, s)
                && string.CompareOrdinal(s.Source?.Date ?? "", pack.Source.Date) >= 0
                && !Snapshot(s.Source)).ToList();
            if (!overridden && complete.Count == 0) return true;
            preserved.UnionWith(complete);
            skippedScopes.Add((c.Faction, c.Detachment));
            return false;
        }
        var cards = (incoming.ReferenceCards ?? []).Where(Keep).ToList();
        if (skippedScopes.Count == 0) return incoming;
        // Carry a preserved complete scope through ReferenceMerge as a whole;
        // otherwise its normal absence handling would downgrade or remove it.
        foreach (var c in existing.Values.SelectMany(c => c).Where(c => preserved.Any(s => ReferenceMerge.OwnedScope(c, s))))
            if (!cards.Any(n => n.Details?.SourceId == c.Details!.SourceId && n.Details.Id == c.Details.Id)) cards.Add(c);
        var coverage = (incoming.ReferenceCoverage ?? []).Where(s =>
            (!Snapshot(s.Source) || !skippedScopes.Contains((s.Faction, s.Detachment)))
            && !preserved.Any(p => ReferenceMerge.CoverageKey(p) == ReferenceMerge.CoverageKey(s))).Concat(preserved).ToList();
        return incoming with { ReferenceCards = cards, ReferenceCoverage = coverage };
    }

    public static Dataset Apply(Dataset data)
    {
        if (data.Game != "Warhammer 40,000" || data.Source.Edition != "11") return data;
        var pack = bundled.Value;
        var factions = pack.Contexts.Select(c => c.Faction).ToHashSet();
        bool Owned(ReferenceCard c) => (data.RulesPacks ?? []).Any(p => RulesPacks.OwnsCard(p, c));
        bool Legacy(SourceInfo? source) => source != null && source.Edition == "11" && pack.LegacySourceHashes.Contains(source.ContentHash);
        // Also scrub reference-only imports and Android's old supplement before
        // source-conflict handling, without injecting a unit/faction library.
        var cards = (data.ReferenceCards ?? []).Where(c => !(factions.Contains(c.Faction)
            && ReferenceModel.Category(c.Section) == "Stratagems" && Legacy(c.Source ?? data.Source)
            && c.Details?.Status is not ("Removed" or "Obsolete") && !Owned(c))).ToList();
        var coverage = (data.ReferenceCoverage ?? []).Where(c => !(factions.Contains(c.Faction)
            && c.Category == "Stratagems" && Legacy(c.Source))).ToList();
        var owner = ReferenceModel.Owner(pack.Source);
        var entries = pack.Cards.GroupBy(c => Key(c.Detachment)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var context in data.FactionContexts ?? [])
        {
            if (!pack.Contexts.Any(c => c.Faction == context.Faction && c.CatalogueId == context.CatalogueId)
                || !data.Units.Any(u => u.Faction == context.Faction)) continue;
            foreach (var definition in DetachmentRules.Options(context.Faction, data))
            {
                // This alias supplies references only; the BSData placeholder's
                // name, restrictions, cost and implementation status stay intact.
                var name = definition.Name == "Deathwatch Support (ITS NOT READY)" ? "Deathwatch Support" : definition.Name;
                if (!entries.TryGetValue(Key(name), out var supplied)) continue;
                bool Scope(ReferenceCard c) => c.Faction == context.Faction && ReferenceModel.Category(c.Section) == "Stratagems"
                    && (Key(c.Detachment) == Key(definition.Name) || (c.Details?.Detachments ?? []).Any(t => Key(t.Name) == Key(definition.Name)));
                var declarations = coverage.Where(c => c.SourceId == owner && c.Faction == context.Faction
                    && c.Detachment == definition.Name && c.Category == "Stratagems").ToList();
                // A newer snapshot or an explicit complete import may intentionally
                // omit cards. Do not fill those omissions from this older bundle.
                if (coverage.Any(c => c.SourceId == owner && c.Edition == "11" && c.Category == "Stratagems"
                    && (c.Faction.Length == 0 || Key(c.Faction) == Key(context.Faction))
                    && (c.Detachment.Length == 0 || Key(c.Detachment) == Key(definition.Name))
                    && c.Authoritative && c.State is "Complete" or "VerifiedComplete"
                    && string.CompareOrdinal(c.Source?.Date ?? "", pack.Source.Date) >= 0)) continue;
                foreach (var entry in supplied)
                {
                    var card = new ReferenceCard(context.Faction, "Stratagems", entry.Name, "", definition.Name, entry.Cost, pack.Source,
                        Details: new(Edition: "11", SourceType: "Wahapedia", Scope: "Detachment", FactionId: definition.FactionId,
                            Detachments: [new(definition.Name, definition.Id)], Timing: entry.Timing, Target: entry.Target,
                            Effect: entry.Effect, Restrictions: entry.Restrictions));
                    if (Owned(card) || cards.Any(c => Scope(c) && Key(c.Name) == Key(entry.Name))) continue;
                    cards.Add(ReferenceModel.Canonical(card, pack.Source));
                }
                // Complete means all entries in this dated page snapshot, not a
                // claim of official verification. Mixed/manual scopes stay partial.
                var complete = supplied.All(e => cards.Any(c => Scope(c) && Key(c.Name) == Key(e.Name)
                    && c.Source == pack.Source && c.Details?.Status == "Active" && !Owned(c)));
                var declaration = new ReferenceCoverage(owner, "11", Faction: context.Faction, Detachment: definition.Name,
                    State: complete ? "Complete" : "Partial", Mode: complete ? "CompleteForDetachment" : "Partial",
                    Authoritative: complete, Source: pack.Source);
                if (declarations.Count == 0) coverage.Add(declaration);
                else if (declarations.All(c => string.CompareOrdinal(c.Source?.Date ?? "", pack.Source.Date) <= 0)
                    && !declarations.Contains(declaration))
                {
                    coverage.RemoveAll(declarations.Contains);
                    coverage.Add(declaration);
                }
            }
        }
        return cards.SequenceEqual(data.ReferenceCards ?? []) && coverage.SequenceEqual(data.ReferenceCoverage ?? []) ? data
            : data with { ReferenceCards = cards, ReferenceCoverage = coverage };
    }
}
