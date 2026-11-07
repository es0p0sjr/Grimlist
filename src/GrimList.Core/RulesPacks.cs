using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrimList.Core;

// Rules packs are data, not faction-specific code. Explicit scopes authorize replacement;
// absence in a partial pack never authorizes deleting another rule or detachment.
public record RulesCardScope(string Faction, string Section, string Detachment = "");
public record RulesCardRemoval(string Faction, string Section, string Name, string Detachment = "");
public record RulesDetachmentRemoval(string Faction, string Name);
public record RulesUnitUpdate(string Faction, string Name, UnitProfile Profile, List<string> Fields,
    string UnitId = "", bool AddIfMissing = false, List<PointOption>? InitialPoints = null,
    bool ReplaceSelectionRules = false, string Category = "Units", int? MaxCopies = null, List<string>? RequiredDetachments = null);
public record RulesPack(string Id, int Revision, SourceInfo Source, List<string> Factions,
    List<RulesUnitUpdate> Units, List<DetachmentDefinition> Detachments, List<ReferenceCard> Cards,
    string Status = "Verified", string Coverage = "Partial", string Notes = "",
    List<RulesCardScope>? ReplaceCardScopes = null, List<RulesCardRemoval>? RemoveCards = null,
    List<string>? ReplaceDetachmentsFor = null, List<RulesDetachmentRemoval>? RemoveDetachments = null,
    int FormatVersion = 1);

public static class RulesPacks
{
    private static string Key(string text) => UnitIdentity.Normalize(text);
    private static string CardKey(ReferenceCard c) => CardKey(c.Faction, c.Section, c.Detachment, c.Name);
    private static string CardKey(string faction, string section, string detachment, string name)
        => string.Join("|", Key(faction), Key(ReferenceModel.Category(section)), Key(detachment), Key(name));
    private static string DetachmentKey(string faction, string name) => Key(faction) + "|" + Key(name);
    private static readonly HashSet<string> ProfileFields = [nameof(UnitProfile.Characteristics), nameof(UnitProfile.Models),
        nameof(UnitProfile.Weapons), nameof(UnitProfile.Abilities), nameof(UnitProfile.Keywords), nameof(UnitProfile.Notes),
        nameof(UnitProfile.Warnings), nameof(UnitProfile.MustBeWarlord), nameof(UnitProfile.Loadout), nameof(UnitProfile.AttachmentRules),
        nameof(UnitProfile.Associations), nameof(UnitProfile.DetachmentKeywords), nameof(UnitProfile.Semantics), nameof(UnitProfile.WarlordRestrictions)];

