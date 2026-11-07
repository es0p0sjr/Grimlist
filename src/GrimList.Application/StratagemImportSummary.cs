using GrimList.Core;

namespace GrimList.Application;

// Import-time reporting only. Visibility still comes from the shared ArmyReferences rules.
public sealed record StratagemImportSummary(string Message, IReadOnlyList<string> Coverage, IReadOnlyList<string> Warnings)
{
    public static StratagemImportSummary Create(Dataset incoming, Dataset merged, ImportLayer layer)
    {
        static bool IsDetachmentCard(ReferenceCard c) => !ArmyReferences.IsCore(c)
            && c.Section.Equals("Stratagems", StringComparison.OrdinalIgnoreCase);
        var supplied = (incoming.ReferenceCards ?? []).Concat((incoming.RulesPacks ?? []).SelectMany(p => p.Cards))
            .Where(IsDetachmentCard).ToList();
        var compatible = supplied.Where(c => string.IsNullOrWhiteSpace(c.Source?.Edition)
            || c.Source.Edition == merged.Source.Edition).ToList();
        var index = DatasetSnapshot.For(merged).References;
        var available = index.All.Where(IsDetachmentCard)
            .Where(c => c.Details!.Edition == merged.Source.Edition && c.Details.Status == "Active" && c.Details.Match is not ("Unresolved" or "Ambiguous")).ToList();
        var message = layer == ImportLayer.Points
            ? $"Detachment stratagems: {supplied.Count} in source; 0 included (Points only). Existing reference cards are preserved. Choose Datasheets only or Points + datasheets to include source references."
            : $"Detachment stratagems: {compatible.Count} matching-edition source cards included for merge; {available.Count} matching-edition cards in the resulting library. Existing matching cards are updated, not duplicated.";
        List<string> warnings = [];
        if (layer == ImportLayer.Points && supplied.Count > 0)
            warnings.Add($"Points only skips all {supplied.Count} source detachment stratagem cards. Select Datasheets only or Points + datasheets to import them.");
        if (layer != ImportLayer.Points && supplied.Count != compatible.Count)
            warnings.Add($"{supplied.Count - compatible.Count} source detachment stratagem cards belong to another edition and will not be shown for edition {merged.Source.Edition}.");
        if (layer != ImportLayer.Points && supplied.Count == 0 && incoming.RulesPacks is not { Count: > 0 })
            message += " This source supplies no detachment stratagem cards; existing references are retained.";
        var coverage = index.Coverage.Factions.SelectMany(f => f.Detachments.Select(d =>
            $"{f.Faction} / {d.Detachment}: {d.StratagemCount} stratagem cards · {d.Coverage}. {string.Join(" ", d.Missing)}"))
            .Concat(index.Coverage.Unresolved).ToList();
        return new(message, coverage, warnings);
    }
}
