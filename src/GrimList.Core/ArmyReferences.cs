namespace GrimList.Core;

public static class ArmyReferences
{
    // Shared reference display path for the army's edition, faction and detachments.
    // ReferenceIndex filters active/matched cards; this layer adds bundled core rules.
    // Preserve for attachment work: attaching units must not filter Stratagems through
    // LeaderRules or enhancement bearer eligibility. Core fallback and imported-card
    // precedence are independent of attachment permissions (StratagemTransferChecks).
    public static bool IsCore(ReferenceCard card) => card.Details?.Scope == "Global" || UnitIdentity.Normalize(card.Faction) == "CORE";

    public static List<ReferenceCard> PreviousForArmy(ArmyList army, Dataset? data) => data == null || data.Game != army.Game ? []
        : DatasetSnapshot.For(data).References.ForArmy(army, data.Source.Edition, previous: true).Where(c => c.Section == "Stratagems").ToList();

    public static List<ReferenceCard> ForArmy(ArmyList army, Dataset? data)
    {
        if (data == null || data.Game != army.Game) return [];
        var imported = DatasetSnapshot.For(data).References.ForArmy(army, data.Source.Edition);
        // A supplied core card overrides its bundled rule, without duplicating it.
        var dispositions = (data.Detachments ?? []).Where(d => UnitIdentity.Normalize(d.Faction) == UnitIdentity.Normalize(army.Faction)
            && DetachmentRules.Contains(army, d.Name, data)).ToList();
        imported = imported.Where(c => c.Section != "Dispositions" || dispositions.Any(d => d.DispositionOptions
                .Any(o => UnitIdentity.Normalize(o) == UnitIdentity.Normalize(c.Name))))
            .Select(c => c.Section != "Dispositions" ? c : c with { Text = "Available from:\n" + string.Join("\n", dispositions
                .Where(d => d.DispositionOptions.Any(o => UnitIdentity.Normalize(o) == UnitIdentity.Normalize(c.Name)))
                .Select(d => $"\u2022 {d.Name} ({d.Cost})")) }).ToList();
        return imported.Concat(army.Game == "Warhammer 40,000" ? CoreStratagems.ForEdition(data.Source.Edition) : [])
            .DistinctBy(c => (UnitIdentity.Normalize(c.Faction), UnitIdentity.Normalize(c.Section),
                UnitIdentity.Normalize(c.Detachment), UnitIdentity.Normalize(c.Name))).ToList();
    }

    public static List<string> MissingStratagemDetachments(ArmyList army, Dataset? data) => DetachmentRules.Selected(army)
        .Where(name => data == null || (DatasetSnapshot.For(data).References.ForArmy(DetachmentRules.WithSelection(army, [name]), data.Source.Edition)
            .All(c => IsCore(c) || !c.Section.Equals("Stratagems", StringComparison.OrdinalIgnoreCase))
            && ReferenceCoverageAudit.ForDetachment(data, DatasetSnapshot.For(data).References, army.Faction, name).Coverage
                is not ("Complete" or "VerifiedComplete"))).ToList();
}