    public static void Validate(RulesPack pack)
    {
        Dataset.ValidateSource(pack.Source);
        if (pack.FormatVersion != 1 || string.IsNullOrWhiteSpace(pack.Id) || pack.Revision < 1
            || pack.Source.Edition.Length == 0 || pack.Source.Date.Length == 0 || pack.Source.Url.Length == 0
            || pack.Status is not ("Verified" or "Provisional") || pack.Coverage is not ("Partial" or "Complete")
            || pack.Factions.Count == 0 || pack.Factions.Any(string.IsNullOrWhiteSpace)
            || pack.Factions.Select(Key).Distinct().Count() != pack.Factions.Count)
            throw new InvalidDataException("Rules packs need an ID, revision, dated edition-specific source, faction scope and valid status/coverage.");
        var factions = pack.Factions.Select(Key).ToHashSet();
        if (pack.Units.Select(u => u.Faction).Concat(pack.Detachments.Select(d => d.Faction)).Concat(pack.Cards.Select(c => c.Faction))
            .Concat((pack.ReplaceCardScopes ?? []).Select(s => s.Faction)).Concat((pack.RemoveCards ?? []).Select(s => s.Faction))
            .Concat(pack.ReplaceDetachmentsFor ?? []).Concat((pack.RemoveDetachments ?? []).Select(d => d.Faction))
            .Any(f => !factions.Contains(Key(f)))) throw new InvalidDataException("A rules pack cannot alter content outside its declared factions.");
        if (pack.Units.GroupBy(u => (Key(u.Faction), Key(u.Name))).Any(g => g.Count() > 1)
            || pack.Cards.GroupBy(CardKey).Any(g => g.Count() > 1)
            || pack.Detachments.GroupBy(d => DetachmentKey(d.Faction, d.Name)).Any(g => g.Count() > 1))
            throw new InvalidDataException("Duplicate unit, rule or detachment in rules pack.");
        foreach (var unit in pack.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.Name) || unit.Profile == null || unit.Fields.Count == 0
                || unit.Fields.Distinct().Count() != unit.Fields.Count || unit.Fields.Any(f => f != "*" && !ProfileFields.Contains(f))
                || unit.Fields.Contains("*") && unit.Fields.Count != 1 || unit.AddIfMissing && !unit.Fields.Contains("*"))
                throw new InvalidDataException($"{unit.Name}: specify valid profile fields, or '*' for a complete profile.");
            // New ability/weapon text must not inherit compiled effects from the previous codex.
            // Authors supply reviewed semantics, or explicitly clear them for text-only rules.
            if (!unit.Fields.Contains("*") && unit.Fields.Any(f => f is "Abilities" or "Weapons" or "Loadout")
                && !unit.Fields.Contains("Semantics"))
                throw new InvalidDataException($"{unit.Name}: changes to abilities, weapons or loadout must explicitly replace Semantics too.");
        }
        if (pack.Cards.Any(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Section)
                || string.IsNullOrWhiteSpace(c.DisplayText))
            || pack.Detachments.Any(d => string.IsNullOrWhiteSpace(d.Name))
            || (pack.ReplaceCardScopes ?? []).Any(s => string.IsNullOrWhiteSpace(s.Section))
            || (pack.RemoveCards ?? []).Any(s => string.IsNullOrWhiteSpace(s.Section) || string.IsNullOrWhiteSpace(s.Name))
            || (pack.RemoveDetachments ?? []).Any(d => string.IsNullOrWhiteSpace(d.Name)))
            throw new InvalidDataException("Rules pack contains an unnamed or empty rule/scope.");
        if (pack.Cards.Any(c => c.Details?.Detachments is { Count: > 0 } targets
                && (targets.Count != 1 || Key(targets[0].Name) != Key(c.Detachment))))
            throw new InvalidDataException("Use one rules-pack card per detachment with matching applicability metadata.");
        if (pack.Cards.Any(c => (pack.RemoveCards ?? []).Any(r => CardKey(c) == CardKey(r.Faction, r.Section, r.Detachment, r.Name)))
            || pack.Detachments.Any(d => (pack.RemoveDetachments ?? []).Any(r => DetachmentKey(d.Faction, d.Name) == DetachmentKey(r.Faction, r.Name))))
            throw new InvalidDataException("A pack cannot both remove and provide the same rule or detachment.");
    }

    public static List<RulesPack> Register(Dataset? current, Dataset incoming, ImportLayer layer)
    {
        var packs = (current?.RulesPacks ?? []).ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var pack in layer == ImportLayer.Points ? [] : incoming.RulesPacks ?? [])
        {
            Validate(pack);
            if (pack.Source.Edition != (current?.Source.Edition ?? incoming.Source.Edition))
                throw new InvalidDataException("Rules pack edition does not match this library: " + pack.Id);
            if (packs.TryGetValue(pack.Id, out var old))
            {
                if (pack.Revision < old.Revision) throw new InvalidDataException("Older rules pack revision rejected: " + pack.Id);
                if (pack.Revision == old.Revision && DataJson.Write(pack) != DataJson.Write(old))
                    throw new InvalidDataException("Changed rules pack content requires a new revision: " + pack.Id);
                if (pack.Revision > old.Revision && string.CompareOrdinal(pack.Source.Date, old.Source.Date) < 0)
                    throw new InvalidDataException("Rules pack source date moves backwards: " + pack.Id);
                if (pack.Revision > old.Revision && (old.Units.Any(u => !pack.Units.Any(n => Key(n.Faction) == Key(u.Faction)
                    && Key(n.Name) == Key(u.Name) && n.UnitId == u.UnitId && (n.Fields.Contains("*") || u.Fields.All(n.Fields.Contains))
                    && (!u.ReplaceSelectionRules || n.ReplaceSelectionRules)))
                    || old.Cards.Any(c => !OwnsCard(pack, c))
                    || (old.RemoveCards ?? []).Any(c => !OwnsCard(pack, new(c.Faction, c.Section, c.Name, "removed", c.Detachment)))
                    || old.Detachments.Any(d => !OwnsDetachment(pack, d))
                    || (old.RemoveDetachments ?? []).Any(d => !OwnsDetachment(pack, new(d.Faction, d.Name)))
                    || (old.ReplaceCardScopes ?? []).Any(s => !(pack.ReplaceCardScopes ?? []).Contains(s))
                    || (old.ReplaceDetachmentsFor ?? []).Any(f => !(pack.ReplaceDetachmentsFor ?? []).Any(n => Key(n) == Key(f)))))
                    throw new InvalidDataException("Rules pack revisions must retain previous coverage. Include replacements or explicit removals: " + pack.Id);
            }
            packs[pack.Id] = pack;
        }
        // Multiple independent packs may cover separate factions. Overlapping faction
        // ownership requires an explicit consolidated revision, never last-file-wins.
        var result = packs.Values.ToList();
        if (result.SelectMany(p => p.Factions.Select(f => Key(f))).GroupBy(f => f).Any(g => g.Count() > 1))
            throw new InvalidDataException("Rules packs overlap a faction. Consolidate the updates into a new revision of its existing pack.");
        return result;
    }

    private static bool InScope(ReferenceCard card, RulesCardScope scope) => Key(card.Faction) == Key(scope.Faction)
        && Key(ReferenceModel.Category(card.Section)) == Key(ReferenceModel.Category(scope.Section))
        && Key(card.Detachment) == Key(scope.Detachment);
    public static bool OwnsDetachment(RulesPack pack, DetachmentDefinition detachment)
        => (pack.ReplaceDetachmentsFor ?? []).Any(f => Key(f) == Key(detachment.Faction))
            || pack.Detachments.Any(d => DetachmentKey(d.Faction, d.Name) == DetachmentKey(detachment.Faction, detachment.Name))
            || (pack.RemoveDetachments ?? []).Any(d => DetachmentKey(d.Faction, d.Name) == DetachmentKey(detachment.Faction, detachment.Name));
    public static bool OwnsCard(RulesPack pack, ReferenceCard card) => pack.Cards.Any(c => CardKey(c) == CardKey(card))
        || (pack.ReplaceCardScopes ?? []).Any(s => InScope(card, s))
        || (pack.RemoveCards ?? []).Any(r => CardKey(card) == CardKey(r.Faction, r.Section, r.Detachment, r.Name))
        || (pack.RemoveDetachments ?? []).Any(r => DetachmentKey(card.Faction, card.Detachment) == DetachmentKey(r.Faction, r.Name))
        || (pack.ReplaceDetachmentsFor ?? []).Any(f => Key(f) == Key(card.Faction)) && card.Detachment.Length > 0
            && !pack.Detachments.Any(d => DetachmentKey(d.Faction, d.Name) == DetachmentKey(card.Faction, card.Detachment));

    public static List<ReferenceCard> WithoutOverrides(IEnumerable<ReferenceCard> cards, List<RulesPack> packs)
    {
        List<ReferenceCard> result = [];
        foreach (var card in cards)
        {
            // A shared source card may apply to several detachments. Replacing one
            // scope must neither leave its old text visible nor erase its siblings.
            var scoped = card.Details?.Detachments is { Count: > 0 } targets
                ? targets.Select(t => card with { Detachment = t.Name, Details = card.Details with {
                    Id = card.Details.Id + "/scope-" + Key(t.Name), Detachments = [t] } }).ToList() : [card];
            if (scoped.Any(c => packs.Any(p => OwnsCard(p, c))))
                result.AddRange(scoped.Where(c => !packs.Any(p => OwnsCard(p, c))));
            else result.Add(card);
        }
        return result;
    }

    public static Dataset Apply(Dataset data, List<RulesPack> packs, List<string> warnings)
    {
        if (packs.Count == 0) return data;
        var units = data.Units.ToList(); var cards = (data.ReferenceCards ?? []).ToList();
        var detachments = (data.Detachments ?? []).ToList(); var coverage = (data.ReferenceCoverage ?? []).ToList();
        foreach (var pack in packs)
        {
            Validate(pack);
            if (pack.Source.Edition != data.Source.Edition) throw new InvalidDataException("Rules pack edition mismatch: " + pack.Id);
            warnings.Add($"Rules pack: {pack.Source.Name}, revision {pack.Revision}, {pack.Source.Date} ({pack.Status}; {pack.Coverage} coverage). {pack.Notes}".Trim());
            foreach (var update in pack.Units)
            {
                var matches = units.Where(u => Key(u.Faction) == Key(update.Faction) && (update.UnitId.Length > 0
                    ? u.Key == update.UnitId || (u.Aliases ?? []).Contains(update.UnitId)
                    : Key(u.Name) == Key(update.Name))).ToList();
                if (matches.Count != 1 && !(matches.Count == 0 && update.AddIfMissing))
                    throw new InvalidDataException($"Rules pack {pack.Id}: {update.Faction} / {update.Name} matched {matches.Count} units. Supply an exact unit ID or correct the source.");
                var previous = matches.SingleOrDefault();
                var full = update.Fields.Contains("*");
                var profile = update.Profile;
                if (!full)
                {
                    if (previous?.Profile == null) throw new InvalidDataException("Partial rules update needs an existing datasheet: " + update.Name);
                    if (update.Fields.Contains("Characteristics") && previous.Profile.Models is { Count: > 0 } && !update.Fields.Contains("Models"))
                        throw new InvalidDataException(update.Name + ": update Models alongside Characteristics so the displayed statlines agree.");
                    var target = JsonSerializer.SerializeToNode(previous.Profile, DataJson.Options)!.AsObject();
                    var replacement = JsonSerializer.SerializeToNode(update.Profile, DataJson.Options)!.AsObject();
                    foreach (var field in update.Fields) target[field] = replacement[field]?.DeepClone();
                    profile = target.Deserialize<UnitProfile>(DataJson.Options)!;
                }
                profile = LeaderRules.ImportRules(profile);
                if ((full || update.Fields.Contains("Semantics")) && profile.Semantics == null)
                    warnings.Add(update.Name + ": updated rules are displayed; this pack supplies no automatic datasheet effects. Review conditional effects manually.");
                // Points, wargear costs, source IDs and aliases remain on their tested
                // paths. Replacing a profile never constructs a new roster identity.
                var next = (previous ?? new UnitDefinition(update.Faction, update.Name, update.InitialPoints ?? [],
                    Id: update.UnitId.Length > 0 ? update.UnitId : UnitIdentity.Create(data.Game, update.Faction + " / " + update.Name),
                    PointsSource: pack.Source, PointsWarning: update.InitialPoints is { Count: > 0 } ? "" : "Points unavailable for this new rules-pack unit."))
                    with { Profile = profile, ProfileSource = pack.Source };
                if (update.ReplaceSelectionRules || previous == null)
                    next = next with { Category = update.Category, MaxCopies = update.MaxCopies, RequiredDetachments = update.RequiredDetachments };
                if (previous == null) units.Add(next); else units[units.IndexOf(previous)] = next;
            }
            detachments.RemoveAll(d => (pack.ReplaceDetachmentsFor ?? []).Any(f => Key(f) == Key(d.Faction))
                || (pack.RemoveDetachments ?? []).Any(r => DetachmentKey(r.Faction, r.Name) == DetachmentKey(d.Faction, d.Name))
                || pack.Detachments.Any(n => DetachmentKey(n.Faction, n.Name) == DetachmentKey(d.Faction, d.Name)));
            detachments.AddRange(pack.Detachments.Select(d => d with { Source = pack.Source }));
            cards = WithoutOverrides(cards, [pack]);
            cards.AddRange(pack.Cards.Select(c => ReferenceModel.Canonical(c with { Source = pack.Source,
                Details = c.Details is { } detail ? detail with { SourceId = "rules-pack:" + pack.Id, Edition = pack.Source.Edition, Status = "Active" }
                    : new ReferenceDetails(SourceId: "rules-pack:" + pack.Id, Edition: pack.Source.Edition,
                        Scope: c.Detachment.Length > 0 ? "Detachment" : "Faction") }, pack.Source)));
            // A previous source's completeness claim cannot certify a revised codex.
            // Only explicitly complete replacement scopes receive a fresh declaration.
            var touchedScopes = pack.Cards.Select(c => new RulesCardScope(c.Faction, c.Section, c.Detachment))
                .Concat(pack.ReplaceCardScopes ?? []).Concat((pack.RemoveCards ?? []).Select(c => new RulesCardScope(c.Faction, c.Section, c.Detachment))).ToList();
            coverage = coverage.Where(c => c.SourceId != "rules-pack:" + pack.Id).Select(c =>
                touchedScopes.Any(s => (c.Faction.Length == 0 || Key(s.Faction) == Key(c.Faction))
                    && (c.Detachment.Length == 0 || Key(s.Detachment) == Key(c.Detachment))
                    && Key(ReferenceModel.Category(s.Section)) == Key(c.Category))
                || (pack.RemoveDetachments ?? []).Any(d => (c.Faction.Length == 0 || Key(d.Faction) == Key(c.Faction))
                    && (c.Detachment.Length == 0 || Key(d.Name) == Key(c.Detachment)))
                || (pack.ReplaceDetachmentsFor ?? []).Any(f => c.Faction.Length == 0 || Key(f) == Key(c.Faction))
                    ? c with { State = "Partial", Mode = "Partial", Authoritative = false } : c).ToList();
            coverage.AddRange((pack.ReplaceCardScopes ?? []).Where(s => s.Detachment.Length > 0).Select(s =>
                new ReferenceCoverage("rules-pack:" + pack.Id, pack.Source.Edition, ReferenceModel.Category(s.Section), s.Faction, s.Detachment,
                    pack.Status == "Verified" ? "VerifiedComplete" : "Partial", "CompleteForDetachment", pack.Status == "Verified", pack.Source)));
        }
        return data with { Units = units, Detachments = detachments, ReferenceCards = cards, ReferenceCoverage = coverage, RulesPacks = packs };
    }
}
