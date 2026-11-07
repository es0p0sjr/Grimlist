using System.Collections.Frozen;

namespace GrimList.Core;

// Immutable projection tied to a DatasetSnapshot, never invalidated by roster edits.
public sealed class ReferenceIndex
{
    private readonly Dataset dataset;
    private sealed record Indexed(ReferenceCard Card, string Edition, string Faction, string Scope,
        FrozenSet<string> Detachments, string LogicalKey);
    private readonly FrozenDictionary<string, Indexed[]> candidates;
    public IReadOnlyList<ReferenceCard> All { get; }
    public FrozenDictionary<string, IReadOnlyList<ReferenceCard>> ByFaction { get; }
    private readonly Lazy<ReferenceCoverageReport> coverage;
    // Full-library coverage is a diagnostics view, not needed for unit editing or
    // per-army reference lookup. Build it only when that view is requested.
    public ReferenceCoverageReport Coverage => coverage.Value;
    public ReferenceIndex(Dataset dataset)
    {
        this.dataset = dataset;
        var normalized = ReferenceData.Normalize(dataset);
        var rows = (normalized.ReferenceCards ?? []).Select(c => ReferenceMatching.Match(ReferenceModel.Canonical(c, dataset.Source), dataset))
            .Select(c => new Indexed(c, c.Details!.Edition, ReferenceModel.Key(c.Faction), c.Details.Scope,
                (c.Details.Detachments ?? []).Select(t => ReferenceModel.Key(t.Name)).ToFrozenSet(), ReferenceModel.LogicalKey(c))).ToArray();
        All = Array.AsReadOnly(rows.Select(r => r.Card).ToArray());
        candidates = rows.Where(r => r.Card.Details!.Status is "Active" or "Stale" && r.Card.Details.Match is not ("Unresolved" or "Ambiguous"))
            .GroupBy(r => r.Edition + "|" + (r.Scope == "Global" ? "*" : r.Faction)).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        ByFaction = All.GroupBy(c => ReferenceModel.Key(c.Faction))
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<ReferenceCard>)Array.AsReadOnly(g.ToArray()));
        coverage = new(() => ReferenceCoverageAudit.Build(normalized, this));
    }
    public List<ReferenceCard> ForArmy(ArmyList army, string edition, bool previous = false)
    {
        var selected = DetachmentRules.Selected(army).Where(n => !DetachmentLifecycle.IsObsolete(dataset, army.Faction, n))
            .Select(n => ReferenceModel.Key(DetachmentLifecycle.ResolveName(dataset, army.Faction, n))).ToHashSet();
        List<ReferenceCard> result = []; HashSet<string> seen = [];
        foreach (var key in new[] { edition + "|*", edition + "|" + ReferenceModel.Key(army.Faction) })
            foreach (var row in candidates.GetValueOrDefault(key) ?? [])
                if (row.Card.Details!.Status == (previous ? "Stale" : "Active")
                    && (row.Scope != "Detachment" || row.Detachments.Overlaps(selected))
                    && (row.Card.Applicability == null || DetachmentRules.Applicable(army,
                        new(row.Card.Faction, "", Applicability: row.Card.Applicability), dataset))
                    && seen.Add(row.LogicalKey)) result.Add(row.Card);
        return result;
    }
}

public record DetachmentReferenceCoverage(string Detachment, int StratagemCount, string Coverage, List<SourceInfo> Sources,
    List<string> Missing, List<string> Unresolved, int PreviousCount = 0);
public record FactionReferenceCoverage(string Faction, List<DetachmentReferenceCoverage> Detachments);
public record ReferenceCoverageReport(string Edition, List<FactionReferenceCoverage> Factions, List<string> Unresolved);

public static class ReferenceCoverageAudit
{
    public static ReferenceCoverageReport Build(Dataset data) => DatasetSnapshot.For(data).References.Coverage;
    internal static ReferenceCoverageReport Build(Dataset data, ReferenceIndex index)
    {
        var factions = data.Units.Select(u => u.Faction).Concat((data.Detachments ?? []).Select(d => d.Faction)).Distinct().Order();
        return new(data.Source.Edition, factions.Select(f => new FactionReferenceCoverage(f,
            (data.Detachments ?? []).Where(d => d.Faction == f).Select(d => d.Name).Distinct().Order().Select(d => ForDetachment(data, index, f, d)).ToList())).ToList(),
            index.All.Where(c => c.Details!.Status == "Unresolved" || c.Details.Match is "Unresolved" or "Ambiguous")
                .Select(c => $"{c.Faction} / {c.Detachment} / {c.Name}: {c.Details!.Match} ({c.Details.SourceId})").ToList());
    }
    public static DetachmentReferenceCoverage ForDetachment(Dataset data, ReferenceIndex index, string faction, string detachment)
    {
        var army = ArmyList.New(faction) with { Detachment = detachment };
        var cards = index.ForArmy(army, data.Source.Edition).Where(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c)).ToList();
        var previous = index.ForArmy(army, data.Source.Edition, previous: true).Count(c => c.Section == "Stratagems" && !ArmyReferences.IsCore(c));
        var declarations = (data.ReferenceCoverage ?? []).Where(c => c.Edition == data.Source.Edition && c.Category == "Stratagems"
            && (c.Faction.Length == 0 || ReferenceModel.Key(c.Faction) == ReferenceModel.Key(faction))
            && (c.Detachment.Length == 0 || ReferenceModel.Key(c.Detachment) == ReferenceModel.Key(detachment))).ToList();
        var unresolved = (index.ByFaction.GetValueOrDefault(ReferenceModel.Key(faction)) ?? [])
            .Where(c => c.Details!.Edition == data.Source.Edition && c.Section == "Stratagems"
                && (c.Details.Status == "Unresolved" || c.Details.Match is "Unresolved" or "Ambiguous")
                && (c.Details.Scope != "Detachment" || (c.Details.Detachments ?? []).Any(t =>
                    ReferenceModel.Key(DetachmentLifecycle.ResolveName(data, faction, t.Name)) == ReferenceModel.Key(detachment))
                    || c.Details.Detachments is not { Count: > 0 } && (c.Detachment.Length == 0 || ReferenceModel.Key(c.Detachment) == ReferenceModel.Key(detachment))))
            .Select(c => c.Name + " [" + c.Details!.Match + "]").ToList();
        var state = unresolved.Count > 0 || previous > 0 ? "Partial"
            : declarations.Any(c => c.State == "VerifiedComplete" && c.Authoritative) ? "VerifiedComplete"
            : declarations.Any(c => c.State == "Complete" && c.Authoritative) ? "Complete"
            : cards.Count > 0 || declarations.Any(c => c.State == "Partial") ? "Partial" : "Unknown";
        return new(detachment, cards.Count, state, cards.Select(c => c.Source!).Distinct().ToList(),
            state is "Complete" or "VerifiedComplete" ? [] : declarations.Where(c => !string.IsNullOrWhiteSpace(c.ReviewReason))
                .Select(c => c.ReviewReason!).Append(previous > 0 ? $"{previous} previous reference cards remain available for review; they are not verified current rules."
                    : cards.Count == 0 ? "Current stratagem references are missing. Use Update data or import a verified reference pack."
                    : "No authoritative completeness declaration; missing card names/count unknown.").Distinct().ToList(), unresolved, previous);
    }
}
