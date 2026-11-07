using System.Text.Json;

namespace GrimList.Core;

// The current BSData rule is linked to datasheets through a shared library that
// isn't a native faction catalogue. Promote only this reviewed army-wide rule;
// importing every linked library rule would leak allied and equipment rules.
public static class SpaceMarineArmyRule
{
    private sealed record Supplement(SourceInfo Source, List<string> Factions, List<FactionContext> Contexts, string Name, string Text);
    private static readonly Lazy<Supplement> bundled = new(() =>
    {
        using var stream = typeof(SpaceMarineArmyRule).Assembly.GetManifestResourceStream("GrimList.Core.Resources.space-marine-army-rule-11.json")!;
        return JsonSerializer.Deserialize<Supplement>(stream, DataJson.Options)!;
    });
    private static string Key(string value) => ReferenceModel.Key(value);

    public static Dataset Apply(Dataset data)
    {
        if (data.Game != "Warhammer 40,000" || data.Source.Edition != "11") return data;
        var pack = bundled.Value;
        bool Marine(string faction) => pack.Factions.Any(f => Key(f) == Key(faction));
        bool Owned(ReferenceCard card) => (data.RulesPacks ?? []).Any(p => RulesPacks.OwnsCard(p, card));
        bool OldBsData(ReferenceCard card)
        {
            var source = card.Source ?? data.Source;
            return source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)
                && (card.Details?.Edition ?? source.Edition) == "11"
                && source.Date.Length > 0 && string.CompareOrdinal(source.Date, pack.Source.Date) <= 0;
        }
        var cards = (data.ReferenceCards ?? []).Where(c => !(
            Marine(c.Faction) && OldBsData(c) && !Owned(c) && c.Details?.Status is not ("Removed" or "Obsolete")
            && (c.Details?.Detachments is not { Count: > 1 })
            && ((ReferenceModel.Category(c.Section) == "Army Rules" && c.Detachment.Length == 0 && Key(c.Name) == "OATHOFMOMENT")
                || (ReferenceModel.Category(c.Section) == "Detachments" && Key(c.Detachment) == "GLADIUSTASKFORCE" && Key(c.Name) == "COMBATDOCTRINES")))).ToList();

        foreach (var context in data.FactionContexts ?? [])
        {
            if (!pack.Contexts.Any(c => c.Faction == context.Faction && c.CatalogueId == context.CatalogueId)
                || !data.Units.Any(u => u.Faction == context.Faction)) continue;
            var card = new ReferenceCard(context.Faction, "Army Rules", pack.Name, pack.Text, Source: pack.Source);
            if (Owned(card)) continue;
            // Prefer current/future source text from the actual unit library. Older
            // bundled installations get the pinned, dated source rule without a download.
            var ability = data.Units.Where(u => u.Faction == context.Faction)
                .SelectMany(u => (u.Profile?.Abilities ?? []).Where(a => Key(a.Name) == "COMBATDOCTRINES")
                    .Select(a => (Ability: a, Source: u.ProfileSource ?? data.Source)))
                .Where(a => a.Source.Edition == "11" && a.Source.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)
                    && string.CompareOrdinal(a.Source.Date, pack.Source.Date) >= 0 && a.Ability.Text.Length > 0)
                .OrderByDescending(a => a.Source.Date, StringComparer.Ordinal).FirstOrDefault();
            if (ability.Ability != null) card = card with { Text = ability.Ability.Text, Source = ability.Source };
            var existing = cards.Where(c => c.Faction == context.Faction && ReferenceModel.Category(c.Section) == "Army Rules"
                && c.Detachment.Length == 0 && c.Details?.Detachments is not { Count: > 0 } && Key(c.Name) == "COMBATDOCTRINES").ToList();
            // A supplied reference or tombstone wins at the same/newer date; manual
            // and rules-pack references always retain their explicit ownership.
            if (existing.Any(c => c.Details?.Status is "Removed" or "Obsolete"
                || !(c.Source ?? data.Source).Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase)
                || string.CompareOrdinal((c.Source ?? data.Source).Date, card.Source!.Date) >= 0)) continue;
            cards.RemoveAll(existing.Contains);
            cards.Add(ReferenceModel.Canonical(card, data.Source));
        }
        return cards.SequenceEqual(data.ReferenceCards ?? []) ? data : data with { ReferenceCards = cards };
    }
}
