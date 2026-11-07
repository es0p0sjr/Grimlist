namespace GrimList.Core;

public record ReferenceMergeResult(List<ReferenceCard> Cards, List<ReferenceCoverage> Coverage, List<ReferenceChange> Changes)
{
    public bool HasConflicts => Changes.Any(c => c.Status == "Conflict");
}

public static class ReferenceMerge
{
    private static long evaluations;
    public static long EvaluationCount => Interlocked.Read(ref evaluations);
    public static ReferenceMergeResult Prepare(Dataset current, Dataset incoming,
        IReadOnlyDictionary<string, string>? preferredSources = null)
    {
        Interlocked.Increment(ref evaluations);
        var edition = current.Source.Edition;
        var cards = (current.ReferenceCards ?? []).Where(ReferenceModel.DisplayOnly)
            .Select(c => ReferenceModel.Canonical(c, current.Source)).ToList();
        var incomingCards = (incoming.ReferenceCards ?? []).Where(ReferenceModel.DisplayOnly)
            .Select(c => ReferenceModel.Canonical(c, incoming.Source)).ToList();
        if (incomingCards.Any(c => c.Details!.Edition != edition))
            throw new InvalidDataException("Reference edition mismatch. References cannot cross editions.");
        if (incomingCards.GroupBy(c => (c.Details!.Edition, c.Details.SourceId, c.Details.Id)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate reference identity in source.");
        List<ReferenceChange> changes = [];
        var logicalKeys = new Dictionary<ReferenceCard, string>(ReferenceEqualityComparer.Instance);
        string Logical(ReferenceCard card)
        {
            if (!logicalKeys.TryGetValue(card, out var key)) logicalKeys[card] = key = ReferenceModel.LogicalKey(card);
            return key;
        }
        void Report(string status, ReferenceCard card, string message = "", List<string>? fields = null) =>
            changes.Add(new(status, card.Details!.Id, card.Faction, card.Detachment, card.Name, card.Details.SourceId,
                $"{card.Details.Match}; {card.Source?.Name}, revision {card.Source?.Version}, {card.Source?.Date}. " + message, fields));
        var touched = new HashSet<string>();
        foreach (var raw in incomingCards)
        {
            var card = ReferenceMatching.Match(raw, current);
            var d = card.Details!;
            var previous = cards.FirstOrDefault(c => c.Details!.Id == d.Id && c.Details.SourceId == d.SourceId && c.Details.Edition == edition);
            if (previous == null && d.SourceCardId.Length > 0)
                previous = cards.SingleOrDefault(c => c.Details!.SourceCardId.Length == 0 && c.Details.SourceId == d.SourceId
                    && Logical(c) == Logical(card));
            touched.Add(d.Id + "|" + d.SourceId);
            if (d.Match is "Ambiguous" or "Unresolved")
            {
                if (previous != null) cards.Remove(previous);
                cards.Add(card with { Details = d with { Status = "Unresolved" } });
                Report("Unresolved", card, d.Match + " applicability; card retained but hidden."); continue;
            }
            var logical = Logical(card);
            var others = cards.Where(c => c != previous && c.Details!.Edition == edition && c.Details.Status == "Active"
                && (c.Details.Id == d.Id || Logical(c) == logical)
                && !(c.Details.SourceId == d.SourceId && c.Details.Id == d.Replaces)).ToList();
            var conflicting = others.Where(c => c.Details!.SemanticHash != d.SemanticHash).ToList();
            if (conflicting.Count > 0)
            {
                var preferred = preferredSources?.GetValueOrDefault(logical);
                if (preferred == d.SourceId)
                    foreach (var old in conflicting) { cards.Remove(old); cards.Add(old with { Details = old.Details! with { Status = "Obsolete" } }); Report("Obsolete", old, "Explicit preferred-source resolution."); }
                else if (conflicting.Any(c => c.Details!.SourceId == preferred))
                { Report("Retained", conflicting.First(c => c.Details!.SourceId == preferred), "Explicit preferred-source resolution; incoming skipped."); continue; }
                else
                {
                    foreach (var other in conflicting)
                        Report("Conflict", card, $"Owner {other.Details!.SourceId} ({other.Source?.Name}, {other.Source?.Version}) conflicts with {d.SourceId} ({card.Source?.Name}, {card.Source?.Version}). Select a preferred source explicitly or cancel.", Differences(other, card));
                    continue;
                }
            }
            if (previous != null) cards.Remove(previous);
            cards.Add(card);
            Report(d.Status is "Obsolete" or "Removed" ? "Obsolete" : previous == null ? "Added"
                : previous.Details!.SemanticHash == d.SemanticHash && previous.Details.Status == d.Status ? "Unchanged" : "Updated", card);
            if (d.Replaces.Length > 0)
                foreach (var old in cards.Where(c => c.Details!.Id == d.Replaces && c.Details.SourceId == d.SourceId && c.Details.Edition == edition).ToList())
                { cards.Remove(old); cards.Add(old with { Details = old.Details! with { Status = "Obsolete" } }); Report("Obsolete", old, "Replaced by " + d.Id); }
        }
        var ownersUpdated = incomingCards.Select(c => c.Details!.SourceId).ToHashSet();
        var declarations = incoming.ReferenceCoverage ?? [];
        var coverage = (current.ReferenceCoverage ?? []).Select(c => ownersUpdated.Contains(c.SourceId)
            && declarations.All(d => CoverageKey(d) != CoverageKey(c))
            ? c with { State = "Partial", Mode = "Partial", Authoritative = false } : c).ToList();
        foreach (var declaration in incoming.ReferenceCoverage ?? [])
        {
            ValidateCoverage(declaration, edition);
            var prior = coverage.FirstOrDefault(c => CoverageKey(c) == CoverageKey(declaration));
            coverage.RemoveAll(c => CoverageKey(c) == CoverageKey(declaration));
            // A failed refresh describes an observation, not a new verified
            // revision. Keep the last proof so catalogue drift remains detectable.
            coverage.Add(declaration with { Source = declaration.Source ?? prior?.Source ?? (declaration.ReviewReason == null ? incoming.Source : null),
                CatalogueFingerprint = declaration.CatalogueFingerprint ?? (declaration.ReviewReason != null ? prior?.CatalogueFingerprint : null) });
            if (!declaration.Authoritative || declaration.Mode == "Partial" || declaration.State is not ("Complete" or "VerifiedComplete")
                || changes.Any(c => c.SourceId == declaration.SourceId && c.Status is "Unresolved" or "Conflict")) continue;
            foreach (var old in cards.Where(c => c.Details!.Status is "Active" or "Stale" or "Unresolved" && OwnedScope(c, declaration)
                && !touched.Contains(c.Details.Id + "|" + c.Details.SourceId)).ToList())
            {
                cards.Remove(old); cards.Add(old with { Details = old.Details! with { Status = "Removed" } });
                Report("Removed", old, "Absent from authoritative complete source scope; history retained.");
            }
        }
        foreach (var card in cards.Where(c => !changes.Any(ch => ch.Id == c.Details!.Id && ch.SourceId == c.Details.SourceId))) Report("Retained", card);
        return new(cards, coverage, changes);
    }
    public static string CoverageKey(ReferenceCoverage c) => DataJson.Write(new[] { c.SourceId, c.Edition, ReferenceModel.Key(c.Category), ReferenceModel.Key(c.Faction), ReferenceModel.Key(c.Detachment) });
    public static bool OwnedScope(ReferenceCard c, ReferenceCoverage scope) => c.Details!.SourceId == scope.SourceId && c.Details.Edition == scope.Edition
        && ReferenceModel.Key(c.Section) == ReferenceModel.Key(scope.Category)
        && (scope.Faction.Length == 0 || ReferenceModel.Key(c.Faction) == ReferenceModel.Key(scope.Faction))
        && (scope.Detachment.Length == 0 || (c.Details.Detachments ?? []).Any(t => ReferenceModel.Key(t.Name) == ReferenceModel.Key(scope.Detachment)));
    public static void ValidateCoverage(ReferenceCoverage c, string edition)
    {
        if (c.SourceId.Length == 0 || c.Edition != edition || c.Category.Length == 0
            || c.State is not ("Unknown" or "Partial" or "Complete" or "VerifiedComplete")
            || c.Mode is not ("Partial" or "CompleteForFaction" or "CompleteForDetachment" or "CompleteForEdition")
            || c.Mode == "CompleteForFaction" && (c.Faction.Length == 0 || c.Detachment.Length > 0)
            || c.Mode == "CompleteForDetachment" && (c.Faction.Length == 0 || c.Detachment.Length == 0)
            || c.Mode == "CompleteForEdition" && (c.Faction.Length > 0 || c.Detachment.Length > 0))
            throw new InvalidDataException("Invalid reference coverage declaration.");
    }
    private static List<string> Differences(ReferenceCard a, ReferenceCard b)
    {
        List<string> result = [];
        foreach (var (name, left, right) in new[] { ("Name", a.Name, b.Name), ("Cost", a.Cost, b.Cost), ("Text", a.Text, b.Text),
            ("Timing", a.Details!.Timing, b.Details!.Timing), ("Target", a.Details.Target, b.Details.Target),
            ("Effect", a.Details.Effect, b.Details.Effect), ("Restrictions", a.Details.Restrictions, b.Details.Restrictions),
            ("Applicability", ReferenceModel.LogicalKey(a), ReferenceModel.LogicalKey(b)),
            ("Modifiers", ReferenceModel.ModifierContent(a), ReferenceModel.ModifierContent(b)),
            ("Visibility", DataJson.Write(a.Applicability), DataJson.Write(b.Applicability)),
            ("Warlord restrictions", DataJson.Write(a.WarlordRestrictions ?? []), DataJson.Write(b.WarlordRestrictions ?? [])) })
            if (ReferenceModel.Clean(left) != ReferenceModel.Clean(right)) result.Add(name);
        return result;
    }
}

public static class ReferenceMatching
{
    private sealed class Lookup
    {
        public Dictionary<string, List<string>> Factions { get; }
        public Dictionary<string, List<string>> FactionIds { get; }
        public Dictionary<string, List<DetachmentDefinition>> Names { get; }
        public Dictionary<string, List<DetachmentDefinition>> Ids { get; }
        public Lookup(Dataset data)
        {
            var detachments = data.Detachments ?? [];
            Factions = data.Units.Select(u => u.Faction).Concat(detachments.Select(d => d.Faction)).Distinct()
                .GroupBy(ReferenceModel.Key).ToDictionary(g => g.Key, g => g.ToList());
            FactionIds = detachments.Select(d => (Id: d.FactionId, d.Faction))
                .Concat((data.FactionContexts ?? []).Select(c => (Id: c.CatalogueId, c.Faction)))
                .Where(c => c.Id.Length > 0).GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Faction).Distinct().ToList());
            Names = detachments.GroupBy(d => d.Faction + "|" + ReferenceModel.Key(d.Name)).ToDictionary(g => g.Key, g => g.ToList());
            Ids = detachments.Where(d => d.Id.Length > 0).GroupBy(d => d.Faction + "|" + d.Id).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var d in detachments)
            {
                var key = d.Faction + "|" + CanonicalDetachmentId(data.Source.Edition, d.Faction, d.Name);
                if (!Ids.TryGetValue(key, out var values)) Ids[key] = values = [];
                values.Add(d);
                foreach (var alias in (data.DetachmentSources ?? []).Where(s => s.Faction == d.Faction)
                    .SelectMany(s => s.Active).Where(a => a.Id == d.Id).SelectMany(a => a.Aliases ?? []))
                {
                    var aliasKey = d.Faction + "|" + CanonicalDetachmentId(data.Source.Edition, d.Faction, alias);
                    if (!Ids.TryGetValue(aliasKey, out var aliases)) Ids[aliasKey] = aliases = [];
                    if (!aliases.Contains(d)) aliases.Add(d);
                }
            }
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Dataset, Lookup> lookups = new();
    public static string CanonicalDetachmentId(string edition, string faction, string name) => "detachment-" + ReferenceModel.Hash(DataJson.Write(new[] { edition, ReferenceModel.Key(faction), ReferenceModel.Key(name) }));
    public static ReferenceCard Match(ReferenceCard c, Dataset data)
    {
        var d = c.Details!;
        if (d.Scope == "Global") return c with { Details = d with { Match = "ExactCanonicalMatch" } };
        var lookup = lookups.GetValue(data, data => new(data));
        var factions = d.FactionId.Length > 0 ? lookup.FactionIds.GetValueOrDefault(d.FactionId) ?? []
            : lookup.Factions.GetValueOrDefault(ReferenceModel.Key(c.Faction)) ?? [];
        if (factions.Count != 1) return c with { Details = d with { Match = factions.Count > 1 ? "Ambiguous" : "Unresolved" } };
        if (d.Scope == "Faction") return c with { Faction = factions[0], Details = d with { Match = d.FactionId.Length > 0 ? "ExactId" : "NameFallback" } };
        var targets = (d.Detachments ?? []).Select(t => MatchTarget(t, factions[0], data)).ToList();
        var state = targets.Count == 0 || targets.Any(t => t.Match == "Unresolved") ? "Unresolved"
            : targets.Any(t => t.Match == "Ambiguous") ? "Ambiguous" : targets.All(t => t.Match == "ExactId") ? "ExactId"
            : targets.All(t => t.Match == "ExactCanonicalMatch") ? "ExactCanonicalMatch" : "NameFallback";
        var result = c with { Faction = factions[0], Detachment = targets.Count == 1 ? targets[0].Name : c.Detachment,
            Details = d with { Detachments = targets, Match = state } };
        return result with { Details = result.Details! with { SemanticHash = ReferenceModel.ContentHash(result) } };
    }
    private static ReferenceTarget MatchTarget(ReferenceTarget target, string faction, Dataset data)
    {
        var lookup = lookups.GetValue(data, data => new(data));
        var candidates = target.Id.Length > 0 ? lookup.Ids.GetValueOrDefault(faction + "|" + target.Id) ?? []
            : lookup.Names.GetValueOrDefault(faction + "|" + ReferenceModel.Key(DetachmentLifecycle.ResolveName(data, faction, target.Name))) ?? [];
        return candidates.Count == 1 ? new(candidates[0].Name, candidates[0].Id.Length > 0 ? candidates[0].Id : target.Id,
            target.Id.Length == 0 ? "NameFallback" : target.Id == candidates[0].Id ? "ExactId" : "ExactCanonicalMatch")
            : target with { Match = candidates.Count > 1 ? "Ambiguous" : "Unresolved" };
    }
}
