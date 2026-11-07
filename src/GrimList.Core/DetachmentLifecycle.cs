namespace GrimList.Core;

public record DetachmentIdentity(string Id, string Name, List<string>? Aliases = null);
public record RetiredDetachment(string Id, string Name, SourceInfo RetiredBy, string Reason);
public record DetachmentSourceScope(string Faction, string CatalogueId, SourceInfo Source,
    List<DetachmentIdentity> Active, List<RetiredDetachment> Retired);

// Persistent closed catalogue scopes. Absence is meaningful only after the
// downloader/importer has proved that the whole detachment scope was resolved.
public static class DetachmentLifecycle
{
    private static string Key(string s) => UnitIdentity.Normalize(s);
    public static bool BsData(SourceInfo? source) => source?.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) == true;
    private static DetachmentSourceScope? Scope(Dataset? data, string faction) => data?.DetachmentSources?
        .FirstOrDefault(s => Key(s.Faction) == Key(faction));
    private static IEnumerable<string> Names(Dataset data, string faction, string name) =>
        Scope(data, faction)?.Active.FirstOrDefault(d => Named(d, name)) is { } identity
            ? (identity.Aliases ?? []).Append(identity.Name) : [name];
    private static bool Owned(Dataset data, string faction, string name) => (data.RulesPacks ?? [])
        .Any(p => Names(data, faction, name).Any(n => RulesPacks.OwnsDetachment(p, new(faction, n))));
    private static bool Named(DetachmentIdentity d, string name) => Key(d.Name) == Key(name)
        || (d.Aliases ?? []).Any(a => Key(a) == Key(name));
    public static string ResolveName(Dataset? data, string faction, string name) =>
        Scope(data, faction)?.Active.FirstOrDefault(d => Named(d, name))?.Name ?? name;
    public static bool IsObsolete(Dataset? data, string faction, string name) => data != null && !Owned(data, faction, name)
        && name.Length > 0 && Scope(data, faction) is { } scope && !scope.Active.Any(d => Named(d, name))
        && !(data.Detachments ?? []).Any(d => Key(d.Faction) == Key(faction) && Key(d.Name) == Key(name) && !BsData(d.Source ?? data.Source));
    public static bool Allows(Dataset data, DetachmentDefinition definition) => Owned(data, definition.Faction, definition.Name)
        || !BsData(definition.Source ?? data.Source) || Scope(data, definition.Faction) is not { } scope
        || scope.Active.Any(d => definition.Id.Length > 0 ? d.Id == definition.Id : Named(d, definition.Name));

    public static void Validate(Dataset data)
    {
        if (data.DetachmentSources == null) return;
        if (data.SchemaVersion < 4) throw new InvalidDataException("Detachment lifecycle metadata requires dataset schema 4.");
        if (data.DetachmentSources.GroupBy(s => Key(s.Faction)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Ambiguous detachment authority.");
        foreach (var scope in data.DetachmentSources)
            if (scope.Faction.Length == 0 || scope.CatalogueId.Length == 0 || !BsData(scope.Source)
                || scope.Source.Edition != data.Source.Edition || scope.Source.ContentHash.Length != 64
                || !scope.Source.ContentHash.All(Uri.IsHexDigit) || scope.Source.Version.Length != 40 || !scope.Source.Version.All(Uri.IsHexDigit)
                || scope.Active.Any(d => d.Id.Length == 0 || d.Name.Length == 0)
                || scope.Active.GroupBy(d => d.Id).Any(g => g.Count() != 1)
                || scope.Active.GroupBy(d => Key(d.Name)).Any(g => g.Count() != 1)
                || scope.Active.SelectMany(d => (d.Aliases ?? []).Append(d.Name).Select(n => (Name: Key(n), d.Id)))
                    .GroupBy(p => p.Name).Any(g => g.Select(p => p.Id).Distinct().Count() > 1))
                throw new InvalidDataException("Incomplete or conflicting detachment authority metadata.");
    }

    public static Dataset Reconcile(Dataset? current, Dataset incoming, Dataset merged, ImportLayer layer, List<string> warnings)
    {
        var scopes = (current?.DetachmentSources ?? []).ToList();
        if (layer != ImportLayer.Points)
        foreach (var next in incoming.DetachmentSources ?? [])
        {
            var old = scopes.FirstOrDefault(s => Key(s.Faction) == Key(next.Faction));
            // Importing an exported library into an empty installation retains its
            // safety metadata. An ordinary local import cannot roll back authority.
            if (!incoming.IsBsDataDownload && current != null)
            {
                if (old?.Source.ContentHash != next.Source.ContentHash)
                    warnings.Add($"{next.Faction}: retained installed detachment authority; a local/cached import cannot reverse retirement. Download BSData to reconcile detachments.");
                continue;
            }
            if (old != null && old.CatalogueId != next.CatalogueId)
                throw new InvalidDataException($"{next.Faction}: catalogue identity changed; detachment replacement requires source review.");
            var previous = (current?.Detachments ?? []).Concat(merged.Detachments ?? []).Where(d => Key(d.Faction) == Key(next.Faction)
                && BsData(d.Source ?? current?.Source ?? merged.Source)).DistinctBy(d => (d.Id, d.Name)).ToList();
            var identities = next.Active.Select(d =>
            {
                var known = old?.Active.FirstOrDefault(p => p.Id == d.Id);
                var names = (known?.Aliases ?? []).Concat(d.Aliases ?? []).Concat(known == null ? [] : [known.Name])
                    .Concat(previous.Where(p => p.Id == d.Id).Select(p => p.Name))
                    .Concat((old?.Retired ?? []).Where(p => p.Id == d.Id).Select(p => p.Name))
                    .Where(n => Key(n) != Key(d.Name)).Distinct().ToList();
                return d with { Aliases = names.Count == 0 ? null : names };
            }).ToList();
            identities = identities.Select(d => d with { Aliases = d.Aliases?.Where(a => !identities.Any(other => other.Id != d.Id && Named(other, a))).ToList() }).ToList();
            var retired = (old?.Retired ?? next.Retired).Where(r => !identities.Any(d => d.Id == r.Id || Named(d, r.Name))).ToList();
            foreach (var d in previous)
                if (!Owned(merged, d.Faction, d.Name) && !identities.Any(n => d.Id.Length > 0 ? n.Id == d.Id : Named(n, d.Name))
                    && !retired.Any(r => Key(r.Name) == Key(d.Name)))
                    retired.Add(new(d.Id, d.Name, next.Source, "Absent or permanently unavailable in the complete BSData detachment scope"));
            // Include identities last observed before this update even if a rules
            // supplement or normalization has already removed their definitions.
            foreach (var d in old?.Active ?? [])
                if (!identities.Any(n => n.Id == d.Id) && !retired.Any(r => r.Id == d.Id))
                    retired.Add(new(d.Id, d.Name, next.Source, "Absent from the complete BSData detachment scope"));
            scopes.RemoveAll(s => Key(s.Faction) == Key(next.Faction));
            scopes.Add(next with { Active = identities, Retired = retired });
        }
        return Apply(merged with { DetachmentSources = scopes.Count == 0 ? null : scopes,
            SchemaVersion = scopes.Count > 0 ? 4 : merged.SchemaVersion, IsBsDataDownload = false });
    }

    public static Dataset Apply(Dataset data)
    {
        if (data.DetachmentSources is not { Count: > 0 }) return data;
        bool Removed(string faction, string name) => name.Length > 0 && !Owned(data, faction, name)
            && Scope(data, faction) is { } s && !s.Active.Any(d => Named(d, name))
            && !(data.Detachments ?? []).Any(d => Key(d.Faction) == Key(faction) && Key(d.Name) == Key(name) && !BsData(d.Source ?? data.Source));
        var definitions = (data.Detachments ?? []).Where(d => Allows(data, d))
            .Select(d => ResolveName(data, d.Faction, d.Name) is var name && name != d.Name ? d with { Name = name } : d)
            .GroupBy(d => (Key(d.Faction), Key(d.Name))).Select(g => g.Last()).ToList();
        var cards = new List<ReferenceCard>();
        bool OwnsCard(ReferenceCard c) => (data.RulesPacks ?? []).Any(p => Names(data, c.Faction, c.Detachment)
            .Any(n => RulesPacks.OwnsCard(p, c with { Detachment = n })));
        string CardKey(ReferenceCard c) => string.Join("|", Key(c.Faction), Key(c.Section), Key(c.Name),
            string.Join(",", (c.Details?.Detachments is { Count: > 0 } targets ? targets.Select(t => t.Name) : [c.Detachment]).Select(Key).Order()));
        var originalCardKeys = (data.ReferenceCards ?? []).Select(CardKey).ToHashSet();
        var remappedCardKeys = new HashSet<string>();
        void AddCard(ReferenceCard original, ReferenceCard updated)
        {
            // A source-ID rename can leave the old and incoming card under two
            // name keys in the merge. Prefer the card already in the new scope.
            if (CardKey(original) != CardKey(updated))
            {
                remappedCardKeys.Add(CardKey(updated));
                if (!OwnsCard(original) && originalCardKeys.Contains(CardKey(updated))) return;
            }
            cards.Add(updated);
        }
        foreach (var card in data.ReferenceCards ?? [])
        {
            if (card.Details?.Detachments is { Count: > 0 } targets)
            {
                var kept = targets.Where(t => OwnsCard(card) || !Removed(card.Faction, t.Name)).Select(t => t with { Name = ResolveName(data, card.Faction, t.Name) }).ToList();
                if (kept.Count == 0) continue;
                AddCard(card, kept.SequenceEqual(targets) ? card : card with { Detachment = kept.Count == 1 ? kept[0].Name : "", Details = card.Details with { Detachments = kept } });
            }
            else if (OwnsCard(card) || !Removed(card.Faction, card.Detachment))
                AddCard(card, ResolveName(data, card.Faction, card.Detachment) is var name && name != card.Detachment ? card with { Detachment = name } : card);
        }
        // Explicit packs are applied last before lifecycle reconciliation. Keep
        // that precedence when their former scope and current scope coalesce.
        cards = cards.GroupBy(CardKey).SelectMany(g => remappedCardKeys.Contains(g.Key) ? [g.Last()] : g.ToList()).ToList();
        var coverage = data.ReferenceCoverage?.Where(c => !Removed(c.Faction, c.Detachment))
            .Select(c => ResolveName(data, c.Faction, c.Detachment) is var name && name != c.Detachment ? c with { Detachment = name } : c).ToList();
        // Mandatory upgrades and context prices must not keep retired effects alive.
        var units = data.Units.Select(u => {
            if (u.DetachmentCosts == null) return u;
            var costs = u.DetachmentCosts.Where(c => !Removed(u.Faction, c.Detachment))
                .Select(c => ResolveName(data, u.Faction, c.Detachment) is var name && name != c.Detachment ? c with { Detachment = name } : c).ToList();
            return costs.SequenceEqual(u.DetachmentCosts) ? u : u with { DetachmentCosts = costs };
        }).ToList();
        // Preserve immutable identity when recovery replays an already-clean library.
        if (definitions.SequenceEqual(data.Detachments ?? []) && cards.SequenceEqual(data.ReferenceCards ?? [])
            && (coverage ?? []).SequenceEqual(data.ReferenceCoverage ?? []) && units.SequenceEqual(data.Units)) return data;
        return data with { Detachments = definitions, ReferenceCards = cards, ReferenceCoverage = coverage, Units = units };
    }
}
