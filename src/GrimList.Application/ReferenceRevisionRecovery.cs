using GrimList.Core;

namespace GrimList.Application;

public static class ReferenceRevisionRecovery
{
    // Infer only the compatibility of the exact shipped source revision. Manual
    // cards, newer imports, explicit packs and custom catalogue identities survive.
    public static Dataset CheckLegacy(Dataset current, Dataset bundled, IReadOnlyList<ReferenceCatalogueBaseline>? baselines = null)
    {
        if (current.IsScoped) return current;
        // Canonicalize at most once per library, not once per coverage scope.
        var suppliedCards = new Lazy<List<ReferenceCard>>(() => (bundled.ReferenceCards ?? []).Select(card => ReferenceModel.Canonical(card, bundled.Source)).ToList());
        var installedCards = new Lazy<List<ReferenceCard>>(() => (current.ReferenceCards ?? []).Select(card => ReferenceModel.Canonical(card, current.Source)).ToList());
        baselines ??= ReferenceCatalogueBaseline.Bundled;
        var repaired = new HashSet<string>();
        var coverage = (current.ReferenceCoverage ?? []).Select(c =>
        {
            if (c.CatalogueFingerprint != null && !c.CatalogueFingerprint.StartsWith("shape:", StringComparison.Ordinal)
                || c.Source?.ContentHash is not { Length: > 0 } hash
                || !(current.DetachmentSources ?? []).Any(s => s.Faction == c.Faction && (bundled.Detachments ?? []).Any(d => d.Faction == c.Faction && d.FactionId == s.CatalogueId))
                || !(bundled.ReferenceCoverage ?? []).Any(b => b.SourceId == c.SourceId && b.Source?.ContentHash == hash
                    && b.Faction == c.Faction && b.Detachment == c.Detachment)
                || (current.RulesPacks ?? []).Any(p => RulesPacks.OwnsDetachment(p, new(c.Faction, c.Detachment)))) return c;
            var supplied = suppliedCards.Value
                .Where(card => ReferenceMerge.OwnedScope(card, c)).ToList();
            var installed = installedCards.Value
                .Where(card => ReferenceMerge.OwnedScope(card, c) && card.Details!.Status is "Active" or "Stale" or "Unresolved").ToList();
            if (installed.Any(card => card.Source?.ContentHash != hash || !supplied.Any(b => b.Details!.Id == card.Details!.Id
                && ReferenceModel.ContentHash(b) == ReferenceModel.ContentHash(card)))) return c;
            var proof = baselines.SingleOrDefault(b => b.CoverageKey == ReferenceCatalogueBaseline.Key(c) && b.ReferenceHash == hash);
            if (proof == null || !(bundled.Detachments ?? []).Any(d => d.Faction == c.Faction && d.Name == c.Detachment && d.Source?.ContentHash == proof.CatalogueHash)) return c;
            var next = c with { CatalogueFingerprint = proof.Fingerprint };
            if (ReferenceCatalogueCompatibility.Changed(next, current)) return next;
            // Undo only our old faction-wide quarantine of unchanged, source-proven
            // cards. Real matching failures, manual content and tombstones survive.
            if (c.CatalogueFingerprint?.StartsWith("shape:", StringComparison.Ordinal) == true
                && c.ReviewReason is ReferenceCatalogueCompatibility.LegacyReviewReason or ReferenceCatalogueCompatibility.ReviewReason)
            {
                foreach (var card in installed.Where(card => card.Details!.Match is not ("Unresolved" or "Ambiguous"))) repaired.Add(card.Details!.Id);
                var complete = installed.Count == supplied.Count && installed.All(card => repaired.Contains(card.Details!.Id));
                next = next with { State = complete ? "Complete" : "Partial", Mode = complete ? "CompleteForDetachment" : "Partial",
                    Authoritative = complete, ReviewReason = complete ? null : "Previous reference coverage is incomplete; refresh references." };
            }
            return next;
        }).ToList();
        var next = coverage.SequenceEqual(current.ReferenceCoverage ?? []) ? current : current with { ReferenceCoverage = coverage };
        if (repaired.Count > 0) next = next with { ReferenceCards = (next.ReferenceCards ?? []).Select(c => c.Details != null && repaired.Contains(c.Details.Id)
            && c.Details.Status is "Unresolved" or "Stale" ? c with { Details = c.Details with { Status = "Active" } } : c).ToList() };
        return ReferenceCatalogueCompatibility.Apply(next);
    }
}
