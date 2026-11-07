namespace GrimList.Core;

/// <summary>Conservative compatibility cleanup for references produced by the old BSData importer.</summary>
public static class ReferenceData
{
    public static Dataset Normalize(Dataset data)
    {
        data = DetachmentCatalogue.Apply(data);
        data = SpaceMarineArmyRule.Apply(data);
        data = SpaceMarineStratagems.Apply(data);
        data = DetachmentLifecycle.Apply(data);
        data = ReferenceCatalogueCompatibility.Apply(data);
        if (data.ReferenceCards is not { Count: > 0 } cards) return data;
        static string Faction(ReferenceCard c) => UnitIdentity.Normalize(c.Faction);
        static bool BsData(ReferenceCard c) => c.Source?.Name.StartsWith("BSData", StringComparison.OrdinalIgnoreCase) == true;
        var scoped = cards.Where(c => c.Section == "Detachments" && c.Detachment.Length > 0)
            .Select(c => (Faction(c), UnitIdentity.Normalize(c.Name), c.Text.Trim())).ToHashSet();
        var abilities = data.Units.Where(u => (u.ProfileSource?.Edition ?? data.Source.Edition) == data.Source.Edition)
            .SelectMany(u => u.Profile?.Abilities ?? [])
            .Select(a => (WeaponRuleReference.Key(a.Name), a.Text.Trim())).ToHashSet();
        var enhancements = cards.Where(c => c.Section == "Enhancements")
            .GroupBy(c => (Faction(c), Detachment: UnitIdentity.Normalize(c.Detachment)))
            .ToDictionary(g => g.Key, g => g.ToList());
        bool Obsolete(ReferenceCard card)
        {
            if (!BsData(card)) return false;
            if (card.Section == "Army Rules" && card.Detachment.Length == 0)
                return scoped.Contains((Faction(card), UnitIdentity.Normalize(card.Name), card.Text.Trim()));
            if (card.Section != "Enhancements" || Enhancements.HasKnownRestriction(card)) return false;
            var peers = enhancements[(Faction(card), UnitIdentity.Normalize(card.Detachment))];
            // Keep the full supplemental upgrade and resolve an old saved spelling via
            // Enhancements.Key. Do not rewrite any saved entry or select a replacement.
            if (peers.Any(p => p != card && p.Cost == card.Cost && Enhancements.Upgrade(p)
                && Enhancements.HasKnownRestriction(p) && Enhancements.Key(p) == Enhancements.Key(card))) return true;
            // Exact imported ability text plus a same-cost owner in this detachment
            // identifies the old linked-keyword card. A keyword-like name alone does not.
            return abilities.Contains((WeaponRuleReference.Key(card.Name), card.Text.Trim()))
                && peers.Any(p => p != card && p.Cost == card.Cost && Enhancements.Key(p) != Enhancements.Key(card)
                    && UnitIdentity.Normalize(p.Text).Contains(UnitIdentity.Normalize(card.Name), StringComparison.Ordinal));
        }
        var cleaned = cards.Where(c => !Obsolete(c)).ToList();
        return cleaned.Count == cards.Count ? data : data with { ReferenceCards = cleaned };
    }
}
