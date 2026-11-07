using System.Security.Cryptography;
using System.Text;

namespace GrimList.Core;

public enum ImportLayer { Points, Profiles, Combined }

public static class UnitIdentity
{
    // Conservative matching: punctuation and case only. Renames require an explicit ID or alias.
    private sealed record Normalized(string Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Normalized> normalized = new();
    public static string Normalize(string value) => normalized.GetValue(value,
        v => new(new string(v.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()))).Value;
    public static string Create(string game, string nameKey) => "unit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(game) + ":" + Normalize(nameKey))))[..24].ToLowerInvariant();
}

public static class DatasetMerge
{
    // Library import boundary, not roster attachment logic. Preserve for attachment
    // work: Points updates prices; Profiles updates datasheets/reference rules;
    // Combined does both. Supplemental references survive ordinary catalogue updates.
    // Regression coverage for Stratagem retention/layers: StratagemTransferChecks.
    public static ImportPreview Prepare(Dataset? current, Dataset incoming, ImportLayer layer, bool completeFactions = false, bool allowReferenceConflicts = false,
        IReadOnlyDictionary<string, string>? preferredReferenceSources = null)
    {
        var originalIncoming = incoming;
        if (current != null) current = ReferenceData.Normalize(current);
        incoming = ReferenceData.Normalize(incoming);
        if (current != null) incoming = SpaceMarineStratagems.PreserveOverrides(incoming, current);
        // A valid reference-only document can become empty after all its legacy
        // cards are retired. Treat that as a no-op, not a broken import or a
        // reason to put superseded cards back into the current library.
        if (current != null && incoming.Units.Count == 0 && incoming.ReferenceCards is not { Count: > 0 }
            && incoming.RulesPacks is not { Count: > 0 } && originalIncoming.ReferenceCards is { Count: > 0 })
        {
            originalIncoming.Validate();
            CheckEdition(current.Source, incoming.Source, "Reference rules");
            if (current.Game != incoming.Game) throw new InvalidDataException("This library belongs to a different game. Use a separate data directory.");
            if (layer == ImportLayer.Points) throw new InvalidDataException("Reference rules require Datasheets only or Points + datasheets.");
            return new(current, ["All supplied references have been superseded; the current library was retained."]);
        }
        incoming.Validate();
        var rulesPacks = RulesPacks.Register(current, incoming, layer);
        // An installed pack is the explicit authority for its covered rules. Filter
        // only those catalogue references before ordinary source-conflict handling;
        // unrelated Stratagems, Enhancements and Upgrades keep their existing path.
        if (rulesPacks.Count > 0)
            incoming = incoming with { ReferenceCards = incoming.ReferenceCards == null ? null : RulesPacks.WithoutOverrides(incoming.ReferenceCards, rulesPacks) };
        if (incoming.Units.Count == 0)
        {
            if (current == null) throw new InvalidDataException("Import a unit library before adding reference rules.");
            if (layer == ImportLayer.Points) throw new InvalidDataException("Reference rules require Datasheets only or Points + datasheets.");
            CheckEdition(current.Source, incoming.Source, "Reference rules");
            foreach (var unit in current.Units.Where(u => (incoming.ReferenceCards ?? []).Any(c => c.Faction == u.Faction)))
            { CheckEdition(unit.PointsSource, incoming.Source, unit.Name); CheckEdition(unit.ProfileSource, incoming.Source, unit.Name); }
        }
        if (current != null && current.Game != incoming.Game)
            throw new InvalidDataException("This library belongs to a different game. Use a separate data directory.");
        if (layer == ImportLayer.Points && incoming.Units.Any(u => u.Options.Count == 0 && string.IsNullOrWhiteSpace(u.PointsWarning)))
            throw new InvalidDataException("A points import needs at least one price for each incoming unit. Use Datasheets only for profiles without prices.");
        if (layer == ImportLayer.Profiles && incoming.Units.Any(u => u.Profile == null))
            throw new InvalidDataException("Each unit in a datasheet import must contain a Profile.");
        List<string> warnings = [];
        if (incoming.Source.Name.Contains("historical", StringComparison.OrdinalIgnoreCase))
            warnings.Add("HISTORICAL SAMPLE: these are archived values, not current game data. Prefer a separate test library.");
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        var source = incoming.Source with { ImportedAt = stamp };
        var units = (current?.Units ?? []).Select(u => u with
        {
            Id = string.IsNullOrWhiteSpace(u.Id) ? UnitIdentity.Create(incoming.Game, u.NameKey) : u.Id,
            Aliases = new[] { u.Key, u.NameKey }.Concat(u.Aliases ?? []).Distinct().ToList(),
            PointsSource = u.PointsSource ?? (u.Options.Count > 0 ? current!.Source : null),
            ProfileSource = u.ProfileSource ?? (u.Profile != null ? current!.Source : null)
        }).ToDictionary(u => u.Key);
        // Index the same normalized aliases used by the original matching predicate.
        // Update it after each replacement so later imports still detect alias collisions.
        var aliases = new Dictionary<string, HashSet<string>>();
        void Index(UnitDefinition unit, bool remove = false)
        {
            foreach (var name in new[] { unit.NameKey }.Concat(unit.Aliases ?? []).Select(UnitIdentity.Normalize).Distinct())
            {
                if (remove) { if (aliases.TryGetValue(name, out var ids)) ids.Remove(unit.Id); }
                else { if (!aliases.TryGetValue(name, out var ids)) aliases[name] = ids = []; ids.Add(unit.Id); }
            }
        }
        foreach (var unit in units.Values) Index(unit);
        var matched = new HashSet<string>();
        foreach (var next in incoming.Units)
        {
            var names = new[] { next.NameKey }.Concat(next.Aliases ?? []).Select(UnitIdentity.Normalize).ToHashSet();
            var matchIds = names.SelectMany(n => aliases.GetValueOrDefault(n) ?? []).ToHashSet();
            if (next.Id.Length > 0 && units.ContainsKey(next.Id)) matchIds.Add(next.Id);
            var matches = matchIds.Select(id => units[id]).ToList();
            if (matches.Count > 1) throw new InvalidDataException($"{next.Name} matches more than one existing unit. Correct its ID/aliases first.");
            var previous = matches.SingleOrDefault();
            if (previous != null && UnitIdentity.Normalize(previous.Faction) != UnitIdentity.Normalize(next.Faction))
                throw new InvalidDataException($"{next.Name}: a permanent unit ID cannot move between factions.");
            if (previous != null && next.Id.Length > 0 && next.Id != previous.Id)
                throw new InvalidDataException($"{next.Name} has a different permanent ID. Export this library and use its ID.");
            var id = previous?.Id ?? (next.Id.Length > 0 ? next.Id : UnitIdentity.Create(incoming.Game, next.NameKey));
            if (!matched.Add(id)) throw new InvalidDataException($"Two imported units resolve to {next.Name}. Review names and aliases.");
            var changesPoints = layer != ImportLayer.Profiles;
            var changesProfile = layer != ImportLayer.Points && next.Profile != null;
            var pointsSource = changesPoints ? (next.PointsSource ?? source) with { ImportedAt = stamp } : previous?.PointsSource;
            var profileSource = changesProfile ? (next.ProfileSource ?? source) with { ImportedAt = stamp } : previous?.ProfileSource;
            CheckEdition(pointsSource, profileSource, next.Name);
            if (pointsSource != null && profileSource != null && (pointsSource.Edition.Length == 0 || profileSource.Edition.Length == 0))
                warnings.Add($"{next.Name}: points/datasheet edition compatibility is unverified because a source edition is unknown.");
            CheckEdition(changesPoints ? previous?.PointsSource : previous?.ProfileSource, changesPoints ? pointsSource : profileSource, next.Name);
            var oldSource = changesPoints ? previous?.PointsSource : previous?.ProfileSource;
            var newSource = changesPoints ? pointsSource : profileSource;
            if (oldSource?.Date.Length > 0 && newSource?.Date.Length > 0 && MfmPoints.IsMfm(oldSource) == MfmPoints.IsMfm(newSource)
                && string.CompareOrdinal(newSource.Date, oldSource.Date) < 0)
                warnings.Add($"{next.Name}: source date moves backwards ({oldSource.Date} → {newSource.Date}).");
            if (previous != null) Index(previous, remove: true);
            units[id] = next with
            {
                Id = id,
                Faction = previous?.Faction ?? next.Faction,
                Aliases = new[] { next.NameKey }.Concat(next.Aliases ?? []).Concat(previous?.Aliases ?? []).Distinct().ToList(),
                Options = changesPoints ? next.Options : previous?.Options ?? [],
                AlliedOptions = changesPoints ? next.AlliedOptions : previous?.AlliedOptions,
                DetachmentCosts = changesPoints ? next.DetachmentCosts : previous?.DetachmentCosts,
                CopyCategory = changesPoints ? next.CopyCategory : previous?.CopyCategory ?? "",
                ModelCosts = changesPoints ? next.ModelCosts : previous?.ModelCosts,
                PriceFormula = changesPoints ? next.PriceFormula : previous?.PriceFormula,
                Diagnostics = changesPoints ? next.Diagnostics : previous?.Diagnostics,
                AlliedPointsWarning = changesPoints ? next.AlliedPointsWarning : previous?.AlliedPointsWarning ?? "",
                PointsWarning = changesPoints ? next.PointsWarning : previous?.PointsWarning ?? "",
                WargearCosts = changesPoints ? next.WargearCosts ?? previous?.WargearCosts : previous?.WargearCosts,
                Profile = changesProfile ? LeaderRules.ImportRules(next.Profile!)
                    : changesPoints && next.Profile?.Loadout?.Composition != null && previous?.Profile != null
                        ? previous.Profile with { Loadout = next.Profile.Loadout } : previous?.Profile,
                RequiredDetachments = layer == ImportLayer.Points ? previous?.RequiredDetachments : next.RequiredDetachments ?? previous?.RequiredDetachments,
                Category = layer == ImportLayer.Points ? previous?.Category ?? "Units" : next.Category,
                MaxCopies = layer == ImportLayer.Points ? previous?.MaxCopies : next.MaxCopies ?? previous?.MaxCopies,
                PointsSource = pointsSource, ProfileSource = profileSource
            };
            if (changesPoints) units[id] = MfmPoints.Preserve(previous, units[id], source, warnings);
            Index(units[id]);
        }
        if (completeFactions && layer != ImportLayer.Profiles)
        {
            var factions = incoming.Units.Select(u => UnitIdentity.Normalize(u.Faction)).ToHashSet();
            foreach (var u in units.Values.ToList().Where(u => factions.Contains(UnitIdentity.Normalize(u.Faction)) && !matched.Contains(u.Key)))
            {
                units[u.Key] = u with { Options = [], AlliedOptions = u.AlliedOptions == null ? null : [], PointsSource = source };
                if (u.Options.Count > 0) warnings.Add($"{u.Name}: absent from the complete faction update. Prices cleared; profile and roster entries retained.");
            }
        }
        if (source.Edition.Length == 0) warnings.Add("Source edition is unspecified. Edition compatibility cannot be verified.");
        var detachments = (current?.Detachments ?? []).Concat(layer == ImportLayer.Points ? [] : incoming.Detachments ?? [])
            .GroupBy(d => (UnitIdentity.Normalize(d.Faction), UnitIdentity.Normalize(d.Name))).Select(g => g.Last() with { Applicability = g.Last().Applicability ?? g.LastOrDefault(d => d.Applicability != null)?.Applicability }).ToList();
        // Keep selectable rules and display references on their existing merge paths.
        // Enhancement/Upgrade cards retain eligibility, classification and modifiers;
        // Stratagems use ReferenceMerge's source, edition and coverage semantics below.
        // Rebuilding ReferenceCards from attachment profiles would discard both kinds.
        static string CardKey(ReferenceCard c) => string.Join("|", new[] { c.Faction, c.Section, c.Detachment, c.Name }.Select(UnitIdentity.Normalize));
        var cards = (current?.ReferenceCards ?? []).GroupBy(CardKey).ToDictionary(g => g.Key, g => g.Last());
        if (layer != ImportLayer.Points)
            foreach (var c in (incoming.ReferenceCards ?? []).Where(c => !ReferenceModel.DisplayOnly(c)))
            {
                // BSData sometimes stores bearer restrictions only as selection conditions.
                // Keep a complete supplemental card instead of disabling a saved selection.
                if (incoming.Source.Name.Contains("BSData", StringComparison.OrdinalIgnoreCase) && c.Section == "Enhancements"
                    && !Enhancements.HasKnownRestriction(c) && cards.TryGetValue(CardKey(c), out var previous) && Enhancements.HasKnownRestriction(previous))
                {
                    warnings.Add($"{c.Faction} / {c.Name}: retained the existing enhancement card and its source because BSData has no readable bearer restriction. Review it against the latest rules.");
                    continue;
                }
                cards[CardKey(c)] = c;
            }
        var result = new Dataset(incoming.Units.Count == 0 ? current!.Source : source,
            incoming.Units.Count == 0 ? current!.Units : units.Values.OrderBy(u => u.Faction).ThenBy(u => u.Name).ToList(), incoming.Game, detachments,
            cards.Values.Where(c => !ReferenceModel.DisplayOnly(c)).Concat((current?.ReferenceCards ?? []).Where(ReferenceModel.DisplayOnly)
                .Select(c => layer == ImportLayer.Points ? c : ReferenceModel.Canonical(c, current!.Source))).ToList(),
            SchemaVersion: rulesPacks.Count > 0 ? 3 : 2, ReferenceCoverage: current?.ReferenceCoverage, FactionContexts: (current?.FactionContexts ?? []).Concat(layer == ImportLayer.Points ? [] : incoming.FactionContexts ?? []).GroupBy(c => c.Faction).Select(g => g.Last()).ToList());
        List<ReferenceChange> referenceChanges = [];
        if (layer != ImportLayer.Points)
        {
            var references = ReferenceMerge.Prepare(result, incoming, preferredReferenceSources);
            referenceChanges = references.Changes;
            if (references.HasConflicts && !allowReferenceConflicts)
                throw new InvalidDataException("Reference source conflict: " + string.Join("; ", references.Changes.Where(c => c.Status == "Conflict").Select(c => c.Name + ": " + c.Message)));
            result = result with { ReferenceCards = cards.Values.Where(c => !ReferenceModel.DisplayOnly(c)).Concat(references.Cards).ToList(), ReferenceCoverage = references.Coverage };
        }
        result = RulesPacks.Apply(result, rulesPacks, warnings);
        result = DetachmentLifecycle.Reconcile(current, originalIncoming, result, layer, warnings);
        result = DetachmentCatalogue.Apply(result);
        result = SpaceMarineArmyRule.Apply(result);
        result = SpaceMarineStratagems.Apply(result);
        result = ReferenceData.Normalize(result);
        result.Validate();
        return new(result, warnings) { ReferenceChanges = referenceChanges };
    }

    private static void CheckEdition(SourceInfo? a, SourceInfo? b, string unit)
    {
        if (!string.IsNullOrWhiteSpace(a?.Edition) && !string.IsNullOrWhiteSpace(b?.Edition) && !a.Edition.Equals(b.Edition, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{unit}: edition mismatch ({a.Edition} / {b.Edition}). Use a separate library for another edition.");
    }
}
